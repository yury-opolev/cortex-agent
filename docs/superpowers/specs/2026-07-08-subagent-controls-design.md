# Subagent controls — design

Status: approved (feature/subagent-controls)
Date: 2026-07-08

## Goal

Two related additions to the Agent Host subagent subsystem:

1. **Admin-editable max concurrent subagents (per tenant, applied live).** Today the cap is a fixed
   `AgentConfig.MaxConcurrentSubagents` (default 5, range 1–20) captured once in the
   `SubagentRunnerRegistry` constructor. Make it editable by the admin from the Bridge tenant-settings
   page and have the change take effect **without a container restart**.
2. **`sub_agent_stop` tool.** Give the agent a way to cancel a subagent it spawned — a running one
   (cancel its loop) or a still-queued one (drop it) — symmetric with the existing
   `sub_agent_start` / `sub_agent_read` / `sub_agent_send` family. **Tool only** — no Bridge UI.

"Per tenant" = per agent container (the runtime has no tenant-id threading; each tenant is its own
container with its own registry), matching the customizable-system-prompts precedent.

## Non-goals (explicitly out of scope)

- No Bridge UI / REST endpoint / hub method for *stopping* subagents — the stop surface is the agent
  tool only.
- Lowering the max never force-kills already-running subagents to fit the smaller cap.
- No change to the queued-task durability model (still SQLite-persisted, dequeued FIFO).

## Feature 1 — Live, admin-editable max concurrent subagents

### The core change: a live-mutable cap on the registry

`SubagentRunnerRegistry` becomes the single source of truth for the live cap:

- Replace the `readonly int maxConcurrent` field with a mutable, thread-safe backing value
  (`volatile int` / `Interlocked`), seeded from `AgentConfig.MaxConcurrentSubagents` at construction
  (unchanged default 5).
- `HasAvailableSlot` / `TryRegister` read the current value.
- Add `void SetMaxConcurrent(int value)`:
  - clamp to `[1, 20]` (same bounds as the config attribute);
  - set the backing value;
  - if the new cap is **higher** than the old, invoke a **dequeue callback** so waiting subagents
    start immediately.
- Add `int MaxConcurrent { get; }` (read) and `int ActiveCount` (already present) for tests/telemetry.
- **Dequeue callback wiring (no DI cycle):** the registry exposes
  `void SetSlotsOpenedCallback(Action callback)`. `SubAgentStartTool` — which already receives the
  registry and owns the queue machinery — registers `this.StartQueuedTasks` on itself in its
  constructor. The registry stays decoupled (it just calls back when slots open); the start tool owns
  what "start queued tasks" means.

### The settings flow (mirrors the existing `MaxSubagentRounds` path + a live push)

- **Contracts**
  - `BridgeConfig.MaxConcurrentSubagents` (int, default 5) — sibling of `MaxSubagentRounds`.
  - `AgentConfigUpdate.MaxConcurrentSubagents` (`int?`) — the live-push DTO field.
  - `AgentConfig.MaxConcurrentSubagents` — already exists (default 5, `[Range(1,20)]`).
- **Bridge**
  - `SettingsEndpoints` GET `/api/settings` → include `maxConcurrentSubagents` (from `BridgeConfig`).
  - `SettingsEndpoints` POST → if `request.MaxConcurrentSubagents.HasValue`: set
    `config.MaxConcurrentSubagents`, mark changed → `BridgeSettingsWriter.PersistSettingsToYaml`
    (restart durability) **and** push live:
    `await tenantRouter.GetDefaultClient()!.UpdateConfigAsync(new AgentConfigUpdate { MaxConcurrentSubagents = value }, ct)`
    when the agent is connected.
  - `SetupHelpers` settings-request DTO → add `int? MaxConcurrentSubagents` (`[JsonPropertyName("maxConcurrentSubagents")]`).
  - `BridgeSettingsWriter` → write `maxConcurrentSubagents: N` to `cortex.yml` (guard `> 0`, like the
    rounds sibling).
  - `tenant-settings.js` → numeric input (min 1, max 20) labeled "Max concurrent subagents", next to
    the max-rounds field; load from GET, send on save.
- **Agent**
  - Inject `SubagentRunnerRegistry?` into `AgentRuntime` (nullable/optional, matching the existing
    optional-dependency convention).
  - `AgentRuntime.UpdateConfigAsync` → `if (config.MaxConcurrentSubagents.HasValue) this.subagentRegistry?.SetMaxConcurrent(config.MaxConcurrentSubagents.Value);`
  - `SubagentRunnerRegistry` is built in `Program.cs` from `AgentConfig.MaxConcurrentSubagents`
    (unchanged); on restart the persisted YAML value seeds it, so restart-durability and live-push
    agree.

### Behavior

- **Raise** the cap → immediately dequeues and starts waiting subagents (via the callback).
- **Lower** the cap → only affects *new* `TryRegister` calls; already-running subagents finish
  normally (no lost work). `ActiveCount` may transiently exceed the new cap until runners drain.
- **Validation** — clamp to `[1,20]` on both the Bridge side (reject/clamp bad input) and the
  registry `SetMaxConcurrent` (defensive).

## Feature 2 — `sub_agent_stop` tool

### Cancellation core

- Each fired runner gets its own `CancellationTokenSource`, owned by the registry alongside the
  runner. `FireRunner` today passes `CancellationToken.None` to `Task.Run` and the parent token to
  `RunAsync`; replace with a **per-task CTS** whose token is passed to `runner.RunAsync` (the agent
  loop already honors a `CancellationToken`, so cancellation stops the loop between/within rounds).
- `SubagentRunnerRegistry`:
  - `TryRegister` stores the CTS with the runner (extend the stored value to `(runner, cts)`), and
    disposes it on `Remove`.
  - `bool TryCancel(string taskId)` — if a running runner is registered, cancel its CTS and return
    true; else false (queued/unknown handled by the tool via the store).

### Stop semantics

- **Running** (registered runner): cancel its token → the loop stops cooperatively; the `Task.Run`
  body catches `OperationCanceledException` and treats it as **Cancelled** (not Failed): set state
  `Cancelled`, free the slot (`Remove`), **notify the main agent** via the existing `onCompletion`
  callback with a `[Background task stopped]` result so it is not left awaiting a result, then
  `DequeueNext`.
- **Queued** (no running runner): mark state `Cancelled` in the store; `GetOldestQueued` must not
  return it. No slot to free.
- **Already terminal** (Completed / Failed / Cancelled): no-op; report the current state.

### New pieces

- `SubagentTaskState.Cancelled` — add enum member; `ToStorageValue` → `"cancelled"`; `Parse`
  `"cancelled"` → `Cancelled`. Audit `GetOldestQueued` / any `State == Queued` filters so a cancelled
  task is never dequeued.
- `SubAgentStopTool : IAgentTool` — `Name = "sub_agent_stop"`, param `task_id` (required); calls
  `registry.TryCancel` and/or updates the store for the queued case; returns a confirmation string
  (running-cancelled / queued-removed / not-found / already-terminal). Registered as a singleton in
  `Program.cs`. Update the `sub_agent_read` description that enumerates the tool family if needed.
- `FireRunner` `Task.Run` catch: distinguish `OperationCanceledException` (→ Cancelled path) from
  other exceptions (→ existing Failed path).
- Telemetry: `[LoggerMessage]` for stop-requested, cancelled-running, cancelled-queued, not-found,
  already-terminal.

## File-change checklist

**Contracts**
- `Config/BridgeConfig.cs` — add `MaxConcurrentSubagents`.
- `Hub/HubTypes.cs` — add `AgentConfigUpdate.MaxConcurrentSubagents`.

**Agent Host**
- `Agent/SubagentRunnerRegistry.cs` — live cap (`SetMaxConcurrent`, `MaxConcurrent`), slots-opened
  callback, per-task CTS storage, `TryCancel`.
- `Agent/SubagentTask.cs` — `Cancelled` state + storage/parse.
- `Agent/AgentRuntime.cs` — inject registry (nullable); apply `MaxConcurrentSubagents` in
  `UpdateConfigAsync`.
- `Tools/BuiltIn/SubAgentStartTool.cs` — per-task CTS in `FireRunner`; register `StartQueuedTasks`
  as the slots-opened callback; cancellation-aware `Task.Run` catch; ensure `GetOldestQueued`
  skips cancelled.
- `Tools/BuiltIn/SubAgentStopTool.cs` — **new** tool.
- `Program.cs` — register `SubAgentStopTool`; pass the registry to `AgentRuntime`.

**Bridge**
- `Endpoints/SettingsEndpoints.cs` — GET returns it; POST applies + persists + live-pushes.
- `SetupHelpers.cs` — request DTO field.
- `Setup/BridgeSettingsWriter.cs` — YAML write.
- `wwwroot/js/pages/tenant-settings.js` — UI field.

## Test plan (red/green TDD)

- **SubagentRunnerRegistry**
  - default cap from config; `SetMaxConcurrent` clamps `[1,20]`.
  - raising the cap invokes the slots-opened callback; lowering it does not.
  - `HasAvailableSlot`/`TryRegister` respect the live value (fill to cap → no slot → raise → slot).
  - `TryCancel` on a running task cancels the token; on unknown returns false; `Remove` disposes CTS.
- **SubAgentStopTool**
  - running → `Cancelled` + `onCompletion` invoked with a stopped result.
  - queued → `Cancelled`, not dequeued afterward.
  - unknown `task_id` → `Fail`.
  - already terminal → reports current state, no state change.
  - missing/blank `task_id` → `Fail`.
- **SubagentTask** — `Cancelled` round-trips through `ToStorageValue`/`Parse`; `GetOldestQueued`
  ignores it.
- **AgentRuntime.UpdateConfigAsync** — applies `MaxConcurrentSubagents` to the registry.
- **Bridge SettingsEndpoints** — POST with `maxConcurrentSubagents` persists to config and (when
  connected) calls `UpdateConfigAsync`; GET returns the current value. (Follow the existing
  `MaxSubagentRounds` endpoint tests as the template.)
- Runner honors the cancellation token (loop stops when cancelled).

All new/changed code: structured `[LoggerMessage]` telemetry, `this.`-qualified members, braces on
all blocks, `sealed` where applicable, per the repo C# style. `TreatWarningsAsErrors` is on.
