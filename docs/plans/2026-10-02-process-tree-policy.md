# Session process-tree policy (nested vs detached)

Tracking: [agushuley/agwinterm#22](https://github.com/agushuley/agwinterm/issues/22).

User request: default **process hierarchy visible in Task Manager** for normal sessions
(shell appears under the agwinterm UI process), with an explicit **opt-out** so agent
sessions and their Win32 descendants do not expose agwinterm in the parent chain.
**pty-host stays a separate process** — it is not reparented under the UI; only the
**session root shell** spawn policy changes.

## Semantics

| Value | Meaning |
|-------|---------|
| **`nested`** (proposed global default) | Root shell is created with a **parent-process override** pointing at the agwinterm UI process (handle duplicated from UI into the host at spawn). Task Manager shows the shell under `Agwinterm.Win32.exe`. |
| **`detached`** | Current behaviour: root shell’s parent is **pty-host** (or in-process host); UI is not in the ParentProcessId chain; no UI-owned grouping for descendants. |

**Not guaranteed:** WSL, ssh, docker, and other non–Win32 roots spawned by the shell.

Each session stores an **effective** policy after resolution (for `tree`, restore, duplicate,
and caller inheritance).

## Precedence (resolve on every `session.new`)

First match wins:

1. **`session.new` explicit** — JSON `"process-tree"` / CLI `--process-tree` (`nested` \| `detached`).
   Invalid or non-string → refuse before creating workspace/session (same style as `command-mode`).
2. **`--profile NAME`** — `processTree` on that entry in `profiles.json` (omit on profile → fall through).
3. **Caller session** — CLI sends `caller` (pane id, usually `$AGWINTERM_SESSION_ID`); copy the
   **effective** policy from the session that pane belongs to (parallel to landing in the caller’s
   workspace). Stale or missing caller → fall through.
4. **`agwinterm.conf`** — `default-process-tree = nested|detached`.

**`session.duplicate`:** copy **effective** policy from the source session (like cwd/profile).

## What changes in which component

| Component | Change |
|-----------|--------|
| **`agwintermctl`** | Optional `--process-tree`; forwards JSON; no spawn. |
| **`Agwinterm.Win32` (UI)** | Resolve precedence; persist on session; duplicate UI process/window handle for `nested`; pass policy + optional handle to backend. |
| **C# `ConPtyConnection` / in-process + C# `--pty-host`** | Spawn branch: parent attribute vs plain CreateProcess. |
| **Rust `native/agwinterm-ptyhost`** | Same spawn behaviour + **proto** field on create/start (must stay in sync with C# host). |
| **pty-host process itself** | Unchanged role; **not** nested under UI. |

If Rust host lacks proto support temporarily, refuse `nested` on `server-rust` with a clear error
or implement both hosts in one delivery — do not silently fall back to detached for `nested`.

## Configuration files

**Note:** there is no `sessions.conf`. Session defaults live in **`agwinterm.conf`** and
**`profiles.json`**.

### `agwinterm.conf`

```ini
# nested | detached — default for new sessions when profile and caller do not decide.
default-process-tree = nested
```

Wire through `TerminalConfig`, `config get` / `config set`, Settings if other session-host keys appear there.

### `profiles.json`

Per profile (camelCase, optional):

```json
"processTree": "nested"
```

Seed agent-oriented profiles with `"processTree": "detached"` where appropriate.

## Control API

- **`session.new`:** optional `"process-tree": "nested"|"detached"`.
- **`tree` (optional):** expose `"processTree"` on session nodes for scripts/debug.
- **`docs/control-api.md`**, `CtlUsage.Long`, `AgentSkill.cs` — document precedence and pty-host boundary.

**Conformance (`tests/conformance/control-api.json`):** add shared steps only if agliteterm
implements the same knob; minimum is an invalid-value refusal step once agreed cross-product.

## Restore / persistence

Persist effective `processTree` with session restore state (same class of data as profile/cwd),
so reopen and duplicate stay consistent.

## Implementation phases (fork → upstream)

### Phase 1 — Contract without spawn

- `default-process-tree` in `agwinterm.conf` + parser.
- `ShellProfile.processTree` + `profiles.json` schema.
- `ControlServer` / `NewSession` / ctl CLI argument + validation.
- Resolve precedence in `Program.ControlHost` including **caller session** lookup.
- Store effective policy on session; optional `tree` field.
- Unit tests for precedence resolver only.

### Phase 2 — C# spawn

- UI: duplicate parent handle for nested.
- `ConPtyConnection`: `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` when nested; de-elevate path too or document unsupported → refuse.
- In-process host first; then C# `--pty-host` if protocol already carries create args.

### Phase 3 — Rust pty-host

- Extend `native/agwinterm-ptyhost/src/proto.rs` and handlers in `creation.rs` / `conpty.rs`.
- Parity with C# host; CI already builds `native/Cargo.toml`.

### Phase 4 — Integration tests

- After spawn, read root shell **ParentProcessId** (Toolhelp/CIM): nested → UI pid band; detached → pty-host.
- Caller inheritance: detached tab → bare `session new` → second shell still detached.
- `session.duplicate` copies policy.

### Phase 5 — Upstream

- Rebase fork `main` on `yeroo/agwinterm`.
- Open **draft** PR to upstream `main` (Summary only per fork workflow).
- Call out default flip to `nested` and agent profile `detached` in release notes.

## Out of scope

- Job Object kill-on-close or TM grouping via jobs.
- Reparenting **pty-host** under the UI.
- Portable build `-g<sha>` naming (separate fork change in `installer/build-portable.ps1`).

## Acceptance

- Precedence table covered by unit tests (override, profile, caller, conf, duplicate).
- PPID integration tests for nested and detached on default `session-host` in CI (windows job).
- Invalid `process-tree` never leaves a partial session.
- Both spawn backends (in-process or rust, per CI matrix) documented in PR if only one lands first.
