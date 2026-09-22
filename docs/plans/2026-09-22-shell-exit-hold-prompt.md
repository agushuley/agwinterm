# Shell-exit hold prompt (agterm / libghostty parity)

**Status:** implemented on branch `feature/shell-exit-hold` (target: yeroo/agwinterm).

## Summary

When a **profile / login-shell** tab’s process exits, agwinterm keeps scrollback and appends an **in-terminal** hold message (`Press Enter to close the session.`). **Enter** removes the session tab via `CloseSessionInternal` — **not** `ConfirmCloseOk()` (boolean `confirm-close-session` unchanged for sidebar/menu closes).

## Config

- `hold-session-on-exit = true` (default) — profile shells only; not `session new --command`.

## References

- agterm libghostty hold: `GHOSTTY_ACTION_SHOW_CHILD_EXITED` → `closePrimaryPane` after key.
- agwinterm **#71** (split survivor), overlay `--wait` is a different UX (footer / any key).

## Out of scope

- `session new --command` / agterm #255.
- Tri-state close confirm (fork prototype #304).
