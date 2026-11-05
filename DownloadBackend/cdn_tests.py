"""Offline scheduling and proxy-split fixtures; no provider credentials."""
import io
import unittest
import urllib.error
import zipfile
from unittest.mock import patch

import bear_downloader as worker
import steam_cdn as cdn
import steam_network as network


class CDNTests(unittest.TestCase):
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
