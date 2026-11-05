# SPDX-License-Identifier: GPL-3.0-only
"""Offline fixtures for the bundled worker. Never contacts Steam or the network."""
import contextlib
import hashlib
import io
import json
from pathlib import Path
import struct
import tempfile
import unittest
import urllib.error
import base64
import hmac
import os
import time
import zipfile
from unittest.mock import patch
import zstandard
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
import bear_downloader as worker
CONFIG = '"InstallConfigStore"\n{\n\t"Software"\n\t{\n\t\t"Valve"\n\t\t{\n\t\t\t"Steam"\n\t\t\t{\n\t\t\t\t"Other" "keep this"\n\t\t\t}\n\t\t}\n\t}\n}\n'

def chunk(body, offset=0):
    return dict(sha=hashlib.sha1(body).hexdigest(), offset=offset, cb_orig=len(body))

def file(name, body, flags=0):
    return dict(path=name, size=len(body), flags=flags, chunks=[chunk(body)] if body else [])

class DownloadTests(unittest.TestCase):

    def setUp(self):
        self.server_env = patch.dict(os.environ, BEAR_DOWNLOAD_SERVER='')
        self.server_env.start()
        self.temporary = tempfile.TemporaryDirectory(dir=TEST_ROOT)
        self.root = Path(self.temporary.name).resolve()
        self.assertTrue(self.root.is_relative_to(TEST_ROOT))
        self.steam = self.root / 'Steam 配置'
        self.library = self.root / '第二个游戏库'
        (self.steam / 'config').mkdir(parents=True)
        (self.steam / 'steamapps').mkdir()
        (self.steam / 'config/config.vdf').write_text(CONFIG, encoding='utf-8')
        self.job = dict(appid=123, name='Fixture', steam=str(self.steam), library=str(self.library), language='schinese')

    def tearDown(self):
        self.server_env.stop()
        self.temporary.cleanup()

    def plan(self):
        return dict(appid=123, name='Fixture 中文', installdir='Fixture 游戏', buildid='99', language='schinese', destination=(self.library / 'steamapps/common/Fixture 游戏').resolve(), depots={11: dict(gid=101, key='aa' * 32, blob=b'manifest-base', manifest=dict(total_size=4, files=[file('shared.dat', b'base'), file('empty.txt', b'')])), 12: dict(gid=102, key='bb' * 32, blob=b'manifest-language', manifest=dict(total_size=8, files=[file('shared.dat', b'language')]))})

    def test_install_and_resume_preserve_overlay_and_other_settings(self):
        plan = self.plan()
        data = {hashlib.sha1(body).hexdigest(): body for body in (b'base', b'language')}
        checkpoint = self.root / 'state/chunks.json'
        with patch.object(worker.core, 'fetch_chunk', side_effect=lambda d, sha, code, key: (data[sha], 'fixture')):
            worker.download_plan(plan, checkpoint, 2)
        with patch.object(worker.core, 'fetch_chunk', side_effect=AssertionError('completed chunk was downloaded again')):
            worker.download_plan(plan, checkpoint, 2)
        self.assertEqual((plan['destination'] / 'shared.dat').read_bytes(), b'language')
        self.assertEqual((plan['destination'] / 'empty.txt').read_bytes(), b'')
        worker.register_install(self.job, plan)
        acf, _ = worker.read_vdf((self.library / 'steamapps/appmanifest_123.acf').read_text(encoding='utf-8'))
        self.assertEqual(acf['AppState']['StateFlags'], '4')
        self.assertEqual(set(acf['AppState']['InstalledDepots']), {'11', '12'})
        cfg, _ = worker.read_vdf((self.steam / 'config/config.vdf').read_text(encoding='utf-8'))
        self.assertEqual(worker.steam_node(cfg)['Other'], 'keep this')
        self.assertEqual(worker.steam_node(cfg)['depots']['12']['DecryptionKey'], 'bb' * 32)
        registry, _ = worker.read_vdf((self.steam / 'steamapps/libraryfolders.vdf').read_text(encoding='utf-8'))
        self.assertTrue(any((row.get('apps', {}).get('123') == '12' for row in registry['libraryfolders'].values())))
        self.assertEqual((self.steam / 'depotcache/11_101.manifest').read_bytes(), b'manifest-base')

    def test_config_backups_keep_five_newest(self):
        config = self.steam / 'config/config.vdf'
        for i in range(8):
            old = config.with_name(config.name + '.bear-download-old-%02d.bak' % i)
            old.write_text('old backup', encoding='utf-8')
            import os
            os.utime(old, (1700000000 + i, 1700000000 + i))
        unrelated = config.with_name(config.name + '.other.bak')
        unrelated.write_text('keep', encoding='utf-8')
        worker.merge_keys(config, {11: 'aa' * 32})
        backups = list(config.parent.glob(config.name + '.bear-download-*.bak'))
        self.assertEqual(len(backups), 5)
        self.assertFalse(config.with_name(config.name + '.bear-download-old-03.bak').exists())
        self.assertTrue(config.with_name(config.name + '.bear-download-old-07.bak').exists())
        self.assertEqual(unrelated.read_text(encoding='utf-8'), 'keep')
        current, _ = worker.read_vdf(config.read_text(encoding='utf-8'))
        self.assertEqual(worker.steam_node(current)['depots']['11']['DecryptionKey'], 'aa' * 32)

    def test_bad_chunk_does_not_register_installed(self):
        plan = self.plan()
        with patch.object(worker.core, 'fetch_chunk', return_value=(b'wrong', 'fixture')):
            with self.assertRaises(RuntimeError):
                worker.download_plan(plan, self.root / 'chunks.json', 2)
        self.assertFalse((self.library / 'steamapps/appmanifest_123.acf').exists())
        self.assertEqual((self.steam / 'config/config.vdf').read_text(encoding='utf-8'), CONFIG)

    def test_windows_and_language_selection(self):

        def row(platform='', language='', dlc=False):
            result = dict(config=dict(oslist=platform, language=language), manifests=dict(public=dict(gid='1')))
            if dlc:
                result['dlcappid'] = '999'
            return result
        info = dict(depots={'1': row(), '2': row('windows', 'schinese'), '3': row('windows', 'english'), '4': row('linux'), '5': row('macos'), '6': row('windows', dlc=True)})
        self.assertEqual(set(worker.select_depots(info, 'schinese')), {1, 2})
        self.assertEqual(set(worker.select_depots(info, 'japanese')), {1, 3})

    def test_real_chunk_crypto_and_vzstd(self):
        key = bytes(range(32))
        iv = bytes(reversed(range(16)))
        body = b'fixture chunk data' * 30
        wrapped = b'VSZa' + b'\x00' * 4 + zstandard.ZstdCompressor().compress(body) + b'\x00' * 4 + struct.pack('<i', len(body)) + b'\x00' * 7
        wrapped += bytes([16 - len(wrapped) % 16]) * (16 - len(wrapped) % 16)
        encrypt_iv = Cipher(algorithms.AES(key), modes.ECB()).encryptor()
        encrypt = Cipher(algorithms.AES(key), modes.CBC(iv)).encryptor()
        ciphertext = encrypt_iv.update(iv) + encrypt_iv.finalize() + encrypt.update(wrapped) + encrypt.finalize()
        self.assertEqual(worker.core._vz_decompress(worker.core._symmetric_decrypt(key, ciphertext)), body)

    def test_public_missing_keys_uses_server_supplement_once_for_all_depots(self):
        info = dict(common=dict(name='Fixture'), depots={str(d): {'manifests': {'public': {'gid': str(100 + d)}}} for d in (11, 12)})
        self.job['cache'] = str(self.root / 'downloads/cache')
        cache = unittest.mock.Mock()
        cache.resolve.return_value = dict(status='missing', info=info)
        cache.prepare.return_value = dict(status='ready', info=info, keys={'11': 'ab' * 32, '12': 'ab' * 32}, manifests={'11': '111', '12': '112'})
        blob = struct.pack('<IIII', worker.core.MAGIC, 0, 524817086, 0)
        cache.manifest.return_value = blob
        order = []
        cache.prepare.side_effect = lambda *a: order.append('supplement') or cache.prepare.return_value
        with patch.dict(os.environ, BEAR_DOWNLOAD_SERVER='https://cache.fixture'), patch.object(worker, 'BearCache', return_value=cache), patch.object(worker.core, 'fetch_lua', side_effect=lambda *a, **k: order.append('lua') or (None, None)), patch.object(worker.core, 'fetch_keyvdf', side_effect=lambda *a, **k: order.append('keyvdf') or (None, None)), patch.object(worker.core, 'manifest_cache_path', side_effect=lambda d, g: str(self.root / ('%d_%d.manifest' % (d, g)))):
            plan = worker.resolve_plan(self.job)
        self.assertEqual(order, ['lua', 'keyvdf', 'supplement'])
        cache.prepare.assert_called_once()
        self.assertEqual(set(plan['depots']), {11, 12})
        self.assertEqual(cache.manifest.call_count, 2)

    def test_public_missing_manifests_use_one_background_supplement(self):
        info = dict(common=dict(name='Fixture'), depots={str(d): {'manifests': {'public': {'gid': str(100 + d)}}} for d in (11, 12)})
        local = worker.vdf_body({'InstallConfigStore': {'Software': {'Valve': {'Steam': {'depots': {'11': {'DecryptionKey': 'ab' * 32}, '12': {'DecryptionKey': 'ab' * 32}}}}}}}, 0)
        (self.steam / 'config/config.vdf').write_text(local, encoding='utf-8')
        self.job['cache'] = str(self.root / 'downloads/cache')
        cache = unittest.mock.Mock()
        cache.resolve.return_value = dict(status='missing', info=info)
        cache.prepare.return_value = dict(status='ready', info=info, keys={'11': 'ab' * 32, '12': 'ab' * 32}, manifests={'11': '111', '12': '112'})
        cache.manifest.return_value = struct.pack('<IIII', worker.core.MAGIC, 0, 524817086, 0)
        missing = urllib.error.HTTPError('https://fixture', 404, 'missing', {}, None)
        with patch.dict(os.environ, BEAR_DOWNLOAD_SERVER='https://cache.fixture'), patch.object(worker, 'BearCache', return_value=cache), patch.object(worker.core, 'http_get', side_effect=missing) as public, patch.object(worker.core, 'manifest_cache_path', side_effect=lambda d, g: str(self.root / ('%d_%d.manifest' % (d, g)))), patch.object(worker.core, 'request_code', side_effect=AssertionError('supplement already has manifests')):
            plan = worker.resolve_plan(self.job)
        self.assertEqual(public.call_count, 0)
        cache.prepare.assert_called_once()
        self.assertEqual(set(plan['depots']), {11, 12})

    def test_missing_keys_stops_before_files_are_written(self):
        info = {'data': {'123': {'common': {'name': 'Fixture'}, 'depots': {'11': {'manifests': {'public': {'gid': '101'}}}}}}}
        (self.root / '123.info.json').write_text(json.dumps(info['data']['123']), encoding='utf-8')
        with patch.dict(os.environ, BEAR_METADATA_DIR=str(self.root)), patch.object(worker.core, 'fetch_lua', return_value=(None, None)), patch.object(worker.core, 'fetch_keyvdf', return_value=(None, None)):
            with self.assertRaisesRegex(RuntimeError, '11'):
                worker.resolve_plan(self.job)
        self.assertFalse(self.library.exists())

    def test_bear_server_hit_does_not_query_other_channels(self):
        info = {'common': {'name': 'Fixture'}, 'depots': {'11': {'manifests': {'public': {'gid': '101'}}}}}
        blob = struct.pack('<IIII', worker.core.MAGIC, 0, 524817086, 0)
        self.job['cache'] = str(self.root / 'downloads/cache')
        reply = dict(status='ready', info=info, keys={'11': 'ab' * 32}, manifests={'11': '101'}, quota=dict(used=1, limit=10))
        cache = unittest.mock.Mock()
        cache.resolve.return_value = reply
        cache.manifest.return_value = blob
        with patch.dict(os.environ, BEAR_DOWNLOAD_SERVER='https://cache.fixture'), patch.object(worker, 'BearCache', return_value=cache), patch.object(worker.core, 'manifest_cache_path', return_value=str(self.root / '11_101.manifest')), patch.object(worker.core, 'http_get', side_effect=AssertionError('unnecessary upstream query')):
            result = worker.resolve_plan(self.job)
        self.assertEqual(result['depots'][11]['source'], 'remote')
        cache.manifest.assert_called_once_with(123, 11, 101)
        cache.finish.assert_not_called()

    def test_bear_daily_limit_stops_without_channel_fallback(self):
        self.job['cache'] = str(self.root / 'downloads/cache')
        cache = unittest.mock.Mock()
        cache.resolve.side_effect = worker.DailyLimit('daily limit')
        with patch.dict(os.environ, BEAR_DOWNLOAD_SERVER='https://cache.fixture'), patch.object(worker, 'BearCache', return_value=cache), patch.object(worker.core, 'http_get', side_effect=AssertionError('quota bypass')):
            with self.assertRaises(worker.DailyLimit):
                worker.resolve_plan(self.job)

def run(output):
    global TEST_ROOT
    TEST_ROOT = Path(output).resolve()
    TEST_ROOT.mkdir(parents=True, exist_ok=True)
    stream = io.StringIO()
    with contextlib.redirect_stdout(io.StringIO()):
        result = unittest.TextTestRunner(stream=stream).run(unittest.defaultTestLoader.loadTestsFromTestCase(DownloadTests))
    report = dict(passed=result.testsRun - len(result.failures) - len(result.errors), total=result.testsRun, result='pass' if result.wasSuccessful() else 'fail', scope='offline fixtures; no network, real Steam files or account access', details=stream.getvalue())
    (TEST_ROOT / 'download-tests.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(report, ensure_ascii=False), flush=True)
    return 0 if result.wasSuccessful() else 1
