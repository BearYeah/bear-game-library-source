"""Portable Windows build; requires the SDK and Python dependencies documented in README."""
import argparse
from pathlib import Path
import shutil
import subprocess
import sys

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser()
parser.add_argument('--skip-assets', action='store_true', help='Use already prepared local assets')
parser.add_argument('--skip-worker', action='store_true', help='Use an already built local worker')
args = parser.parse_args()
subprocess.run([sys.executable, str(root/'Build/prepare_lan_core.py')], check=True)
if not args.skip_assets:
    subprocess.run([sys.executable, str(root/'Build/fetch_network_assets.py')], check=True)
if not args.skip_worker:
    subprocess.run([sys.executable, str(root/'Build/build_downloader.py')], check=True)
subprocess.run(['dotnet', 'publish', 'NovaLibrary.csproj', '-c', 'Release', '-r', 'win-x64',
                '--self-contained', 'true', '-p:PublishSingleFile=true',
                '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
                '-p:DebugType=None', '-o', str(root/'dist')], cwd=root, check=True)
for name in ('LICENSE', 'THIRD_PARTY_NOTICES.md'):
    shutil.copyfile(root/name, root/'dist'/name)
print('Build complete: dist/BearLibrary.exe')
