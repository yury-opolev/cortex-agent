# Recall settings UI — design

**Date:** 2026-11-22 · **Branch:** `feat/episodic-history-memory` (base `43c5747`, increments 0–5 done, deployed as v0.2.339)
**Parent:** [2026-11-21-episodic-history-memory-design.md](2026-11-21-episodic-history-memory-design.md) (D3, D10, §4.2, §5.2, §11), [inc5 reranker](2026-11-21-inc5-reranker-design.md)
**Plan:** [../plans/2026-11-22-recall-settings-ui.md](../plans/2026-11-22-recall-settings-ui.md)

## 1. Goal

A **Recall** page in the Bridge web UI (per tenant: `/tenants/{id}/recall`) from which the owner configures
and observes the recall stack at runtime, without editing environment variables, compose files or
`cortex.yml` by hand:

1. History search on/off (`History:Enabled`).
2. Default history search mode (`keyword` / `hybrid`; `auto` = hybrid when embeddings are available).
3. Dense embeddings on/off (`History:Dense:Enabled`).
4. Reranker on/off (Bridge-wide `Reranker:Enabled`) and its per-tenant budget (`TopN`, `MaxDocChars`, `TimeoutMs`).
5. Default memory search mode (`MemoryMcp:Search:DefaultMode`: `dense` / `hybrid` / `keyword`).
6. Status: history index build progress, dense embedding progress, reranker health (agent and sidecar),
   event backfill, pending erasure, quarantined-memory count.
7. History deletion (one channel or all; everything, older than, or between two dates) with a preview, and
   the owner's **per-purge choice for dependent memories**: *review* (today's D10 quarantine, the default)
   or *delete* (erased physically with the history).
8. A review list of quarantined memories with content preview, creation time and the purge they came from;
   per-item and **bulk** keep/delete (bulk delete = one batched `DeleteManyAsync`).
9. A button to run the event backfill.

The shipped defaults do not change (History off, `auto` → keyword while dense is off, dense off, memory
`dense`, reranker off). The owner's choices (e.g. "hybrid by default" on one machine) are persisted locally
and never committed.

## 2. What exists today (investigation summary)

| Area | Finding | Consequence |
|---|---|---|
| `MemorySettingsStore` (agent) | In-memory overrides only, pushed by the Bridge (`UpdateMemoryConfig`, `CredentialsPusher.PushMemorySettingsAsync`) from Bridge `cortex.yml` (`memory:`); bridged into `IOptionsMonitor` by `IPostConfigureOptions` + `MemorySettingsChangeTokenSource`. Global, not per tenant. | Reuse the **post-configure + change-token** pattern; but persist per tenant on the agent side (below). |
| Agent `cortex.yml` / compose env | The agent's config is `appsettings.json` + `History__*` env vars; the installed compose file is rewritten by the Launcher's `ComposeFileSync` on every start. | Env/compose edits are not a persistence mechanism. |
| `SystemPromptStore` | File-backed JSON in the container volume, atomic temp+rename, per container = per tenant. | Precedent for an agent-side per-tenant JSON store. |
| `HistoryProjectorService` | Read `History:Enabled` once and **returned** when false: enabling needed a restart. | Must become a loop that idles while disabled and (re)starts when enabled. |
| `HistoryToolsSwitch` | Reads `IOptionsMonitor<HistoryOptions>.CurrentValue.Enabled` per call. | Tools gate is already live. |
| `HistoryMaintenanceService` | While disabled, prunes the journal to the head every tick (so it cannot grow). | A runtime re-enable must detect a pruned journal → rebuild (the startup check did this, the runtime apply did not). |
| `HistoryProjector.EraseAsync` | With History disabled, the erasure `index` step deletes every generation. | "Off" leaves the index until the next erasure pass (documented rollback semantics). |
| Dense | `History:Dense:Enabled` is in `HistoryConfigFingerprint`; `ApplyJournalAsync` compares fingerprints. | Toggling dense at runtime already triggers retire + rebuild; the UI must say so. |
| History search mode | `history_search mode=auto` = hybrid iff dense is on and the embedder is available; there was no configurable default. | Add `History:DefaultSearchMode` (query time). |
| Reranker | `RerankerOptions` read via `IOptionsMonitor` per call (budgets, timeout live). `Reranker:Enabled` is Bridge config; `RerankerSidecarLifecycle` reconciles at startup; `CredentialsPusher` pushes `RerankerGrant` on connect. **`BridgeSettingsWriter` never wrote a `reranker:` section**, so any UI save dropped a hand-set `reranker: enabled: true`. | Persist the switch in `cortex.yml` (add the section), converge sidecar + grant, report sidecar problems. |
| Memory search mode | `MemorySearchTool` reads `IOptionsMonitor<MemoryMcpOptions>.CurrentValue.Search.DefaultMode` per call. | A post-configure makes it live. |
| Purge | `HistoryPurgeService.PurgeAsync(scope)`; scope = channel + `OlderThan`; quarantine windows recorded with `ErasureId`; `HistoryDeletionResponse` says memories are "never deleted automatically". | Add `From`, preview, dependent-memory action; record the purged channel per window. |
| Quarantine | `MemoryQuarantine` (windows × creation times − kept); `KeepQuarantinedMemory(id)` one at a time; Memory page "Needs review". | Add bulk keep/delete and a review list with purge source. |
| Bridge UI | Alpine.js SPA (`app.html` + `js/pages/*.js`), tenant sub-pages Settings/History/Memory; no JS test harness; Bridge endpoint tests use static handler seams + route-metadata checks. | New `tenant-recall` page; handlers as `internal static` seams with an `IRecallAgentClient` seam over the concrete `HubClient`. |

## 3. Decisions

| # | Decision |
|---|---|
| R1 | **Agent-side persistence per tenant.** `RecallSettingsStore` keeps the owner's overrides in `<state>/recall-settings.json` (`/app/state`, outside the tool sandbox, survives restarts, image upgrades and Self-Update). Null member = configured default; set member = override. `ResetToDefaults` removes all. The Bridge is only the UI/API front for these. |
| R2 | **Live application via options.** `RecallHistoryPostConfigure`, `RecallRerankerPostConfigure`, `RecallMemorySearchPostConfigure` overlay the overrides on `HistoryOptions` (`Enabled`, `DefaultSearchMode`, `Dense.Enabled`), `RerankerOptions` (`History/Memory.TopN|MaxDocChars`, `TimeoutMs`) and `MemoryMcpOptions.Search.DefaultMode`; `RecallSettingsChangeTokenSource<T>` invalidates the monitors on every applied update. Validation uses the same bounds as startup validation (`RerankBudget` limits, timeout 100–30000). |
| R3 | **History on/off at runtime** — the projector service polls `History:Enabled` every tick: on → startup checks then resume/build; off → idle (tools already hidden). No container restart. The journal-pruned case is caught by a new guard in `ApplyJournalAsync` (`checkpoint < JournalPrunedThrough` ⇒ rebuild). Startup-check failures (invalid options, unsupported SQLite, wrong storage key) still halt until restart, as before. |
| R4 | **Off keeps the index** until the next erasure pass deletes it (existing rollback semantics); the UI notice says so. Re-enable rebuilds. |
| R5 | **Dense on/off** applies live through the existing fingerprint rebuild; the UI warns before and explains after (notice). |
| R6 | **`History:DefaultSearchMode`** (`Auto` default) decides what `mode=auto` means; explicit `mode=hybrid|keyword` from the model is still honoured. Shipped behaviour unchanged. |
| R7 | **Reranker switch is Bridge-wide** (`/api/recall/reranker`), persisted in Bridge `cortex.yml` (`reranker: enabled:` — new YAML section writer). Converge order: enable → sidecar `compose up` → grant; disable → withdraw grant → stop sidecar (no circuit-breaker trips while the container loads). Runs in the background; the page polls. |
| R8 | **Honest sidecar errors.** `RerankerSidecarLifecycle` records `{state, problem, at}`: image missing (`docker image inspect cortex-reranker:latest`; compose never pulls), compose failure (GPU/NVIDIA hint, CPU variant), container not running after start. Agent-side reranker health comes from `RerankStage.Snapshot()`. |
| R9 | **Per-purge dependent-memory choice** (`HistoryPurgeRequestDto.DependentMemories` = `review` default / `delete`). `delete` adds the erasure step `dependent-memories` (before `vacuum`) which erases the not-kept memories inside **this purge's** windows with one `DeleteManyAsync` (memory rows, content files, vectors, keyword rows + one FTS rewrite, WAL truncate; `secure_delete` from increment 0). Counted before the steps run; on failure the step stays pending, the memories stay quarantined and the step resumes on the next maintenance tick (idempotent). Memories the owner kept earlier are never deleted. This **relaxes D3/D10** as an explicit owner opt-in; the master spec text is amended. |
| R10 | **Preview before confirm.** `PreviewHistoryPurge` counts the messages and dependent memories with the same window builder and settings the purge uses, read-only. The Bridge builds both confirmation sentences (review/delete) from the preview; the confirmation names the chosen option and the count. |
| R11 | **Between dates.** `HistoryPurgeScope.From` (inclusive) next to `OlderThan` (exclusive); `From ≥ OlderThan` is rejected (agent `HubException`, Bridge `400`). The journal `purge` entry records `FromTs`, the tombstone payload `from`. Projector unaffected (whole-channel recompute). Spill-file erasure keeps its conservative rule (all-channel purge ⇒ all spill files). |
| R12 | **Review list with purge source.** `MemoryQuarantineWindows` gains `ChannelId` (idempotent `ALTER TABLE`); the list shows the latest purge whose window contains each memory (time, channel or "all channels"). Bulk keep is one transaction; bulk delete one batch; at most 1000 ids per request (Bridge and agent); ids not currently quarantined are skipped (a stale or forged list cannot delete other memories). |
| R13 | **Typed hub surface.** New `IRecallHub` (7 methods) composed into `IAgentHub`; `AgentHub.Recall.cs` is a thin adapter over `RecallControlService` (all logic testable without SignalR); missing service / invalid input → `HubException`. |
| R14 | **Bridge endpoints** under `/api/tenants/{tenantId}/recall/...` (settings GET/PUT, status, purge/preview, purge, quarantine list/keep/delete) and `/api/recall/reranker` (GET/PUT), all `.RequireAuthorization()` (cookie auth, SameSite=Strict). Disconnected agent → `503`; agent rejection (`HubException`) → `400`; transport failure → `503`. Existing history DELETE endpoints gain `?dependentMemories=`. |
| R15 | **UI**: new Recall tab/sidebar entry; History and channel "Clear" modals use the same preview + choice (shared `recall-purge.js`); the Memory page's Needs-review card links to the Recall page. Strings are plain and state exactly what happens. |

## 4. Architecture

```
Browser (Alpine) ──cookie──► Bridge RecallEndpoints ──IRecallAgentClient──► HubClient ──SignalR──► AgentHub.Recall
   tenant-recall.js           │  (static seams, tested)                                            │
   recall-purge.js            │                                                                    ▼
                              └─ RerankerSwitch ─► cortex.yml (reranker:)              RecallControlService
                                   │                                                    ├─ RecallSettingsStore ─► /app/state/recall-settings.json
                                   ├─ RerankerSidecarLifecycle ─► docker compose        │     └─ post-configures ─► IOptionsMonitor<History|Reranker|MemoryMcp>
                                   └─ CredentialsPusher.PushRerankerGrantAsync          ├─ HistoryHealthReporter, RerankStage.Snapshot, EventBackfillService.Status
                                                                                        ├─ HistoryPurgeService.PreviewAsync / PurgeAsync(dependentMemories)
                                                                                        │     └─ DependentMemoryErasureStep ─► IMemoryManagementService.DeleteManyAsync
                                                                                        └─ QuarantineReviewService ─► MemoryQuarantine (+ ChannelId per window)
```

Agent components (new unless noted):

- `Recall/RecallOverrides.cs` — `RecallOverrides` record (+ `Names()`, `With()`), `RecallSettingsValidation` (string modes, ranges).
- `Recall/RecallSettingsStore.cs` — load/validate/persist (atomic) + change token.
- `Recall/RecallPostConfigure.cs` — three post-configures, token source, `AddRecallSettings(stateRoot)`.
- `Recall/RecallStatusMapper.cs` — pure mapping to DTOs.
- `Recall/RecallControlService.cs` — facade + `RecallNotices`.
- `Memory/QuarantineReviewService.cs`, `Memory/DependentMemoryErasureStep.cs`.
- Modified: `HistoryOptions` (`DefaultSearchMode`), `HistoryRecallService.Hybrid` (resolution), `HistoryProjectorService` (runtime loop), `HistoryProjector` (pruned guard), `HistoryPurge` (`From`, `Problem()`, `HistoryPurgePreview`, `DependentMemoryAction`), `HistoryPurgeService` (`PreviewAsync`, dependent memories), `ErasureTask` (`DependentMemories` step), `MessageStore.History/Quarantine/Preview`, `MemoryQuarantine` (list, per-erasure ids, bulk keep), `EventBackfillService` (`Status`), `PurgeEvents` (`from`), `AgentHub` (+ `AgentHub.Recall.cs`), `Program.cs`.

Bridge components: `Recall/IRecallAgentClient.cs` (+ `HubRecallAgentClient`), `Recall/RerankerSwitch.cs`, `Recall/HistoryPurgeConfirmation.cs`, `Endpoints/RecallEndpoints.cs`, `Hub/HubClient.Recall.cs`, `RerankerConfigYamlWriter.cs`; modified `RerankerSidecarLifecycle` (status), `IRerankerComposeRunner`/`DockerComposeCommandRunner` (image probe), `BridgeSettingsWriter`, `CredentialsPusher` (public grant push), `HistoryDeletionResponse` (wording), `TenantEndpoints` (query param), `Program.cs`, `wwwroot` (page, helper, modals, help article).

Contracts: `Hub/RecallTypes.cs`, `Hub/IRecallHub.cs`, `HistoryPurgeTypes.cs` (`From`, `DependentMemories`, `DeletedMemories`), `IAgentHub` composition.

## 5. Behaviour details

- **Effective settings** = options after post-configure; `overridden[]` lists the persisted override names
  so the UI marks them *custom*.
- **Notices** after a save: History switched on (index builds in background, recent-only until complete),
  switched off (tools hidden now; index erased by the next history deletion; re-enable rebuilds), dense
  changed while History is on (rebuild), hybrid chosen while dense is off (stays keyword-only).
- **Status** polls every 5 s while the page is open (stopped in Alpine `destroy()`).
- **Purge confirmation** (exact strings, tested):
  - review: "This deletes N messages and erases them from the search index. M memories were created in
    that period. You chose Review: they are hidden from the agent until you keep or delete them under Recall → Needs review."
  - delete: "… M memories were created in that period. You chose Delete: they are erased permanently together with the history."
  - after the purge, `HistoryDeletionResponse.Message` repeats the choice and the actual counts (and, if the
    delete step is still pending, that the memories stay hidden until cleanup finishes).
- **Event backfill**: existing `POST /history/event-backfill`; the page confirms first (it pauses recall and
  deletions while it holds the write lease) and shows `EventBackfillService.Status` (running / last run since start).

## 6. Security and privacy

- All new endpoints require the cookie-authenticated owner; no new unauthenticated surface, no new ports.
- `recall-settings.json` holds no secrets and lives outside the agent's file-tool sandbox (`/app/data`), so the
  model cannot change its own recall configuration.
- Bulk delete only touches ids currently in quarantine; max 1000 ids; channel ids in purge bodies are
  length/control-character checked.
- Deletion with `delete` is irreversible: the UI requires a confirmation dialog naming the count; the default
  stays `review`.
- The reranker key never leaves DPAPI/agent memory/sidecar env; the switch endpoint only flips a boolean.

## 7. Out of scope / non-goals

- Changing other History options (time zone, segmentation, ranking weights, keyword variant) from the UI.
- Per-tenant reranker sidecars; the sidecar stays shared.
- Event provenance for memories (D10 remains time-window based).
- A JS unit-test harness (none exists; UI verified by `node --check`, build, and a manual smoke).
- Fixing the pre-existing gap that `memory: enabled:` is not written to `cortex.yml` (noted as a risk).

## 8. Risks

| Risk | Mitigation |
|---|---|
| Toggling dense or History on a large history triggers a long rebuild; recall is recent-only meanwhile. | Explicit UI warning + notice; Status shows build progress; keyword tail keeps working. |
| `delete` erases memories the owner might have wanted. | Default `review`; preview count; named confirmation; kept memories excluded; per-purge opt-in only. |
| Memory deletion step fails (e.g. busy `memory.db`). | Step stays pending and resumes; memories stay quarantined (never visible) until then; response says so. |
| Projector halted by a startup-check failure after a runtime enable. | Same as before (Status shows `halted` + error); restart after fixing. |
| Reranker GPU/driver problems vary. | Problems recorded with actionable hints; `docker logs cortex-reranker` pointer; agent circuit breaker unchanged. |
| `memory: enabled` is not persisted by `BridgeSettingsWriter` (pre-existing, out of scope). | Called out for a follow-up. |
