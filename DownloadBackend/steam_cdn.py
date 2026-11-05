"""Steam content-directory discovery and scheduling from real chunk transfers."""
from dataclasses import dataclass
import atexit
import json
import os
from pathlib import Path
import re
import subprocess
import threading
import time
from urllib.parse import urlencode, urlsplit
import urllib.error
import requests


DIRECTORY = 'https://api.steampowered.com/IContentServerDirectoryService/GetServersForSteamPipe/v1/'


def steam_cell(steam):
    config = Path(steam)/'config/config.vdf'
    text = config.read_text(encoding='utf-8', errors='replace') if config.exists() else ''
    for name in ('CellIDServerOverride', 'CurrentCellID', 'CellID'):
        match = re.search(r'"'+name+r'"\s+"(\d+)"', text, re.I)
        if match and int(match.group(1)):
            return int(match.group(1))
    return 0


def discover(get, steam, appid):
    params = dict(cell_id=steam_cell(steam), max_servers=32, current_appid=appid)
    reply = json.loads(get(DIRECTORY+'?'+urlencode(params), timeout=6, retries=1))
    return directory_nodes(reply)


def directory_nodes(reply):
    nodes = []
    for server in reply['response']['servers']:
        if server['type'] not in ('CDN', 'SteamCache') or server.get('use_as_proxy'):
            continue
        host = server.get('vhost') or server['host']
        scheme = 'http' if server.get('https_support') == 'unavailable' else 'https'
        url = scheme+'://'+host
        if url not in nodes:
            nodes.append(url)
    return nodes


class CdnAuthorization:
    """One anonymous Steam session per download; tokens stay in memory."""
    def __init__(self, appid, steam, get, notify=lambda message: None):
        self.appid, self.steam, self.get, self.notify = appid, Path(steam), get, notify
        self.lock = threading.Lock()
        self.tokens = {}
        self.process = None
        self.unavailable = False
        atexit.register(self.close)

    def cached(self, depot, host):
        with self.lock:
            token, expires = self.tokens.get((depot, host), ('', 0))
            return token if expires > time.time()+30 else ''

    def token(self, depot, host):
        with self.lock:
            now = time.time()
            cached = self.tokens.get((depot, host))
            if cached and cached[1] > now+30:
                return cached[0]
            if self.unavailable:
                return ''
            try:
                reply = self.request(depot, host)
            except (OSError, ValueError, KeyError, urllib.error.URLError, requests.RequestException):
                self.unavailable = True
                self.notify('内容连接授权暂不可用，正在选择其他节点…')
                return ''
            token = reply.get('token', '') if reply.get('ok') else ''
            self.tokens[depot, host] = (token, reply.get('expires', now+120) if token else now+120)
            return token

    def request(self, depot, host):
        request = dict(app=self.appid, depot=depot, host=host)
        if self.process is None:
            executable = os.environ.get('BEAR_CDN_AUTH_EXE')
            if not executable:
                raise OSError('CDN session helper unavailable')
            self.notify('正在建立内容下载连接授权…')
            log = self.steam/'logs/connection_log.txt'
            text = log.read_text(encoding='utf-8', errors='replace') if log.exists() else ''
            servers = list(dict.fromkeys(re.findall(r'\b(?:\d{1,3}\.){3}\d{1,3}:27017\b', text)))[-8:]
            if not servers:
                directory = json.loads(self.get('https://api.steampowered.com/ISteamDirectory/GetCMList/v1/?cellid='+str(steam_cell(self.steam)), timeout=6, retries=1))
                servers = directory['response']['serverlist'][:8]
            request['servers'] = servers
            self.process = subprocess.Popen([executable, '--cdn-auth', str(steam_cell(self.steam))],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                text=True, encoding='utf-8', creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        self.process.stdin.write(json.dumps(request)+'\n')
        self.process.stdin.flush()
        return json.loads(self.process.stdout.readline())

    def close(self):
        if self.process is not None:
            self.process.stdin.close()
            try:
                self.process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
            self.process = None


class TransferMeter:
    def __init__(self):
        self.lock = threading.Lock()
        self.total = 0

    def add(self, count):
        with self.lock:
            self.total += count

    def read(self):
        with self.lock:
            return self.total


@dataclass
class Node:
    url: str
    preferred: bool
    rate: float = 0
    active: int = 0
    retry_at: float = 0
    samples: int = 0


class ContentPool:
    def __init__(self, preferred, fallback, get, clock=time.monotonic, authorization=None):
        urls = list(dict.fromkeys(preferred+fallback))
        self.nodes = [Node(url, url in preferred) for url in urls]
        self.get = get
        self.clock = clock
        self.authorization = authorization
        self.changed = threading.Condition()

    def acquire(self, excluded):
        with self.changed:
            while True:
                available = [n for n in self.nodes if n.url not in excluded and n.retry_at <= self.clock()]
                if not available:
                    return None
                preferred = [n for n in available if n.preferred]
                free = [n for n in (preferred or available) if n.active < 8]
                if free:
                    def score(node):
                        rate = node.rate if node.samples else (2*1048576 if node.preferred else 524288)
                        return rate/(node.active+1)
                    node = max(free, key=score)
                    node.active += 1
                    return node
                self.changed.wait(0.1)

    def release(self, node, size, elapsed, success):
        with self.changed:
            node.active -= 1
            if success:
                rate = size/max(elapsed, 0.001)
                node.rate = rate if not node.samples else node.rate*0.7+rate*0.3
                node.samples += 1
                node.retry_at = 0
            else:
                node.retry_at = self.clock()+20
            self.changed.notify_all()

    def fetch(self, depot, sha, timeout=12):
        tried = set()
        last_error = None
        while True:
            node = self.acquire(tried)
            if node is None:
                if last_error is not None:
                    raise last_error
                raise RuntimeError('内容节点暂时不可用，请稍后继续下载。')
            tried.add(node.url)
            started = self.clock()
            try:
                base = '%s/depot/%d/chunk/%s' % (node.url, depot, sha)
                host = urlsplit(node.url).hostname
                token = self.authorization.cached(depot, host) if self.authorization else ''
                fastest = max((n.rate for n in self.nodes if n.samples), default=0)
                options = dict(timeout=min(timeout, 8), retries=1, raw=True,
                               minimum_rate=min(fastest*0.2, 262144))
                try:
                    data = self.get(base+('?' + token.lstrip('?') if token else ''), **options)
                except urllib.error.HTTPError as error:
                    if error.code != 403 or self.authorization is None:
                        raise
                    authorized = self.authorization.token(depot, host)
                    if not authorized or authorized == token:
                        raise
                    started = self.clock()
                    data = self.get(base+'?'+authorized.lstrip('?'), **options)
            except Exception as error:
                self.release(node, 0, self.clock()-started, False)
                last_error = error
            else:
                self.release(node, len(data), self.clock()-started, True)
                return data, node.url


class AdaptiveConcurrency:
    """Try a larger window, keep it only when useful, then let it settle."""
    def __init__(self, jobs, clock=time.monotonic):
        self.automatic = jobs == 0
        self.limit = 8 if self.automatic else jobs
        self.maximum = 32 if self.automatic else jobs
        self.clock = clock
        self.started = clock()
        self.previous_bytes = 0
        self.baseline = None
        self.previous_limit = self.limit
        self.next_sample = self.started+6

    def observe(self, received):
        now = self.clock()
        if not self.automatic or now < self.next_sample:
            return self.limit
        rate = (received-self.previous_bytes)/max(now-self.started, 0.001)
        self.previous_bytes = received
        self.started = now
        self.next_sample = now+6
        if rate <= 0:
            return self.limit
        if self.baseline is not None and rate < self.baseline*1.08:
            self.limit = self.previous_limit
            self.baseline = None
            self.next_sample = now+24
        elif self.limit < self.maximum:
            self.baseline = rate
            self.previous_limit = self.limit
            self.limit = min(self.maximum, self.limit+4)
        else:
            self.baseline = None
            self.next_sample = now+24
        return self.limit
