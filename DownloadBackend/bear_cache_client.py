"""Client for an optional user-configured HTTPS metadata provider."""
from contextlib import contextmanager
import io
import json
import os
from pathlib import Path
import time
import zipfile

import requests

DEFAULT_SERVER = ''


class CacheUnavailable(RuntimeError):
    pass


class DailyLimit(RuntimeError):
    pass


@contextmanager
def identity_lock(file):
    with file.open('a+b') as lock:
        if os.name == 'nt':
            import msvcrt
            lock.seek(0)
            if not lock.read(1):
                lock.write(b'0')
                lock.flush()
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_LOCK, 1)
        else:
            import fcntl
            fcntl.flock(lock, fcntl.LOCK_EX)
        try:
            yield
        finally:
            if os.name == 'nt':
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(lock, fcntl.LOCK_UN)


class BearCache:
    def __init__(self, cache_directory, proxy):
        self.base = os.environ.get('BEAR_DOWNLOAD_SERVER', DEFAULT_SERVER).rstrip('/')
        root = Path(cache_directory).parent
        root.mkdir(parents=True, exist_ok=True)
        self.identity_file = root/'server-device.json'
        self.identity_lock_file = root/'server-device.lock'
        self.upload_key_file = Path(os.environ.get('BEAR_UPLOAD_KEY_FILE') or root/'server-upload.key')
        self.certificate = True
        self.proxies = {'http': proxy, 'https': proxy} if proxy else None
        self.token = ''

    def request(self, method, route, *, data=None, json_body=None, extra_headers=None):
        headers = {'User-Agent': 'BearLibrary/1.8.10'}
        if self.token:
            headers['Authorization'] = 'Bearer '+self.token
        headers.update(extra_headers or {})
        try:
            response = requests.request(method, self.base+route, headers=headers,
                                        data=data, json=json_body, proxies=self.proxies,
                                        verify=self.certificate, timeout=(4, 10), allow_redirects=False)
        except requests.RequestException as error:
            raise CacheUnavailable('资料连接暂时不可用，请稍后重试。') from error
        if response.status_code >= 400:
            try:
                error = response.json()
            except ValueError:
                error = {}
            if error.get('error') == 'daily_limit':
                raise DailyLimit('今天已获取 10 款游戏的资料，请北京时间零点后再试。')
            raise CacheUnavailable('所需下载资料暂不可用，请稍后重试。')
        return response

    def identify(self):
        with identity_lock(self.identity_lock_file):
            if self.identity_file.exists():
                self.token = json.loads(self.identity_file.read_text(encoding='utf-8'))['token']
            else:
                token = self.request('POST', '/v1/devices', json_body={}).json()['token']
                temp = self.identity_file.with_suffix('.tmp')
                temp.write_text(json.dumps(dict(token=token)), encoding='utf-8')
                os.replace(temp, self.identity_file)
                self.token = token

    def resolve(self, appid, language, dlc_ids=()):
        self.identify()
        return self.request('POST', '/v1/games/%d/resolve' % appid,
                            json_body=dict(language=language, dlc_ids=list(dlc_ids))).json()

    def manifest(self, appid, depot, gid):
        return self.request('GET', '/v1/games/%d/manifests/%d/%d' % (appid, depot, gid)).content

    def prepare(self, appid, language, progress, dlc_ids=()):
        if not self.token:
            self.identify()
        started = time.monotonic()
        while time.monotonic()-started < 180:
            result = self.request('POST', '/v1/games/%d/prepare' % appid,
                                  json_body=dict(language=language, dlc_ids=list(dlc_ids))).json()
            if result['status'] == 'ready':
                return result
            progress(int(time.monotonic()-started))
            time.sleep(2)
        raise CacheUnavailable('下载资料仍在准备中，稍后继续即可使用已取得的资料。')

    def finish(self, plan, info):
        appid = plan['appid']
        if self.upload_key_file.exists():
            package = io.BytesIO()
            index = dict(info=info, language=plan['language'],
                         keys={str(d): row['key'] for d,row in plan['depots'].items()})
            with zipfile.ZipFile(package, 'w', compression=zipfile.ZIP_STORED) as archive:
                archive.writestr('index.json', json.dumps(index))
                for depot, row in plan['depots'].items():
                    archive.writestr('%d_%d.manifest' % (depot, row['gid']), row['blob'])
            response = self.request('POST', '/v1/games/%d/upload' % appid, data=package.getvalue(),
                                    extra_headers={'Content-Type': 'application/zip',
                                                   'X-Bear-Upload-Key': self.upload_key_file.read_text().strip()})
        else:
            response = self.request('POST', '/v1/games/%d/complete' % appid, json_body={})
        return response.json()
