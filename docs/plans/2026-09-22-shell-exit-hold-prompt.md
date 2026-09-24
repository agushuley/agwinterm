# Shell-exit hold prompt (agterm / libghostty parity)

**Status:** partial prototype. The remaining work is tracked in [fork issue #14](https://github.com/agushuley/agwinterm/issues/14); fork branch: `feature/shell-fix-hold`.

## Summary

When a **profile / login-shell** tab’s process exits, agwinterm keeps scrollback and appends an **in-terminal** hold message (`Press Enter to close the session.`). **Enter** removes the session tab via `CloseSessionInternal` — **not** `ConfirmCloseOk()` (boolean `confirm-close-session` unchanged for sidebar/menu closes).

## Screenshot

![Exited session with the Enter-to-close prompt](../img/session-has-ended.png)

## Prototype scope

- Always on for profile/login-shell tabs; not `session new --command`. No config key.
- The ended tab keeps its scrollback until Enter dismisses it.

## Remaining work: exit outcome and session chrome

The prototype does not yet provide session semaphores, notifications, or tooltips for an ended process.

- Surface the exit outcome in the session chrome, including the process exit code.
- Exit `0` uses a green completed/success session semaphore.
- A non-zero exit uses a red ended-with-error semaphore, so a failed session is visible in the sidebar before dismissal.
- Add a tooltip and accessible name, for example `Session ended (exit 42)`.
- Add an appropriate notification for the completed or failed process outcome.
- Define whether terminal outcome is a dedicated semaphore state or a presentation of the existing agent-status model; it must not hide an independent agent status without an explicit rule.

## References

- agterm libghostty hold: `GHOSTTY_ACTION_SHOW_CHILD_EXITED` → `closePrimaryPane` after key.
- agwinterm **#71** (split survivor), overlay `--wait` is a different UX (footer / any key).

## Acceptance

1. Profile/login-shell tab: `exit` → hold lines in scrollback; **Enter** closes the session tab.
2. `confirm-close-session = true`: exit-hold **Enter** does not show the generic Close session dialog; manual UI close unchanged.
3. `session new --command` / direct command panes: no exit hold (pane may linger until manual close).
4. No configuration toggle — always on for eligible panes only.

### Completion acceptance

- A successful exit has the green success semaphore, while a non-zero exit has the red error semaphore.
- The sidebar tooltip and accessible name report the ended state and exit code.
- The process outcome produces the specified notification without replacing an independent agent status.

## Out of scope

- `session new --command` / agterm #255.
- Tri-state close confirm (fork prototype #304).
- **agliteterm** backport after fork release per [AGENTS.md](../../../AGENTS.md).
