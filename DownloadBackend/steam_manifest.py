# SPDX-License-Identifier: GPL-3.0-only
# Bear manifest/chunk engine; operational data providers are configured externally.
import argparse
from download_catalog import parse_lua
import base64
import binascii
import concurrent.futures as futures
import hashlib
import io
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
MAGIC = 1911953360
UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) BearDownload/1.8'
TIMEOUT = 30
_print_lock = threading.Lock()

def http_get(url, timeout=TIMEOUT, retries=2, raw=False):
    """GET，返回 bytes 或文本；失败抛异常。"""
    last = None
    for i in range(retries):
        try:
            req = urllib.request.Request(url, headers={'User-Agent': UA})
            with urllib.request.urlopen(req, timeout=timeout) as r:
                data = r.read()
            return data if raw else data.decode('utf-8', 'replace')
        except urllib.error.HTTPError as e:
            last = 'HTTP %s' % e.code
            if e.code in (404, 403, 401):
                raise
        except Exception as e:
            last = '%s: %s' % (type(e).__name__, e)
        if i + 1 < retries:
            time.sleep(0.8 * (i + 1))
    raise RuntimeError('%s -> %s' % (url, last))
_tls = threading.local()
CDN_HOSTS = ['cache3-hkg1.steamcontent.com', 'cache1-hkg1.steamcontent.com', 'cache4-hkg1.steamcontent.com', 'cache2-hkg1.steamcontent.com', 'cache5-hkg1.steamcontent.com', 'steamcdn-a.akamaihd.net']
_host_bad = {}
_host_ok = {}
_rr = [0]

def host_pick():
    """按「成功次数」轮转着挑节点：单点并发太高会被限速（实测 cache3 被压到 30s 无响应），
    分散到多个节点才是满速。"""
    hosts = [h for h in CDN_HOSTS if not _host_bad.get(h)]
    hosts = hosts or list(CDN_HOSTS)
    hosts.sort(key=lambda h: (-_host_ok.get(h, 0), CDN_HOSTS.index(h)))
    with _print_lock:
        _rr[0] = (_rr[0] + 1) % max(1, len(hosts))
        idx = _rr[0]
    return hosts[idx % len(hosts)]

def cdn_order():
    good = [h for h in CDN_HOSTS if not _host_bad.get(h)]
    bad = [h for h in CDN_HOSTS if _host_bad.get(h)]
    return good + bad
_code_cache_path = None
_code_cache = {}
_code_lock = threading.Lock()

def _load_code_cache():
    global _code_cache, _code_cache_path
    if _code_cache_path is None:
        base = os.environ.get('BEAR_DOWNLOAD_CACHE') or os.path.join(os.path.expanduser('~'), '.bear-library')
        os.makedirs(base, exist_ok=True)
        _code_cache_path = os.path.join(base, 'codes.json')
        if os.path.isfile(_code_cache_path):
            try:
                with open(_code_cache_path, 'r', encoding='utf-8') as f:
                    _code_cache = json.load(f)
            except Exception:
                _code_cache = {}
    return _code_cache

def save_code_cache():
    if not _code_cache_path:
        return
    with _code_lock:
        tmp = _code_cache_path + '.tmp'
        with open(tmp, 'w', encoding='utf-8') as f:
            json.dump(_code_cache, f)
        os.replace(tmp, _code_cache_path)
_cache_dir = None

def manifest_cache_path(depot, gid):
    global _cache_dir
    if _cache_dir is None:
        base = os.environ.get('BEAR_DOWNLOAD_CACHE') or os.path.join(os.path.expanduser('~'), '.bear-library')
        _cache_dir = os.path.join(base, 'manifest')
        os.makedirs(_cache_dir, exist_ok=True)
    return os.path.join(_cache_dir, 'manifest_%d_%d.manifest' % (depot, gid))

def _save_manifest(path, data):
    tmp = path + '.tmp'
    with open(tmp, 'wb') as f:
        f.write(data)
    os.replace(tmp, path)

def _pb(buf):
    i, out = (0, [])
    while i < len(buf):
        b = buf[i]
        i += 1
        k, t = (b >> 3, b & 7)
        if t == 0:
            v = s = 0
            while True:
                x = buf[i]
                i += 1
                v |= (x & 127) << s
                s += 7
                if not x & 128:
                    break
            out.append((k, 0, v))
        elif t == 2:
            l = s = 0
            while True:
                x = buf[i]
                i += 1
                l |= (x & 127) << s
                s += 7
                if not x & 128:
                    break
            out.append((k, 2, buf[i:i + l]))
            i += l
        elif t == 5:
            out.append((k, 5, struct.unpack('<I', buf[i:i + 4])[0]))
            i += 4
        elif t == 1:
            out.append((k, 1, struct.unpack('<Q', buf[i:i + 8])[0]))
            i += 8
        else:
            raise ValueError('bad wire type %d' % t)
    return out

def _looks_like_path(b):
    return bool(b) and b'.' in b and (b'\\' in b or b'/' in b) and all((32 <= c < 127 for c in b))

def _maybe_decrypt_name(raw, key):
    """文件名可能被加密：base64 包裹，AES-256-CBC（前 16B 是 ECB 的 IV），明文前 20B 是名 sha1。"""
    if not raw:
        return ('', False)
    if _looks_like_path(raw):
        return (raw.decode('utf-8', 'replace'), False)
    if not key:
        return (raw.decode('utf-8', 'replace'), False)
    try:
        ct = base64.b64decode(raw + b'=' * (-len(raw) % 4))
    except Exception:
        return (raw.decode('utf-8', 'replace'), False)
    if len(ct) < 32 or len(ct) % 16:
        return (raw.decode('utf-8', 'replace'), False)
    try:
        pt = _symmetric_decrypt(key, ct)
    except Exception:
        return (raw.decode('utf-8', 'replace'), False)
    for name in (pt.split(b'\x00')[0], pt[20:].split(b'\x00')[0]):
        name = name.strip()
        try:
            decoded = name.decode('utf-8')
        except UnicodeDecodeError:
            continue
        if decoded and all((ord(c) >= 32 for c in decoded)):
            return (decoded, True)
    return (raw.decode('utf-8', 'replace'), False)

def parse_manifest(blob, depot_key=None):
    """返回 {'files': [{path,size,flags,chunks:[{sha,offset,cb_orig,cb_comp}],sha_content}]}"""
    if struct.unpack('<I', blob[:4])[0] != MAGIC:
        raise ValueError('not a manifest blob')
    mlen = struct.unpack('<I', blob[4:8])[0]
    meta = blob[8:8 + mlen]
    key = binascii.unhexlify(depot_key) if depot_key else None
    files = []
    for fk, ft, fv in _pb(meta):
        if fk != 1:
            continue
        m = {}
        for k, t, v in _pb(fv):
            if k == 6:
                c = {}
                for ck, ct2, cv in _pb(v):
                    c[ck] = cv
                m.setdefault('chunks', []).append({'sha': binascii.hexlify(c.get(1, b'')).decode(), 'crc': c.get(2, 0), 'offset': c.get(3, 0), 'cb_orig': c.get(4, 0), 'cb_comp': c.get(5, 0)})
            else:
                m[k] = v
        raw_name = m.get(1, b'')
        name, enc = _maybe_decrypt_name(raw_name, key)
        files.append({'path': name.replace('/', '\\'), 'size': m.get(2, 0), 'flags': m.get(3, 0), 'encrypted_name': enc, 'sha_content': binascii.hexlify(m.get(5, b'')).decode(), 'chunks': m.get('chunks', [])})
    total = sum((f['size'] for f in files))
    return {'files': files, 'file_count': len(files), 'total_size': total, 'chunk_count': sum((len(f['chunks']) for f in files))}

def _vz_decompress(data):
    """解开 Steam 的 VZ / VZstd 压缩块（SteamKit VZipUtil / VZstdUtil 的格式）。

    VZ   : "VZa" + 4B(crc/timestamp) + 5B lzma props + 流 + 10B 尾(crc4 + 原长4 + "vz")
    VSZa : "VSZa" + 4B crc + zstd 帧 + 15B 尾(crc4 + 原长4 + 3B + "zsv")
    两种都按尾部声明的原始长度截断（LZMA 解码器会多吃掉尾部字节）。
    """
    import lzma as _lz_
    if data[:4] == b'VSZa':
        if len(data) < 8 + 15:
            raise ValueError('VZstd 太短')
        want = struct.unpack('<i', data[-11:-7])[0]
        frame = data[8:-15]
        try:
            import zstandard as _zstd
            out = _zstd.ZstdDecompressor().decompress(frame, max_output_size=max(want, 1 << 20))
        except ImportError:
            try:
                from compression import zstd as _zstd2
                out = _zstd2.decompress(frame)
            except Exception:
                raise RuntimeError('VZstd 需要 zstandard 模块')
    elif data[:3] == b'VZa':
        if len(data) < 7 + 5 + 10:
            raise ValueError('VZ 太短')
        props = data[7:12]
        body = data[12:-10]
        want = struct.unpack('<i', data[-6:-2])[0]
        d = props[0]
        lc, rem = (d % 9, d // 9)
        lp, pb = (rem % 5, rem // 5)
        dict_size = struct.unpack('<I', props[1:5])[0]
        filters = [{'id': _lz_.FILTER_LZMA1, 'dict_size': max(min(dict_size, 1 << 26), 4096), 'lc': lc, 'lp': lp, 'pb': pb}]
        out = _lz_.LZMADecompressor(format=_lz_.FORMAT_RAW, filters=filters).decompress(body)
    else:
        return data
    if want >= 0 and len(out) > want:
        out = out[:want]
    if want >= 0 and len(out) != want:
        raise ValueError('VZ 长度不符: 声明 %d 实得 %d' % (want, len(out)))
    return out

def _depot_key_bytes(key_hex):
    return binascii.unhexlify(key_hex)

def _symmetric_decrypt(key, ct):
    """Steam 的 SymmetricDecrypt：前 16 字节是 ECB 加密的 IV，其余 CBC/PKCS7。"""
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
    if len(ct) < 32 or len(ct) % 16:
        raise ValueError('密文长度异常 %d' % len(ct))
    e = Cipher(algorithms.AES(key), modes.ECB()).decryptor()
    iv = e.update(ct[:16]) + e.finalize()
    d = Cipher(algorithms.AES(key), modes.CBC(iv)).decryptor()
    pt = d.update(ct[16:]) + d.finalize()
    pad = pt[-1] if pt else 0
    if 0 < pad <= 16 and pt[-pad:] == bytes([pad]) * pad:
        pt = pt[:-pad]
    return pt

chunk_fetch = None


def decode_chunk(data, key_hex):
    if key_hex:
        plain = _symmetric_decrypt(_depot_key_bytes(key_hex), data)
    else:
        plain = data
    if plain[:4] == b'PK\x03\x04':
        with zipfile.ZipFile(io.BytesIO(plain)) as archive:
            return archive.read('z' if 'z' in archive.namelist() else archive.namelist()[0])
    if not key_hex:
        raise RuntimeError('没有 depot key，无法解密内容块。')
    return _vz_decompress(plain)


def fetch_chunk(depot, sha_hex, code, key_hex, timeout=12):
    if chunk_fetch is not None:
        data, host = chunk_fetch(depot, sha_hex, timeout)
        return decode_chunk(data, key_hex), host
    errs = []
    first = host_pick()
    for host in [first] + [candidate for candidate in cdn_order() if candidate != first]:
        try:
            data = http_get('https://%s/depot/%d/chunk/%s' % (host, depot, sha_hex),
                            timeout=timeout, retries=1, raw=True)
            plain = decode_chunk(data, key_hex)
        except Exception as error:
            errs.append('%s: %s' % (host, str(error)[:60]))
            _host_bad[host] = _host_bad.get(host, 0) + 1
            continue
        _host_ok[host] = _host_ok.get(host, 0) + 1
        return plain, host
    raise RuntimeError('chunk %s@%d 取不到 (%s)' % (sha_hex, depot, ' | '.join(errs[:4])))


def _local_metadata_file(name):
    from pathlib import Path
    directory = os.environ.get('BEAR_METADATA_DIR')
    if not directory:
        return None
    file = Path(directory) / name
    return file if file.is_file() else None

def fetch_lua(appid, budget=40):
    file = _local_metadata_file('%d.lua' % appid)
    return (file.read_text(encoding='utf-8'), 'local') if file else (None, None)

def fetch_keyvdf(appid, budget=15):
    file = _local_metadata_file('%d.key.vdf' % appid)
    return (file.read_text(encoding='utf-8'), 'local') if file else (None, None)

def request_code(gid):
    file = os.environ.get('BEAR_REQUEST_CODES_FILE')
    if file:
        with open(file, encoding='utf-8') as source:
            code = json.load(source).get(str(gid))
        if code:
            return (str(code), 'configured')
    raise RuntimeError('Please provide the manifest locally or configure a metadata provider.')

def fetch_manifest(depot, gid, code=None, use_cache=True, on_fallback=None, alternate=None):
    cache = manifest_cache_path(depot, gid)
    if use_cache and os.path.isfile(cache):
        with open(cache, 'rb') as source:
            return (source.read(), 'cache')
    file = _local_metadata_file('%d_%d.manifest' % (depot, gid))
    if file:
        data = file.read_bytes()
        if use_cache:
            _save_manifest(cache, data)
        return (data, 'local')
    if alternate:
        data = alternate(depot, gid)
        if data is not None:
            if use_cache:
                _save_manifest(cache, data)
            return (data, 'configured')
    raise RuntimeError('Manifest unavailable from the configured inputs.')
