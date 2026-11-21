# History locator (episodic history memory, increment 1)

> Increment 2 adds the event log, `event_get`, purge-generation fencing and encryption at rest: see [event-log.md](event-log.md).
>
> Increment 3 adds hybrid (keyword + exact dense) search, the stemmed/trigram keyword variants and the evaluation tooling: see [history-hybrid-search.md](history-hybrid-search.md).

The history locator lets the agent find **when and where** something was said in its own
conversation history and read the exact source around it. It is a *locator*, not a source of
truth: a hit proves that something was said at a time, never that it is still true.

Design: [`docs/superpowers/specs/2026-11-21-episodic-history-memory-design.md`](superpowers/specs/2026-11-21-episodic-history-memory-design.md)
(Part I decisions D1–D10, Part II §7–§16).

## Components (Agent Host)

| Component | Role |
|---|---|
| `MessageStore` | Single writer boundary for `Messages`. One exclusive writer connection, read-only reader connections. Every mutation appends a content-free row to `HistoryJournal` and advances `Meta.JournalHead` in the same transaction. `Meta` also holds `DbInstanceId`, `RestoreGeneration`, `JournalPrunedThrough` and `PendingErasure`. |
| `HistoryAccessGate` | Async reader/writer lease. Recall tools hold a read lease for the whole call; purges take the write lease. |
| `HistoryProjector` / `HistoryProjectorService` | Applies the journal to the index (whole-channel recompute), prunes the journal, rebuilds and cuts over generations. Three consecutive failures halt it (`/health` → `degraded`). |
| `HistoryIndexDatabase` | One contentless FTS5 file per generation: `/app/state/history/history-index.<gen>.db`, pointer `history-index.current`. |
| `HistoryRecallService` | Search/read under one source snapshot: verifies every hit (full-content fingerprint + in-memory FTS5 re-match), merges the unindexed journal tail, reports coverage. |
| `HistorySearchTool` / `HistoryReadTool` | `history_search` and `history_read` (`aroundMessageId`). |
| `HistoryPurgeService` / `HistoryMaintenanceService` | Erasure workflow (§11), resumption of pending steps, periodic `VACUUM`. |
| `MemoryQuarantine` / `QuarantineAwareMemoryService` | D10: curated memories created in a purged window are hidden from the agent until the owner keeps or deletes them. |

The index stores no message text: `history_fts` is contentless (`contentless_delete=1`,
`secure-delete=1`) and `segment_map` holds only ids, channel, times, category, flags and
fingerprints. The agent always reads the text from `messages.db`.

## Configuration

`History` section (`appsettings.json`, `cortex.yml` `history:` or `History__*` env vars):

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Runs the projector and exposes the recall tools. |
| `TimeZone` | empty (machine local zone) | IANA zone for local dates, segment cuts, displayed times. Empty uses `TimeZoneInfo.Local`, i.e. the `TZ` environment variable of the container (UTC when unset); set an explicit IANA id such as `Europe/Berlin` to override. The effective zone is part of the config fingerprint, so changing `TZ` rebuilds the index. |
| `IdleGapMinutes` | `30` | Idle gap that starts a new segment. |
| `MaxSegmentWords` | `1500` | Segment word (FTS token) cap. |
| `DfCapRatio` | `0.2` | Relevance queries drop unquoted terms in more than this share of segments (only once the index has ≥ 50 segments). |
| `SearchTokenCap` | `1500` | Aggregate token cap per recall response. |
| `UnindexedTailMax` | `2000` | Max journal-tail rows matched directly before the projector catches up. |
| `BuildBatchMessages` | `2000` | Messages per bounded rebuild batch. |
| `QuarantineExtractionSlackHours` | `24` | Hours added after the last purged message of a quarantine window. |
| `QuarantineClusterGapHours` | `6` | Purged messages further apart form separate windows. |
| `VacuumIntervalDays` | `7` | Maintenance `VACUUM` interval. |
| `VacuumAfterPurgeRows` | `1000` | A larger purge schedules a `VACUUM` immediately. |
| `SmokeArtifactsDirectory` | `/app/state/eval/smoke` | Local smoke-eval workspace; its `runs` are destroyed on overlapping purge. |
| `EvalArtifactsDirectory` | `/app/state/eval/hybrid` | `history-eval` workspace; its `runs` (raw copies) are destroyed on overlapping purge ([details](history-hybrid-search.md#evaluation-run-artifacts-d3)). |

Changing `TimeZone` (or, when it is empty, the machine zone), `IdleGapMinutes` or `MaxSegmentWords` (or the schema, segmenter, folder
or tokenizer versions) changes the config fingerprint and triggers a rebuild; until it cuts
over, recall serves only the unindexed tail and reports `buildComplete=false`.

The Agent Host links SQLite 3.51 (SQLite3 Multiple Ciphers, `SQLite3MC.PCLRaw.bundle`) because
FTS5 `contentless_delete` needs ≥ 3.43. The startup log prints `SQLite runtime <version>`.
`MemoryMcp:Encryption:Enabled=true` is refused at startup (SQLite3MC cannot open SQLCipher files).

## Tools

- `history_search(query, order?, since?, until?, channel?, includeInternal?, includeEchoes?, limit?, cursor?)`
  - `order=relevance` (default): terms are OR-ed, very frequent unquoted terms are dropped; `order=time`: all terms required, exhaustive, oldest first, paginated by `cursor`.
  - `"quoted phrase"` matches the phrase; `word*` matches a prefix (stem ≥ 3 characters).
  - Internal rows (scheduler/system) are excluded unless `includeInternal=true`; assistant turns that themselves used a history tool (`recall_echo`) are down-weighted unless `includeEchoes=true`.
- `history_read(channelId?, aroundMessageId?, window?, since?, until?, untilMessageId?, includeInternal?, …)` — reads the exact source around a hit. A truncated range read keeps the newest lines and names `until` plus `untilMessageId` (a `(timestamp, id)` keyset) for the older ones, so lines sharing a timestamp are never skipped.

Every response is wrapped in a per-response random boundary token; stored text containing it is
escaped. Each line is labelled (`user`, `assistant`, `delivered`, `system`, `internal (not necessarily delivered)`, `recall_echo`),
and the footer states `coverage` (`from`, `to`, `indexedThrough`, `unindexedTailIncluded`,
`buildComplete`, `excludedTotal`). "No hits" means *no hits within that coverage*, never "this
was never said". `HistoryToolsSwitch` can hide the tools at runtime (`PUT
/api/tenants/{t}/history/recall-tools`) without stopping the projector.

## Health

`GET /health` (Agent Host) includes `subsystems.history`: `status` (`ok` / `degraded` when the
projector halted or erasure is pending), `enabled`, `toolsEnabled`, `sqliteVersion`,
`generation`, `buildComplete`, `buildInProgress`, `checkpointLag`, `journalBacklog`, `halted`,
`lastError`, `pendingErasureTasks`, `pendingErasureSteps`, `maintenancePendingTasks` (routine
weekly `VACUUM` work; it never degrades `status`, and it only runs while `history.enabled` is
true — pending erasure steps always resume). History never flips the top-level
`healthy` flag.

## Deletion, erasure and retention

All delete paths (Bridge history page, channel history, tenant import with *clear existing*,
`DeleteConversation`) go through `HistoryPurgeService`:

1. Under the write lease, one `messages.db` transaction deletes the rows (optionally inserts a
   replacement set), appends the journal purge entry, records the quarantine windows and a
   `PendingErasure` task listing the steps.
2. The steps run synchronously: `index` (purge segments from the current generation, drop an
   in-progress build), `messages-wal` (`wal_checkpoint(TRUNCATE)`), `tool-output` (all-channel
   purges delete every `/app/data/tool-output` spill file), `subagents` (terminal subagent tasks
   overlapping the purged range), `smoke-artifacts` (overlapping `runs/*` directories of the smoke and `history-eval` workspaces), `vacuum`
   (large purges) and `retired-generation:<n>`.
3. Steps that cannot finish (a busy WAL reader, an active subagent) stay in `PendingErasure`;
   `HistoryMaintenanceService` retries them. The Bridge reports `erasureComplete` /
   `cleanupPending` and the number of quarantined memories.

Retention: `messages.db` keeps history until deleted; the index and journal are derived and
pruned; spill files and subagent transcripts are deleted with purges (per-channel purges delete
exactly the spill files their tool results referenced, via the event log). Docker volume
snapshots and host backups are outside this workflow. With `Storage:EncryptAtRest`, `messages.db`
and the index generations are encrypted at rest; `subagents.db`, spill files and the memory
database are not (see [security.md](security.md)).

### Memory quarantine (D10)

Curated memories may have been extracted from deleted conversations. After a purge, memories
whose `CreatedAt` falls in a purged window (clustered with a 6 h gap, extended by 24 h of
extraction slack, capped at the purge time) are hidden from `memory_search`, subagent seeding,
consolidation and compaction. The Memory page shows them under **Needs review** with **Keep**
(make visible again) and **Delete**.

## Rollout

1. Deploy with `History:Enabled=false` (default) — the journal is written, nothing else runs.
2. Set `History__Enabled=true` (or `history: enabled: true` in `cortex.yml`) and restart. The
   projector builds generation 1 in bounded batches; watch `/health` until `buildComplete=true`.
3. Add one line to the agent's self-notes: *"history_search finds when/where something was said;
   check current facts elsewhere before acting on a hit."*
4. Run the smoke evaluation and the behaviour gate below before relying on the tools.
5. Rollback: `History__Enabled=false` hides the tools and the next erasure pass deletes every
   index generation.

## Smoke evaluation (§14.2, local only)

Runs inside the agent container against a backup copy of the live DB. Outputs go to
`/app/state/eval/smoke` and are **never committed**; run outputs contain ids and numbers only.

```powershell
$c = docker ps --filter "ancestor=cortex-agent:latest" --format "{{.Names}}" | Select-Object -First 1
docker exec $c dotnet Cortex.Contained.Agent.Host.dll history-smoke init
# write /app/state/eval/smoke/queries.jsonl (see queries.example.jsonl) and analysis-plan.md
docker exec -e SMOKE_LLM_ENDPOINT=... -e SMOKE_LLM_API_KEY=... -e SMOKE_LLM_MODEL=... $c dotnet Cortex.Contained.Agent.Host.dll history-smoke generate-queries
docker exec $c dotnet Cortex.Contained.Agent.Host.dll history-smoke pool      # optional: candidates for labelling
docker exec $c dotnet Cortex.Contained.Agent.Host.dll history-smoke register  # freezes queries + plan (sha256)
docker exec $c dotnet Cortex.Contained.Agent.Host.dll history-smoke run
```

Systems: (a) owner's oracle query, (b) model-written query through `history_search`, (c)
`LIKE` baseline over the same folded text with the model query's terms. `run` refuses to start
if the registered files changed. The gate passes when (b) beats (c) on excerpt recall at an
equal 1,500-word reading budget (two-sided sign test, p < 0.05, wins > losses).

## Behaviour gate (§14.3)

Paired tools-off / tools-on runs of 11 synthetic scenarios against a staging Bridge with
`History:Enabled=true`:

```powershell
$env:SCENARIO_EVAL_BRIDGE_URL = "http://localhost:5080"
$env:SCENARIO_EVAL_BRIDGE_PASSWORD = "..."
# plus the SCENARIO_EVAL_* actor/judge LLM variables used by the other scenario evals
dotnet test tests\Cortex.Contained.ScenarioEvals --filter "Category=HistoryBehaviourGate"
```

Thresholds live in `appsettings.scenarioevals.json` (`ScenarioEval:HistoryGate`). Results
(ids, booleans, rates with Wilson intervals, latencies — no text) are written to
`eval-results/history-gate-*.json`. Limitations: voice time-to-first-response is approximated
by API-channel greeting latency, and subagent over-search is judged from the final report only.
