"""Historical Python hook retained only as a benchmark baseline; never installed or shipped."""
import json
import os
from pathlib import Path
import subprocess
import sys


def main():
    pane = os.environ.get('AGWINTERM_SESSION_ID')
    if not pane:
        return
    agent, state = sys.argv[1:]
    event = json.load(sys.stdin)
    if agent == 'claude' and (os.environ.get('DEVIN_PROJECT_DIR') or os.environ.get('CLAUDE_CODE_ENTRYPOINT', 'cli') != 'cli' or os.environ.get('CLAUDE_CODE_SESSION_ATTENDED') == '0'):
        return
    if agent == 'codex' and event.get('transcript_path'):
        with open(event['transcript_path'], encoding='utf-8') as transcript:
            if json.loads(transcript.readline()).get('payload', {}).get('source', 'cli') != 'cli':
                return
    cli = str(Path.home() / '.local/bin/agwintermctl')
    args = [cli, 'session']
    if state == 'bind':
        session = event.get('session_id')
        if not session:
            return
        args += ['bind', agent, '--resume', session, '--pid', str(os.getpid())]
        cwd = event.get('cwd') or os.environ.get('DEVIN_PROJECT_DIR') or os.getcwd()
        args += ['--cwd', cwd]
    else:
        if state == 'stop':
            state = 'blocked' if str(event.get('last_assistant_message', '')).rstrip().endswith('?') else 'completed'
        args += ['status', state]
    # Explicit routing also works if the user customized WSLENV after the shell started.
    args += ['--target', pane, '--pipe', os.environ.get('AGWINTERM_PIPE', 'agwinterm'), '--timeout', '10']
    subprocess.run(args, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=12)


try:
    main()
except Exception:
    pass  # A terminal integration must never fail an agent turn.
