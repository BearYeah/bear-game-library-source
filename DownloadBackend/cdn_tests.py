"""Offline scheduling and proxy-split fixtures; no provider credentials."""
import io
import concurrent.futures
from pathlib import Path
import tempfile
import time
import unittest
import urllib.error
import zipfile
from unittest.mock import patch, Mock
import requests

import bear_downloader as worker
import steam_cdn as cdn
import steam_network as network


class CDNTests(unittest.TestCase):
    def test_cell_uses_steam_override_then_current_region(self):
        with tempfile.TemporaryDirectory() as folder:
            config = Path(folder)/'config/config.vdf'
            config.parent.mkdir()
            config.write_text('"CellIDServerOverride" "199"\n"CurrentCellID" "47"\n"CellID" "1"')
            self.assertEqual(cdn.steam_cell(folder), 199)
            config.write_text('"CellIDServerOverride" "0"\n"CurrentCellID" "47"')
            self.assertEqual(cdn.steam_cell(folder), 47)

    def test_403_gets_authorization_once_and_reuses_query_for_next_chunk(self):
        authorization = cdn.CdnAuthorization(10, '.', None)
        authorization.request = Mock(return_value=dict(ok=True, token='?ticket=fixture', expires=time.time()+3600))
        calls = []
        def get(url, **kwargs):
            calls.append(url)
            if '?' not in url:
                raise urllib.error.HTTPError(url, 403, 'Forbidden', {}, None)
            return b'content'
        pool = cdn.ContentPool(['http://content.invalid'], [], get, authorization=authorization)
        pool.fetch(11, 'one');pool.fetch(11, 'two')
        self.assertEqual(calls, ['http://content.invalid/depot/11/chunk/one',
            'http://content.invalid/depot/11/chunk/one?ticket=fixture',
            'http://content.invalid/depot/11/chunk/two?ticket=fixture'])
        authorization.request.assert_called_once_with(11, 'content.invalid')

    def test_parallel_403_requests_share_one_token_request(self):
        authorization = cdn.CdnAuthorization(10, '.', None)
        authorization.request = Mock(return_value=dict(ok=True, token='?ticket=fixture', expires=time.time()+3600))
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            results = list(pool.map(lambda _: authorization.token(11, 'content.invalid'), range(16)))
        self.assertEqual(set(results), {'?ticket=fixture'})
        self.assertEqual(authorization.request.call_count, 1)

    def test_denied_authorization_moves_to_other_node_without_repeated_login(self):
        authorization = cdn.CdnAuthorization(10, '.', None)
        authorization.request = Mock(return_value=dict(ok=False, reason='AccessDenied'))
        def get(url, **kwargs):
            if 'denied.invalid' in url:
                raise urllib.error.HTTPError(url, 403, 'Forbidden', {}, None)
            return b'content'
        pool = cdn.ContentPool(['http://denied.invalid', 'http://works.invalid'], [], get, authorization=authorization)
        self.assertEqual(pool.fetch(11, 'one')[1], 'http://works.invalid')
        self.assertEqual(authorization.token(11, 'denied.invalid'), '')
        self.assertEqual(authorization.request.call_count, 1)

    def test_slow_stream_releases_connection_for_other_nodes(self):
        response = Mock()
        response.status_code = 200
        response.iter_content.return_value = [b'x'*65536]
        transport = Mock()
        transport.get.return_value.__enter__ = Mock(return_value=response)
        transport.get.return_value.__exit__ = Mock(return_value=False)
        with patch.object(worker, 'SteamHTTP', return_value=transport):
            worker.configure_network('')
        with patch.object(worker.time, 'monotonic', side_effect=[0, 4]):
            with self.assertRaises(requests.Timeout):
                worker.content_get('http://content.invalid/depot/11/chunk/one', timeout=8, retries=1, minimum_rate=262144)

    def test_directory_preserves_http_only_nodes_and_uses_virtual_host(self):
        reply = dict(response=dict(servers=[
            dict(type='CDN', host='physical.invalid', vhost='content.invalid', https_support='unavailable'),
            dict(type='SteamCache', host='secure.invalid', https_support='mandatory'),
            dict(type='CDN', host='proxy.invalid', use_as_proxy=True),
            dict(type='CDN', host='content.invalid', https_support='unavailable')]))
        self.assertEqual(cdn.directory_nodes(reply), ['http://content.invalid', 'https://secure.invalid'])

    def test_fast_node_gets_more_work_and_connections_are_distributed(self):
        pool = cdn.ContentPool(['http://a.invalid', 'http://b.invalid'], [], None)
        a = pool.acquire(set())
        b = pool.acquire(set())
        self.assertNotEqual(a.url, b.url)
        pool.release(a, 1048576, 0.1, True)
        pool.release(b, 1048576, 2, True)
        selected = pool.acquire(set())
        self.assertEqual(selected.url, a.url)
        pool.release(selected, 1048576, 0.1, True)

    def test_failed_node_is_skipped_then_allowed_to_recover(self):
        now = [0.0]
        calls = []

        def get(url, **kwargs):
            calls.append(url)
            if url.startswith('http://a.invalid') and now[0] == 0:
                raise urllib.error.URLError('fixture unavailable')
            return b'body'

        pool = cdn.ContentPool(['http://a.invalid', 'http://b.invalid'], [], get, lambda: now[0])
        self.assertEqual(pool.fetch(1, 'one')[1], 'http://b.invalid')
        pool.fetch(1, 'two')
        self.assertEqual(sum(u.startswith('http://a.invalid') for u in calls), 1)
        now[0] = 21
        self.assertEqual(pool.fetch(1, 'three')[1], 'http://a.invalid')

    def test_small_files_do_not_push_healthy_preferred_nodes_to_fallback(self):
        pool = cdn.ContentPool(['http://local.invalid'], ['https://remote.invalid'], None)
        node = pool.acquire(set())
        pool.release(node, 32, 1, True)
        self.assertEqual(pool.acquire(set()).url, 'http://local.invalid')

    def test_official_http_chunk_passes_through_normal_unpacking(self):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, 'w') as archive:
            archive.writestr('z', b'fixture payload')
        urls = []
        pool = cdn.ContentPool(['http://content.invalid'], [],
                               lambda url, **kwargs: (urls.append(url) or stream.getvalue()))
        with patch.object(worker.core, 'chunk_fetch', pool.fetch):
            body, _ = worker.core.fetch_chunk(11, 'fixture', None, '')
        self.assertEqual(body, b'fixture payload')
        self.assertEqual(urls, ['http://content.invalid/depot/11/chunk/fixture'])

    def test_auto_concurrency_reverts_when_extra_connections_do_not_help(self):
        now = [0.0]
        controller = cdn.AdaptiveConcurrency(0, lambda: now[0])
        self.assertEqual(controller.limit, 8)
        now[0] = 6
        self.assertEqual(controller.observe(6*1048576), 12)
        now[0] = 12
        self.assertEqual(controller.observe(12*1048576), 8)
        now[0] = 18
        self.assertEqual(controller.observe(18*1048576), 8)
        fixed = cdn.AdaptiveConcurrency(24, lambda: now[0])
        now[0] = 60
        self.assertEqual(fixed.observe(100*1048576), 24)

    def test_direct_content_sessions_ignore_environment_proxies(self):
        direct = network.SteamHTTP(direct=True)
        normal, routed = direct.sessions()
        self.assertTrue(all(not s.trust_env for s in (normal, routed, direct.local.sni)))
        for session in (normal, routed, direct.local.sni):
            session.close()
        proxied = network.SteamHTTP(proxy='http://127.0.0.1:7890')
        normal, routed = proxied.sessions()
        self.assertEqual(normal.proxies['https'], 'http://127.0.0.1:7890')
        for session in (normal, routed, proxied.local.sni):
            session.close()


if __name__ == '__main__':
    unittest.main()
