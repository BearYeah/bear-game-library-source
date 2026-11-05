# SPDX-License-Identifier: GPL-3.0-only
"""DLC fixtures using configured inputs only; no network or real game data."""
import json
import os
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import Mock, patch

import bear_downloader as worker
from download_tests import CONFIG, file


class DLCTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='bear-public-dlc-')
        self.root = Path(self.temporary.name).resolve()
        self.inputs = self.root / 'inputs'
        self.inputs.mkdir()
        self.environment = patch.dict(os.environ, BEAR_DOWNLOAD_SERVER='', BEAR_METADATA_DIR=str(self.inputs))
        self.environment.start()
        self.events = patch.object(worker, 'emit')
        self.events.start()
        self.steam, self.library = self.root / 'Steam', self.root / 'Library'
        (self.steam / 'config').mkdir(parents=True)
        (self.steam / 'steamapps').mkdir()
        (self.steam / 'config/config.vdf').write_text(CONFIG, encoding='utf-8')
        self.installed = self.root / 'existing game outside library'
        self.installed.mkdir()
        self.job = dict(appid=123, name='Fixture', steam=str(self.steam), library=str(self.library),
                        language='schinese', cache=str(self.root / 'cache'),
                        state=str(self.root / 'task/chunks.json'), dlc_ids=[999],
                        dlc_only=True, install_dir=str(self.installed))
        self.blob = struct.pack('<IIII', worker.core.MAGIC, 0, 0x1F4812BE, 0)

    def tearDown(self):
        self.events.stop()
        self.environment.stop()
        self.temporary.cleanup()

    def info(self, include_dlc=True):
        depots = {'11':dict(manifests=dict(public=dict(gid='101')))}
        if include_dlc:
            depots['12'] = dict(dlcappid='999', manifests=dict(public=dict(gid='102')))
        return dict(common=dict(name='Fixture'), depots=depots)

    def cache(self, info=None):
        cache = Mock()
        cache.resolve.return_value = dict(status='ready', info=info or self.info(),
                                          keys={'11':'ab'*32, '12':'cd'*32},
                                          manifests={'11':'101', '12':'102'})
        cache.prepare.return_value = cache.resolve.return_value
        cache.manifest.return_value = self.blob
        return cache

    def manifest_path(self, depot, gid):
        return str(self.root / ('%d_%d.manifest' % (depot, gid)))

    def plan(self):
        return dict(appid=123, name='Fixture', installdir='Fixture', buildid='99', language='schinese',
                    destination=self.installed, dlc_ids=[999], dlc_only=True,
                    install_dir=str(self.installed), depots={
                        12:dict(gid=102, key='cd'*32, blob=self.blob, dlcappid=999,
                                manifest=dict(total_size=7, files=[file('shared.dat', b'new dlc')]))})

    def test_selection_keeps_base_and_uses_each_dlc_language_fallback(self):
        def depot(language='', owner=None):
            row = dict(config=dict(oslist='windows', language=language), manifests=dict(public=dict(gid='101')))
            if owner:
                row['dlcappid'] = str(owner)
            return row
        info = dict(depots={'11':depot(), '12':depot('schinese'), '13':depot('english', 999),
                            '14':depot('', 888), '15':depot('schinese', 888)})
        self.assertEqual(set(worker.select_depots(info, 'schinese')), {11, 12})
        self.assertEqual(set(worker.select_depots(info, 'schinese', [999])), {11, 12, 13})
        self.assertEqual(set(worker.select_depots(info, 'schinese', [999], True)), {13})
        self.assertEqual(set(worker.select_depots(info, 'schinese', [888], True)), {14, 15})

    def test_configured_dlc_metadata_maps_depot_and_keeps_parent_requests(self):
        dlc = dict(depots={'11':dict(depotfromapp='123'),
                          '12':dict(manifests=dict(public=dict(gid='102')))})
        (self.inputs / '999.info.json').write_text(json.dumps(dlc), encoding='utf-8')
        cache = self.cache(self.info(False))
        with patch.dict(os.environ, BEAR_DOWNLOAD_SERVER='https://cache.invalid'), \
             patch.object(worker, 'BearCache', return_value=cache), \
             patch.object(worker.core, 'manifest_cache_path', side_effect=self.manifest_path), \
             patch.object(worker.core, 'http_get', side_effect=AssertionError('no external request')):
            plan = worker.resolve_plan(self.job)
        self.assertEqual(set(plan['depots']), {12})
        self.assertEqual(plan['destination'], self.installed)
        self.assertEqual(plan['depots'][12]['dlcappid'], 999)
        self.assertEqual(plan['dlc_ids'], [999])
        self.assertTrue(plan['dlc_only'])
        cache.resolve.assert_called_once_with(123, 'schinese', dlc_ids=[999])
        cache.manifest.assert_called_once_with(123, 12, 102)
        cache.prepare.assert_not_called()
        cache.finish.assert_not_called()

    def test_selected_dlc_requires_metadata_ownership(self):
        cache = self.cache(self.info(False))
        with patch.dict(os.environ, BEAR_DOWNLOAD_SERVER='https://cache.invalid'), \
             patch.object(worker, 'BearCache', return_value=cache):
            with self.assertRaisesRegex(RuntimeError, '999.*独立 Windows'):
                worker.resolve_plan(self.job)
        cache.manifest.assert_not_called()
        self.assertFalse(self.library.exists())

    def test_missing_dlc_key_is_explicit_and_prepares_parent_once(self):
        cache = self.cache()
        cache.resolve.return_value['keys'].pop('12')
        with patch.dict(os.environ, BEAR_DOWNLOAD_SERVER='https://cache.invalid'), \
             patch.object(worker, 'BearCache', return_value=cache):
            with self.assertRaisesRegex(RuntimeError, '999.*12.*解密资料'):
                worker.resolve_plan(self.job)
        self.assertEqual(cache.prepare.call_count, 1)
        self.assertEqual(cache.prepare.call_args.args[:2], (123, 'schinese'))
        self.assertEqual(cache.prepare.call_args.kwargs, dict(dlc_ids=[999]))
        cache.manifest.assert_not_called()

    def test_different_manifest_gid_is_not_accepted_for_selected_dlc(self):
        cache = self.cache()
        cache.resolve.return_value['manifests']['12'] = '999'
        with patch.dict(os.environ, BEAR_DOWNLOAD_SERVER='https://cache.invalid'), \
             patch.object(worker, 'BearCache', return_value=cache), \
             patch.object(worker.core, 'manifest_cache_path', side_effect=self.manifest_path):
            with self.assertRaisesRegex(RuntimeError, '999.*12.*102.*清单'):
                worker.resolve_plan(self.job)
        self.assertEqual(cache.prepare.call_args.args[:2], (123, 'schinese'))
        cache.manifest.assert_not_called()
        self.assertEqual(list(self.installed.iterdir()), [])

    def test_dlc_only_does_not_upload_partial_parent_from_local_inputs(self):
        cache = self.cache()
        cache.resolve.return_value = dict(status='missing', info=self.info())
        config = {'InstallConfigStore':{'Software':{'Valve':{'Steam':{'depots':{
            '12':{'DecryptionKey':'cd'*32}}}}}}}
        (self.steam / 'config/config.vdf').write_text(worker.vdf_body(config, 0), encoding='utf-8')
        Path(self.manifest_path(12, 102)).write_bytes(self.blob)
        with patch.dict(os.environ, BEAR_DOWNLOAD_SERVER='https://cache.invalid'), \
             patch.object(worker, 'BearCache', return_value=cache), \
             patch.object(worker.core, 'manifest_cache_path', side_effect=self.manifest_path):
            plan = worker.resolve_plan(self.job)
        self.assertEqual(set(plan['depots']), {12})
        cache.prepare.assert_not_called()
        cache.finish.assert_not_called()

    def test_dlc_overlay_updates_manifest_paths_and_preserves_base_record(self):
        (self.installed / 'base.dat').write_bytes(b'keep base')
        (self.installed / 'shared.dat').write_bytes(b'old dlc')
        appmanifest = self.library / 'steamapps/appmanifest_123.acf'
        appmanifest.parent.mkdir(parents=True)
        appmanifest.write_bytes(b'base installation record')
        registry = self.steam / 'steamapps/libraryfolders.vdf'
        registry.write_bytes(b'base library registry')
        plan = self.plan()
        with patch.object(worker.core, 'fetch_chunk', return_value=(b'new dlc', 'fixture')):
            worker.download_plan(plan, Path(self.job['state']), 2)
        worker.register_install(self.job, plan)
        self.assertEqual((self.installed / 'shared.dat').read_bytes(), b'new dlc')
        self.assertEqual((self.installed / 'base.dat').read_bytes(), b'keep base')
        self.assertEqual(appmanifest.read_bytes(), b'base installation record')
        self.assertEqual(registry.read_bytes(), b'base library registry')
        self.assertEqual((self.steam / 'depotcache/12_102.manifest').read_bytes(), self.blob)

    def test_resume_cache_matches_selection_mode_and_directory(self):
        self.job['dlc_ids'] = [999, 888]
        plan = self.plan()
        plan.update(worker.download_selection(self.job))
        Path(self.manifest_path(12, 102)).write_bytes(self.blob)
        with patch.object(worker, 'resolve_plan', return_value=plan) as resolve, \
             patch.object(worker.core, 'manifest_cache_path', side_effect=self.manifest_path):
            worker.cached_plan(self.job)
            self.job['dlc_ids'] = [888, 999, 999]
            restored = worker.cached_plan(self.job)
            self.assertEqual(resolve.call_count, 1)
            self.assertEqual(restored['dlc_ids'], [888, 999])
            self.job['dlc_ids'] = [999]
            worker.cached_plan(self.job)
            self.assertEqual(resolve.call_count, 2)
            self.job['dlc_only'] = False
            worker.cached_plan(self.job)
            self.assertEqual(resolve.call_count, 3)
            self.job['install_dir'] = str(self.root / 'another game')
            worker.cached_plan(self.job)
            self.assertEqual(resolve.call_count, 4)

    def test_changed_selection_invalidates_old_chunk_completion(self):
        plan = self.plan()
        with patch.object(worker.core, 'fetch_chunk', return_value=(b'new dlc', 'fixture')) as fetch:
            worker.download_plan(plan, Path(self.job['state']), 1)
            worker.download_plan(plan, Path(self.job['state']), 1)
            self.assertEqual(fetch.call_count, 1)
            plan['dlc_ids'] = [888]
            worker.download_plan(plan, Path(self.job['state']), 1)
            self.assertEqual(fetch.call_count, 2)

    def test_dlc_only_requires_selection_and_an_existing_target(self):
        self.job['dlc_ids'] = []
        with self.assertRaisesRegex(RuntimeError, '先选择.*DLC'):
            worker.resolve_plan(self.job)
        self.job.update(dlc_ids=[999], install_dir=str(self.root / 'not installed'))
        cache = self.cache()
        with patch.dict(os.environ, BEAR_DOWNLOAD_SERVER='https://cache.invalid'), \
             patch.object(worker, 'BearCache', return_value=cache):
            with self.assertRaisesRegex(RuntimeError, '已安装的本体'):
                worker.resolve_plan(self.job)


if __name__ == '__main__':
    unittest.main()
