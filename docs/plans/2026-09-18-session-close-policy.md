# Session-tab close confirmation (yeroo/agwinterm#304)

Product issue: [Add a three-state session-tab close confirmation policy](https://github.com/yeroo/agwinterm/issues/304).

Implementation source: distilled from [agushuley/agwinterm#12](https://github.com/agushuley/agwinterm/pull/12) (fork), scoped to #304 only.

## Problem (#304)

agwinterm keeps a session tab after its shell exits so the user can read the final output. The old
`confirm-close-session` boolean, when enabled, prompted on **every** UI close — including fully
exited tabs — which fights that model. Users also need a predictable focus story when confirming a
close on a background tab or a multi-selection.

## Configuration

| Value | Behaviour |
| --- | --- |
| `false` (default) | Never ask. |
| `live` | Ask only when the session has at least one **live shell** (`!HasExited` on any pane). |
| `true` | Ask before every user-initiated **session-tab** close. |

Terms in UI and docs: **live shell** / **exited shell** (not *interactive*, *zombie*, or *deactivated*).

Settings: General ▸ Sessions — dropdown **Off / Live shells only / Always**.  
`agwintermctl config get/set confirm-close-session` returns `false`, `live`, or `true`.  
Unknown tokens in `agwinterm.conf` leave the previous value (on first read, default `false`).

## Mapping: issue requirement → this PR

| #304 requirement | Implementation |
| --- | --- |
| Tri-state `confirm-close-session` | `TerminalConfig.ConfirmCloseSession` (`string`), parse + `ShouldConfirmCloseSession` in Core; Settings dropdown; ctl get/set validation. |
| `live` skips exited-only tabs | `SessionHasLiveShell` + `ShouldConfirmCloseSession("live", …)`. |
| Inactive target: show session + workspace, restore prior active | `ConfirmCloseOk(Ses)`: `SetActive` → `MessageBoxW` → restore prior if still present. |
| Batch: one dialog when any live; list live names, live count, exited count; all-or-nothing | `ConfirmCloseOk(IReadOnlyCollection<Ses>)`; sidebar **Close N Sessions**. |
| User paths: Ctrl+Shift+W, menus, sidebar context | **Close Session** / **Close N Sessions** use confirm helpers; **File ▸ Close Session** and `close_session` use `CloseActivePane()` — confirm only when the chord closes the **last pane** (whole tab), not when collapsing one pane of a split. |
| No change to shell-exit auto behaviour | `OnPaneProcessExited` unchanged (exited single-pane tabs stay open). |
| No change to control API | `Program.ControlHost.CloseSession` → `CloseSessionInternal` with no prompt. |

## Explicitly out of scope (not in #304)

| Item | Notes |
| --- | --- |
| `auto-close-session-on-exit` | Present in the fork PR; **not** included here — #304 excludes automatic shell-exit policy changes. |
| agterm tri-state parity | agterm still uses a single bool; this is an agwinterm UX improvement ahead of agterm. |
| Interactive `tests/integration/*.ps1` cases | Modal confirm is awkward to automate; follow-up if maintainers want a harness hook. |
| `Agwinterm.App` (WinUI host) | Not the shipping Win32 host; no change in this PR. |

## Tests in this PR

- `TerminalConfigTests`: parse `false` / `live` / `true`, reject legacy aliases (`interactive`, `exited`), `ShouldConfirmCloseSession` matrix.

## Manual checks (PR evidence)

1. `confirm-close-session = live`: exited tab closes from sidebar with no dialog; live tab prompts.
2. Background live tab: sidebar **Close Session** briefly activates it, dialog shows name + workspace, prior tab restored on Yes and No.
3. Multi-select with mix of live and exited: one dialog with counts; Cancel leaves all open.
4. `agwintermctl session close active` with `live` or `true` — no dialog.
