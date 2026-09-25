# Notification categories for the unread-message badge

**Status:** implementing in `feature/notification-categories` from fork `main`. The session-exit notification is layered on a separate branch from this one. The existing shell-exit-hold fork PR ([#15](https://github.com/agushuley/agwinterm/pull/15)) remains unchanged until the stacked work is reviewed.

## Goal and contract

Make notification intent explicit with three categories: `ok`, `normal`, and `attention`. The sidebar unread-count badge uses green, yellow, and red respectively. The existing agent-status circle remains an independent signal; no exit-specific semaphore is needed.

An omitted category maps to `attention` **for display compatibility**: existing `agwintermctl notify`, raw `notify` control requests, and OSC 9/777 notifications stay red. New senders opt into yellow or green explicitly. Do not infer category from title, body, exit-code text, or agent status.

| Unread categories in one session | Badge color |
| --- | --- |
| Only `ok` | Green |
| `normal` and any number of `ok` | Yellow |
| Any `attention` (including legacy/untyped), with anything else | Red |

The count stays the total unread messages, not a count for the winning category. Focus/visit and `session seen` continue to clear both count and category together. A notification delivered to the currently focused pane does not create an unread badge under today's focus policy.

## Implementation locations

1. **Domain type and validation — `src/Agwinterm.Pty/`:** Add one `NotificationCategory` enum/contract with exact wire values `ok`, `normal`, `attention` and an explicit priority rule `attention > normal > ok`. Parse only those values; do not trim or silently coerce unknown strings. Missing value defaults to `attention`. Keep category separate from `AgentStatus`.
2. **CLI — `src/Agwinterm.Ctl/Program.cs`, `CtlUsage.cs`:** Add optional `agwintermctl notify BODY [--title TITLE] [--category ok|normal|attention] [--target ID]`. Serialize `args.category` only when supplied. Reject a missing or invalid option value before sending a request; preserve the existing output and target behavior.
3. **Control API — `src/Agwinterm.Pty/ControlServer.cs`, `ISessionHost.cs`:** Validate optional `args.category` on `notify`, pass the typed category through `ISessionHost.Notify`, and keep an omitted value backward-compatible. Update the test fake and no-op host implementations for the signature change. Preserve the existing `ok/error` reply and `tree --json` numeric `notifications` field. Do not reshape the bounded `events` log's `info` string as part of this change.
4. **Notification entry points — `src/Agwinterm.Win32/Program.ControlHost.cs`, `Program.Sessions.cs`, `Program.Services.cs`:** Pass the category from control `notify` into `OnNotified`. Pane-host OSC 9/777 remains `attention` unless a separate OSC extension is designed. Shell-exit hold sends `ok` for exit code `0`, `attention` otherwise. Replace the PR's `successfulExit` boolean with the category; title/body, toast, tray balloon, sound, flash, and focus suppression remain unchanged.
5. **Unread state and UI — `src/Agwinterm.Win32/Program.cs`, `Program.Services.cs`, `Program.Chrome.cs`:** Keep `Pane.Unread` for the count. Replace `UnreadSuccessfulExits` with the highest-priority unread category per pane; combine those priorities across the session's panes when painting the badge. Reset the category wherever `ClearUnread` resets the count. Keep the count badge in its current position and the agent-status circle unchanged. Expose category badge colors through the same picker/config pattern as agent-status colors; colored OS balloons remain out of scope.
6. **Documentation and skill — `docs/control-api.md`, `docs/user-guide.md`, `src/Agwinterm.Pty/AgentSkill.cs`, the shell-exit plan:** Document the three values, the legacy red default, mixed-category precedence, focus behavior, and `notify --category` examples. Update the shell-exit plan's success-only counter wording to the new category model once this feature lands.

## Data and compatibility impact

Unread counts and their color are transient per-window state, not persisted in session restore data. **No migration** is needed; restart discards old unread state as it does today. The control request only gains an optional field, so older clients continue to produce red badges. An older server ignores `args.category` and keeps its badge red; callers need a capability/version check before relying on yellow or green. Do not change the meaning of existing `AgentStatus`, OSC payloads, or the `tree` count.

## Verification

- Unit tests for exact category parsing, missing/invalid `args.category`, and priority aggregation; CLI tests for `--category`, missing value, and unchanged legacy syntax.
- Win32 integration: background `ok` → green, `normal` → yellow, `attention`/untyped → red; `ok + normal` → yellow; any `attention` → red. The badge number remains the total, and `session seen`/visit clears count and priority.
- Shell-exit integration: background exit `0` contributes `ok`, non-zero exit contributes `attention`; focused exit retains the in-terminal code but no unread badge. Agent status is unaffected.
- Check OSC 9/777 still uses the legacy red path and existing notification side effects still fire. Build/test Core, Pty, Ctl, and Win32; visually inspect the three badge colors in light and dark themes.

## Follow-up, not part of this implementation

`agliteterm` has its own `notify` handler in `src/remainder_runtime.h` and badge paint in `src/main.cpp`. If we want shared control-API parity, port the same optional category/default and aggregation there, then extend the shared conformance cases. Do not claim Lite supports the new option until that work is done.
