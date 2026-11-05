"""Build the embedded ZIP from the pinned official core or a prepared release folder."""
import argparse
import hashlib
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import zipfile

import requests

root = Path(__file__).resolve().parent.parent
version = re.search(r'public const string Version="([^"]+)"', (root/'LanService.cs').read_text(encoding='utf-8')).group(1)
digest = {'release-2026_09_27': '300186c34f49a9ea75b33ce2af299267cc1dad0225cf45e81a229578d4e11c85'}[version]
target = root/'LanAssets'/('core-'+version+'.zip')
parser = argparse.ArgumentParser()
parser.add_argument('--source', type=Path, help='An already extracted official release folder')
args = parser.parse_args()
if target.exists():
    print('Embedded LAN core is ready.')
    raise SystemExit(0)

target.parent.mkdir(exist_ok=True)
(root/'obj').mkdir(exist_ok=True)
with tempfile.TemporaryDirectory(prefix='lan-package-', dir=root/'obj') as temporary:
    stage = Path(temporary)
    source = args.source
    if source is None:
        session = requests.Session()
        session.trust_env = False
        response = session.get('https://github.com/Detanup01/gbe_fork/releases/download/'+version+'/emu-win-release-vs22.7z', timeout=(10, 90))
        response.raise_for_status()
        if hashlib.sha256(response.content).hexdigest() != digest:
            raise SystemExit('Official core archive differs from the pinned release.')
        archive = stage/'core.7z'
        archive.write_bytes(response.content)
        extractor = shutil.which('7z')
        if extractor is None:
            response = session.get('https://www.7-zip.org/a/7zr.exe', timeout=(10, 60))
            response.raise_for_status()
            extractor = stage/'7zr.exe'
            extractor.write_bytes(response.content)
        subprocess.run([str(extractor), 'x', '-y', '-o'+str(stage), str(archive),
                        'release/regular/*', 'release/experimental/*',
                        'release/tools/lobby_connect/*', 'release/tools/generate_interfaces/*',
                        'release/README.release.md'], check=True, capture_output=True)
        source = stage/'release'
    if not (source/'regular/x64/steam_api64.dll').is_file():
        raise SystemExit('Select the release folder containing regular/x64/steam_api64.dll.')
    partial = target.with_suffix('.zip.partial')
    try:
        with zipfile.ZipFile(partial, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for item in ('regular', 'experimental', 'tools/lobby_connect', 'tools/generate_interfaces', 'README.release.md'):
                path = source/item
                files = sorted(path.rglob('*')) if path.is_dir() else [path]
                for file in files:
                    if file.is_file():
                        archive.write(file, 'release/'+file.relative_to(source).as_posix())
        partial.replace(target)
    finally:
        if partial.exists():
            partial.unlink()
print('Embedded LAN core prepared:', target.name)
