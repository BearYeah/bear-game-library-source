"""Steam content-directory discovery and scheduling from real chunk transfers."""
from dataclasses import dataclass
import json
from pathlib import Path
import re
import threading
import time
from urllib.parse import urlencode


DIRECTORY = 'https://api.steampowered.com/IContentServerDirectoryService/GetServersForSteamPipe/v1/'


def discover(get, steam, appid):
    config = Path(steam)/'config/config.vdf'
    match = re.search(r'"CellID"\s+"(\d+)"', config.read_text(encoding='utf-8', errors='replace'), re.I) if config.exists() else None
    params = dict(cell_id=int(match.group(1)) if match else 0, max_servers=32, current_appid=appid)
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
    def __init__(self, preferred, fallback, get, clock=time.monotonic):
        urls = list(dict.fromkeys(preferred+fallback))
        self.nodes = [Node(url, url in preferred) for url in urls]
        self.get = get
        self.clock = clock
        self.changed = threading.Condition()

    def acquire(self, excluded):
        with self.changed:
            while True:
                available = [n for n in self.nodes if n.url not in excluded and n.retry_at <= self.clock()]
                if not available:
                    return None
                free = [n for n in available if n.active < 8]
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
                data = self.get('%s/depot/%d/chunk/%s' % (node.url, depot, sha),
                                timeout=min(timeout, 8), retries=1, raw=True)
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
