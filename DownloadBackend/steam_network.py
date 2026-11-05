"""Reuse HTTP connections and apply Steam-only address hints without changing TLS identity."""
import json
from pathlib import Path
import threading
from urllib.parse import urlsplit

import requests
from requests.adapters import HTTPAdapter
from requests.utils import get_environ_proxies, select_proxy
from urllib3 import PoolManager
from urllib3.connection import HTTPSConnection
from urllib3.connectionpool import HTTPSConnectionPool
from urllib3.util.connection import create_connection


def steam_host(host):
    return host == 'steamcdn-a.akamaihd.net' or host.endswith(('.steamstatic.com', '.steamcontent.com')) or host in (
        'store.steampowered.com', 'api.steampowered.com', 'steamcommunity.com', 'www.steamcommunity.com')


class AddressAdapter(HTTPAdapter):
    def __init__(self, address, suppress_sni=False):
        self.address = address
        self.suppress_sni = suppress_sni
        super().__init__(max_retries=0)

    def init_poolmanager(self, connections, maxsize, block=False, **kwargs):
        address = self.address
        suppress_sni = self.suppress_sni

        class AddressConnection(HTTPSConnection):
            def __init__(self, *args, **options):
                super().__init__(*args, **options)
                if suppress_sni:
                    self.server_hostname = address or '0.0.0.0'
                    self.assert_hostname = self.host

            def _new_conn(self):
                # The URL host is always retained for HTTP and certificate identity.
                if address is None:
                    return super()._new_conn()
                return create_connection((address, self.port), self.timeout,
                                         source_address=self.source_address,
                                         socket_options=self.socket_options)

        class AddressPool(HTTPSConnectionPool):
            ConnectionCls = AddressConnection

        self.poolmanager = PoolManager(num_pools=connections, maxsize=maxsize,
                                       block=block, **kwargs)
        self.poolmanager.pool_classes_by_scheme = dict(self.poolmanager.pool_classes_by_scheme)
        self.poolmanager.pool_classes_by_scheme['https'] = AddressPool


class SteamHTTP:
    def __init__(self, route_file='', proxy='', user_agent='BearLibrary', local_sni=True, direct=False):
        self.proxy = proxy
        self.user_agent = user_agent
        self.local = threading.local()
        self.routed_hosts = set()
        self.sni_hosts = set()
        self.sni_unavailable = set()
        self.local_sni = local_sni
        self.direct = direct
        self.routes = {}
        if route_file and Path(route_file).is_file():
            self.routes = json.loads(Path(route_file).read_text(encoding='utf-8'))['Hosts']

    def sessions(self):
        if not hasattr(self.local, 'normal'):
            self.local.normal = requests.Session()
            self.local.routed = requests.Session()
            self.local.sni = requests.Session()
            for session in (self.local.normal, self.local.routed, self.local.sni):
                session.headers['User-Agent'] = self.user_agent
                if self.direct:
                    session.trust_env = False
                if self.proxy:
                    session.proxies.update(http=self.proxy, https=self.proxy)
            self.local.sni.mount('https://', AddressAdapter(None, suppress_sni=True))
            for host, address in self.routes.items():
                if steam_host(host):
                    self.local.routed.mount('https://' + host + '/', AddressAdapter(address))
                    self.local.sni.mount('https://' + host + '/', AddressAdapter(address, suppress_sni=True))
        return self.local.normal, self.local.routed

    def get(self, url, timeout, stream=False):
        normal, routed = self.sessions()
        host = urlsplit(url).hostname
        direct = self.direct or (not self.proxy and not select_proxy(url, get_environ_proxies(url)))
        if direct and host in self.sni_hosts:
            return self.local.sni.get(url, timeout=timeout, stream=stream)
        if direct and host in self.routed_hosts:
            return routed.get(url, timeout=timeout, stream=stream)
        try:
            return normal.get(url, timeout=timeout, stream=stream)
        except (requests.ConnectionError, requests.Timeout):
            if not direct or urlsplit(url).scheme != 'https':
                raise
            if self.local_sni and steam_host(host) and host not in self.sni_unavailable:
                try:
                    response = self.local.sni.get(url, timeout=timeout, stream=stream)
                except (requests.ConnectionError, requests.Timeout):
                    self.sni_unavailable.add(host)
                else:
                    if response.ok:
                        self.sni_hosts.add(host)
                    return response
            if host not in self.routes:
                raise
            response = routed.get(url, timeout=timeout, stream=stream)
            if response.ok:
                self.routed_hosts.add(host)
            return response
