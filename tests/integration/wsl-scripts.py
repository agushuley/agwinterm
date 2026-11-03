"""Run in WSL: python3 tests/integration/wsl-scripts.py. Uses only a disposable HOME."""
import json
import os
import platform
from pathlib import Path
import shutil
import shlex
import subprocess
import tempfile
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[2] / 'src/Agwinterm.Pty/Wsl'


class WslScripts(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="agwinterm-wsl space' тест-")
        self.addCleanup(self.temp.cleanup)
        self.home = Path(self.temp.name)
        self.cli = self.home / 'Windows CLI with spaces' / 'agwintermctl.exe'
        self.cli.parent.mkdir()
        self.cli.write_text('#!/bin/sh\nprintf "%s\\n" "$@"\n')
        self.cli.chmod(0o700)
        self.bridge = self.cli.with_name('agwinterm-hook.exe')
        self.bridge.write_bytes(b'test bridge')
        self.helper = self.home / 'native installer'
        arch = {'x86_64': 'x64', 'aarch64': 'arm64', 'arm64': 'arm64'}[platform.machine()]
        shutil.copyfile(SCRIPTS.parents[2] / f'artifacts/wsl-hook/agwinterm-hook-linux-{arch}', self.helper)
        self.helper.chmod(0o700)
        self.environment = patch.dict(os.environ, {'HOME': str(self.home), 'CODEX_HOME': str(self.home / '.codex'),
            'XDG_CONFIG_HOME': str(self.home / '.config'), 'ZDOTDIR': str(self.home), 'AGWINTERM_ENABLED': '1',
            'CLAUDE_CONFIG_DIR': str(self.home / '.claude')})
        self.environment.start()
        self.addCleanup(self.environment.stop)
        if os.environ.get('AGWINTERM_TEST_POSIX_PATHS') == '1':
            # Explicit Linux CI mode: paths already refer to POSIX fixture files.
            # Real WSL runs continue to use the actual wslpath in both directions.
            tools = self.home / 'path shim'
            tools.mkdir()
            shim = tools / 'wslpath'
            shim.write_text('#!/bin/sh\n[ "$#" = 2 ] || exit 1\ncase "$1" in -u|-w) printf "%s\\n" "$2";; *) exit 1;; esac\n')
            shim.chmod(0o700)
            os.environ['PATH'] = str(tools) + os.pathsep + os.environ['PATH']

    def install(self, kind, action='install'):
        def windows(path):
            return subprocess.check_output(['wslpath','-w',str(path)],text=True).rstrip('\n')
        payload = dict(version=1, kind=kind, action=action, windowsCli=windows(self.cli), windowsBridge=windows(self.bridge),
                       skill='test skill',shell=(SCRIPTS/'shell.sh').read_text())
        return subprocess.check_output([str(self.helper),'--install'],input=json.dumps(payload),text=True)

    def test_native_upgrade_preserves_user_hooks_and_refuses_missing_bridge(self):
        self.install('hooks')
        path = self.home / '.codex/hooks.json'
        settings = json.loads(path.read_text())
        settings['hooks']['Stop'][0]['hooks'].append({'command': 'echo mine'})
        settings['hooks']['Stop'][0]['hooks'][0]['command'] = 'python3 ' + shlex.quote(str(self.home/'.local/share/agwinterm/hook.py')) + ' codex stop'
        path.write_text(json.dumps(settings))
        self.assertIn('native Rust', self.install('hooks'))
        first = path.read_text()
        self.assertNotIn('python3', first)
        self.assertIn('echo mine', first)
        self.assertEqual(1, len(json.loads(first)['hooks']['Stop']))
        self.install('hooks')
        self.assertEqual(first, path.read_text())
        installed = self.home / '.local/share/agwinterm/agwinterm-hook'
        self.assertEqual(self.helper.read_bytes(), installed.read_bytes())
        self.assertEqual(0o700, installed.stat().st_mode & 0o777)
        self.bridge.unlink()
        with self.assertRaises(subprocess.CalledProcessError):
            self.install('hooks')
        self.assertEqual(first,path.read_text())

    def test_repeat_install_preserves_hooks_and_profile_and_cli_quotes(self):
        settings = self.home / '.claude/settings.json'
        settings.parent.mkdir()
        settings.write_text(json.dumps({'custom': True, 'hooks': {'Stop': [{'hooks': [{'command': 'echo mine'}]}]}}))
        rc = self.home / '.bashrc'
        rc.write_text('export MY_SETTING=preserved\n')
        self.install('hooks')
        first = settings.read_text()
        self.install('hooks')
        self.assertEqual(first, settings.read_text())
        self.assertTrue(json.loads(first)['custom'])
        self.assertIn('echo mine', first)
        self.assertEqual(1, rc.read_text().count('# >>> agwinterm cli >>>'))
        self.assertIn('MY_SETTING=preserved', rc.read_text())
        wrapper = self.home / '.local/bin/agwintermctl'
        result = subprocess.check_output([str(wrapper), 'a b', "single'quote", '$HOME'], text=True)
        self.assertEqual("a b\nsingle'quote\n$HOME\n", result)
        self.install('cli', 'remove')
        self.assertFalse(wrapper.exists())
        self.assertIn('MY_SETTING=preserved', rc.read_text())

    def test_invalid_settings_not_overwritten(self):
        path = self.home / '.codex/hooks.json'
        path.parent.mkdir()
        path.write_text('{invalid')
        self.assertIn('refused', self.install('hooks'))
        self.assertEqual('{invalid', path.read_text())

    def test_cli_remove_restores_all_startup_files(self):
        original = 'export MY_SETTING=preserved\n'
        profiles = [self.home / name for name in ('.profile', '.bashrc', '.zshrc')]
        for path in profiles:
            path.write_text(original)
        self.install('cli')
        for path in profiles:
            self.assertIn('# >>> agwinterm cli >>>', path.read_text())
        self.install('cli', 'remove')
        self.assertFalse((self.home / '.local/bin/agwintermctl').exists())
        for path in profiles:
            self.assertEqual(original, path.read_text())
        self.install('cli', 'remove')
        for path in profiles:
            self.assertEqual(original, path.read_text())

    def test_incomplete_profile_block_is_not_overwritten(self):
        path=self.home/'.profile'
        original='export MINE=1\n# >>> agwinterm cli >>>\nbroken'
        path.write_text(original)
        with self.assertRaises(subprocess.CalledProcessError):
            self.install('cli')
        self.assertEqual(original,path.read_text())

    def test_cli_remove_refuses_unrelated_executable(self):
        path=self.home/'.local/bin/agwintermctl'
        path.parent.mkdir(parents=True)
        path.write_text('unrelated executable')
        with self.assertRaises(subprocess.CalledProcessError):
            self.install('cli','remove')
        self.assertEqual('unrelated executable',path.read_text())

    def test_devin_jsonc_is_backed_up_and_preserves_string_contents(self):
        path = self.home / '.config/devin/config.json'
        path.parent.mkdir(parents=True)
        original = '{// user comment\n"url":"https://example.test/a,}", /* comment */ "enabled":true,}'
        path.write_text(original)
        self.install('hooks')
        self.assertEqual('https://example.test/a,}', json.loads(path.read_text())['url'])
        self.assertEqual(original, path.with_name('config.json.agwinterm.bak').read_text())

    def test_bash_and_zsh_osc7_paths_and_existing_prompt(self):
        self.install('shell')
        self.install('shell')
        cwd = self.home / "space % тест ' quote"
        cwd.mkdir()
        for shell in ('bash', 'zsh'):
            script = '. "$HOME/.local/share/agwinterm/shell.sh"; __agw_prompt'
            result = subprocess.check_output([shell, '-c', script], cwd=cwd)
            self.assertTrue(result.startswith(b'\x1b]7;file://localhost/'), (shell, result))
            self.assertIn(b'space%20%25%20%D1%82%D0%B5%D1%81%D1%82%20%27%20quote', result, shell)
            self.assertTrue(result.endswith(b'\x07'))
        result = subprocess.check_output(['bash', '-c', 'PROMPT_COMMAND=("echo existing"); . "$HOME/.local/share/agwinterm/shell.sh"; . "$HOME/.local/share/agwinterm/shell.sh"; declare -p PROMPT_COMMAND'], text=True)
        self.assertIn('echo existing', result)
        self.assertEqual(1, result.count('__agw_prompt'))

    def test_bash_string_prompt_preserves_existing_command_and_reports_cwd(self):
        for existing in ('', ':;', 'printf existing', 'printf existing;', 'printf existing # comment'):
            with self.subTest(prompt=existing):
                script = 'PROMPT_COMMAND=$1; . "$2"; . "$2"; eval "$PROMPT_COMMAND"'
                result = subprocess.run(['bash', '-c', script, 'test', existing, str(SCRIPTS / 'shell.sh')],
                                        cwd=self.home, capture_output=True)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual(b'', result.stderr)
                self.assertEqual(1, result.stdout.count(b'\x1b]7;file://localhost/'))
                self.assertTrue(result.stdout.endswith(b'\x07'))
                if existing.startswith('printf'):
                    self.assertTrue(result.stdout.startswith(b'existing\x1b]7;'))

    def test_skills_respect_codex_home(self):
        with patch.dict(os.environ, {'CODEX_HOME': str(self.home / 'custom codex')}):
            self.install('skill')
        self.assertTrue((self.home / 'custom codex/skills/agwinterm/SKILL.md').exists())

    def test_ancestry_rejects_pid_from_another_pane(self):
        result = subprocess.run([str(self.helper),'--ancestry',str(os.getpid()),'not-this-pane'],capture_output=True)
        self.assertNotEqual(0, result.returncode)

    def test_config_symlink_is_preserved_and_backup_contains_original(self):
        target=self.home/'dotfiles/settings.json'
        target.parent.mkdir()
        original='{"custom":true}'
        target.write_text(original)
        link=self.home/'.claude/settings.json'
        link.parent.mkdir()
        link.symlink_to(target)
        self.install('hooks')
        self.assertTrue(link.is_symlink())
        self.assertTrue(json.loads(target.read_text())['custom'])
        self.assertEqual(original,link.with_name('settings.json.agwinterm.bak').read_text())

    def test_install_without_python_on_path(self):
        restricted=self.home/'restricted'
        restricted.mkdir()
        for tool in ('wslpath','sh'):
            (restricted/tool).symlink_to(shutil.which(tool))
        with patch.dict(os.environ,{'PATH':str(restricted)}):
            for kind in ('cli','hooks','skill','shell'):
                self.install(kind)
        self.assertTrue((self.home/'.local/share/agwinterm/agwinterm-hook').exists())

    def test_bootstrap_selects_guest_architecture_preserves_arguments_and_stdin(self):
        tools = self.home / 'bootstrap tools'
        tools.mkdir()
        for name, script in {
            'uname': 'printf "%s\\n" "$TEST_MACHINE"',
            'wslpath': 'printf "%s\\n" "$2"',
        }.items():
            path = tools / name
            path.write_text('#!/bin/sh\n' + script + '\n')
            path.chmod(0o700)
        for arch in ('x64', 'arm64'):
            # Deliberately not executable: bootstrap must copy and chmod for noexec mounts.
            (tools / f'agwinterm-hook-linux-{arch}').write_text(
                f'#!/bin/sh\nprintf "%s\\n" {arch} "$1"; cat\n')
        env = dict(os.environ, PATH=str(tools) + os.pathsep + os.environ['PATH'])
        for machine, expected in [('x86_64', 'x64'), ('aarch64', 'arm64'), ('arm64', 'arm64')]:
            result = subprocess.run(['sh', str(SCRIPTS / 'bootstrap.sh'), str(tools), "arg ' $value"],
                                    env=dict(env, TEST_MACHINE=machine), input='payload', text=True, capture_output=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(f"{expected}\narg ' $value\npayload", result.stdout)
        result = subprocess.run(['sh', str(SCRIPTS / 'bootstrap.sh'), str(tools)],
                                env=dict(env, TEST_MACHINE='riscv64'), capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Unsupported WSL architecture: riscv64', result.stderr)
        (tools / 'agwinterm-hook-linux-arm64').unlink()
        result = subprocess.run(['sh', str(SCRIPTS / 'bootstrap.sh'), str(tools)],
                                env=dict(env, TEST_MACHINE='aarch64'), capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('helper is missing:', result.stderr)


if __name__ == '__main__':
    unittest.main()
