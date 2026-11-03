"""A disposable agent process for the real SessionStart/status hook round trip."""
import json
import os
from pathlib import Path
import subprocess

data = Path.home() / '.local/share/agwinterm'
hook = [str(data / 'agwinterm-hook')]
event = json.dumps({'session_id': 'wsl-test-conversation', 'cwd': os.getcwd()})
for state in ('bind', 'active', 'stop'):
    subprocess.run(hook + ['codex', state], input=event, text=True, check=True)
print('WSL_HOOKS_FINISHED', flush=True)
