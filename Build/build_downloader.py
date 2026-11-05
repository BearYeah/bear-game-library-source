"""Build the public worker from source without private modules or binary helpers."""
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parent.parent
subprocess.run([sys.executable, '-m', 'PyInstaller', '--noconfirm', '--clean', '--onefile',
                '--name', 'BearDownloader', '--distpath', str(root/'DownloadAssets'),
                '--workpath', str(root/'obj/worker-build'), '--specpath', str(root/'obj'),
                '--collect-all', 'zstandard', '--collect-all', 'cryptography',
                str(root/'DownloadBackend/bear_downloader.py')], cwd=root, check=True)
