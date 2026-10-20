# Workspace settings and environment

Right-click a workspace and choose **Workspace Settings…** to open the themed Settings panel.
Its **General** tab edits the name, default directory, and default shell profile; **Environment**
edits workspace variables. **Browse…** opens the folder picker at the current default directory;
**Use application directory default** clears the directory override. An empty directory or **Use application default**
inherits the existing application behavior. Explicit session cwd/profile arguments override workspace
defaults. A missing saved profile falls back to the application profile; a missing saved directory
falls back to the application's new-session-directory mode.

Application-wide environment variables are edited directly in **Settings → Environment**.
Both editors use the same themed panel and validate before **Apply**. Workspace drafts are shared
between its tabs. **Cancel**, Escape, or closing the panel discards changes not yet applied.
Existing sessions are not restarted. Directory defaults are edited only in Workspace Settings,
not through duplicate Set/Clear actions in the workspace menu.
Shell profiles keep their existing `env` object in `profiles.json`; there is no new profile/session editor.

## Literal text editor

One assignment per line; the first `=` separates name and value. Everything after it is literal,
including spaces, additional equals signs, quotes, backslashes, `#`, `$VAR`, `%VAR%`, and `\n`.
There are no comments, escapes, substitutions, or evaluation. Empty lines between assignments are ignored.

```text
ROOT=C:\new\tools
TOKEN=abc=def
MESSAGE=plain with space '"and quotes"'
EMPTY=
MULTILINE<<END_ENV
abc=def
bcd=
qw=
END_ENV
```

`MULTILINE` contains three lines, without a final newline. A heredoc ends only at a line exactly
matching its marker. The separator before that marker is excluded; add an empty line before the
marker to retain a final newline. Choose another marker if its line occurs in the value. Missing
markers, duplicate names (case-insensitive), invalid names, and NUL are errors; nothing is applied.
Names cannot be empty or contain whitespace, `=`, `<`, or NUL. Markers cannot contain whitespace.

JSON stores values, not editor syntax or marker names:

```json
{
  "env": {
    "ROOT": "C:\\new\\tools",
    "MULTILINE": "abc=def\nbcd=\nqw=",
    "EMPTY": ""
  }
}
```

Opening the editor regenerates assignments and collision-free heredoc markers. Unchanged values
retain their exact line endings, even when another variable is edited; edited multiline values use LF.
Quotes and formatting are not shell syntax. These files are plaintext, not a secret store. Values are
not included in `tree`, change events, or diagnostics; explicit environment/settings reads return them.

## Merge and lifecycle

New local processes receive the normal inherited/fresh Windows environment, then these overrides,
from lowest to highest priority:

1. Global (`environment.json` in the application's config directory).
2. Selected shell profile (`profiles.json`, existing `env`).
3. Workspace defaults.
4. Session overrides.
5. Internal launch context and terminal identity.

Names are case-insensitive. `NAME=` means an empty value, not removal; deleting an override restores
the lower-priority value. There is no `unset`. `AGWINTERM*`, `AGW_*`, `TERM_PROGRAM`, and
`TERM_PROGRAM_VERSION` are reserved. Explicit command launches do not select a shell profile, but
still receive global, workspace, and session overrides. No shell expansion occurs during merge.

Session overrides survive restore/reopen and are copied on duplication. New split/scratch/overlay
processes receive the owning session's overrides. Moving an existing session does not change its
running processes; subsequent processes use the destination workspace's defaults. Surviving hosted
processes are reattached without changing their environment. This does not automatically configure
remote SSH processes or guarantee propagation into a WSL distro.

Workspace/session overrides are additive optional fields in window restore files. Existing directory
fields and profile `env` objects remain compatible; old files require no migration. Older application
versions can discard unknown fields when saving. Only overrides are persisted, never the full resolved
system environment. Global overrides are reread for new processes; an invalid external edit displays
a warning and keeps the last valid global overrides rather than applying a partial configuration.

## API and CLI

`environment.get` and `environment.set` accept `args.scope`: `global` (default), `profile`, `workspace`,
or `session`. Profile targets are exact profile names; workspace/session targets use their normal
target resolution. Set requires `args.env`, a JSON object of string values, and replaces that level's
overrides atomically; `{}` clears them. Null/non-string values and duplicate names are refused.
Session changes apply only to future child processes.

`workspace.settings` reads defaults with no `args.settings`; supplying that object replaces all
defaults (`defaultDir`, `defaultProfile`, `env`). Omitted directory/profile fields clear those defaults;
omitted `env` clears workspace overrides. Invalid settings leave everything unchanged.
`workspace.new` additionally accepts `defaultProfile` and `env`; `session.new` accepts `env`.
Existing `workspace.default-dir` calls retain their behavior and preserve other defaults.

```powershell
agwintermctl environment set --scope global --file global-env.json
agwintermctl environment get --scope workspace --target <workspace-id>
agwintermctl environment set --scope profile --target "PowerShell 7" --file profile-env.json
agwintermctl session new --workspace <workspace-id> --env-file session-env.json
agwintermctl workspace settings --target <workspace-id>
```

Environment files passed to the CLI contain just the string map, without an outer `env` property.
File arguments avoid exposing values in the CLI process command line; protect the files themselves
as appropriate. `workspace settings` only reads defaults; edit them in the UI or through the JSON API.
There are no separate workspace configuration files or workspace import/export commands.
