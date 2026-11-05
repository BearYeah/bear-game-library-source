"""Fetch the upstream curl distribution. No private mirrors or prebuilt Bear executables."""
import hashlib
import io
from pathlib import Path
import urllib.request
import zipfile

root = Path(__file__).resolve().parent.parent
url = 'https://curl.se/windows/dl-8.22.0_3/curl-8.22.0_3-win64-mingw.zip'
sha256 = 'f38b0ac2e3a280fd414b607c874a21e6dd4d1ed0e2ae622afbf0020917446561'
with urllib.request.urlopen(url, timeout=60) as response:
    data = response.read()
if hashlib.sha256(data).hexdigest() != sha256:
    raise SystemExit('Upstream curl archive hash differs from the pinned release.')
with zipfile.ZipFile(io.BytesIO(data)) as archive:
    for name in ('curl.exe', 'curl-ca-bundle.crt'):
        member = next(item for item in archive.namelist() if item.endswith('/bin/'+name))
        target = root/'NetAssets'/name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(archive.read(member))
    for member in archive.namelist():
        if Path(member).name.upper().startswith(('COPYING', 'LICENSE')) and not member.endswith('/'):
            target = root/'dist/licenses/curl'/Path(member).name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(archive.read(member))
print('curl and the CA bundle are ready.')
