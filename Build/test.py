"""Run public fixtures. No real Steam directory, account or external network is used."""
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parent.parent
subprocess.run([sys.executable, '-B', '-m', 'unittest', 'network_tests', 'cdn_tests'],
               cwd=root/'DownloadBackend', check=True)
subprocess.run([sys.executable, '-B', 'bear_downloader.py', '--self-test', str(root/'obj/download-tests')],
               cwd=root/'DownloadBackend', check=True)
exe = root/'dist/BearLibrary.exe'
if exe.exists():
    subprocess.run([str(exe), '--workflow-test', str(root/'obj/workflow-tests')], check=True)
