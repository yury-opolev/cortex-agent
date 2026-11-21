# Episodic history memory: event log + hybrid search — design

Date: 2026-11-21
Status: Draft for review
Scope: chunk 1 of the "ambient Cortex" redesign. Research: the synthesis of 2026-11-20 (session research files `00`–`04`).

## 1. Goal

Give the agent a reliable way to find **when and where** something was said or done: in which channel, in which time window, and by which run. It can then read the exact content from a single source of truth. Concretely:

- Record conversations, tool calls with full (redacted) arguments and result digests, scheduled-task runs, subagent runs, timers and deliveries as an **append-only event log** in the existing `messages.db`.
- Build a **derived, disposable history index** of per-channel segments in a separate database. It uses **hybrid search**: BM25 (SQLite FTS5) + dense vectors (sqlite-vec) + reciprocal rank fusion, with an optional cross-encoder reranker.
- Put the hybrid engine in **MemoryMcp.Core**, so curated memory (`memory_search`) benefits too.
- Give the agent `history_search`, `history_grep`, an extended `history_read`, and `event_get`.
- Measure quality with a **frozen, sanitised copy of real history** (500 queries) before any search default changes.

This chunk stands on its own and is a prerequisite for the ambient-agent model. The event log, event IDs and projection pattern defined here are reused unchanged by later chunks.

## 2. Non-goals

- The ambient agent itself: inbox, context compiler, state snapshot, a new reply model.
- Changing what the LLM sees in its prompt, apart from one grounding line in self-notes at rollout.
- Changing how subagents or scheduled tasks run. Only their events are recorded.
- Voice improvements.
- A SafeExchange MCP. The frozen dataset is copied to SafeExchange by hand for now.
- Replacing `Messages`. It stays the store for the chat UI, seeding and `history_read` (dual write).

## 3. Decisions

| Topic | Decision |
|---|---|
| Hybrid search location | MemoryMcp.Core, shared by curated memory and history |
| Index contents | Conversation turns plus every tool call (full redacted args, result digest), including scheduled, subagent and timer runs |
| Indexing unit | Per-channel **segments**, used as locators; exact content is read from the source of truth |
| Segment text | Deterministic raw transcript (for BM25) plus an LLM digest (for vectors and display) |
| Source of truth | New `Events` and `EventBlobs` tables in the existing `messages.db`, written in the same transaction as `Messages` |
| Index storage | Separate disposable `history-index.db`, rebuildable from `Events` |
| Segmentation | Per channel: idle gap / local midnight / size cap. Worker runs are their own segments |
| Reranker | Separate optional llama.cpp sidecar (CPU) with fallback to RRF order. Off until the quality gate passes |
| Backfill | Explicit side tool (`--months N`), never automatic, not part of the core library |
| Secrets | Deterministic redaction before anything is persisted to events or indexed |
| History deletion | Must keep working: deletes cascade to events, blobs and the index through a tombstone |
| Quality gate | 500 queries over a **frozen, sanitised copy of real history**, stored locally plus SafeExchange, never in the public repo |
| Sanitisation | Deterministic mapping-based replacement of names, IDs **and places**. The LLM may only propose mapping entries |

## 4. Architecture

```
            ┌────────────────────────── Agent.Host ───────────────────────────┐
 inbound ──►│ AgentRuntime / SchedulerService / ProactiveMessageDispatcher /  │
 messages   │ SubagentRunner / SessionTimerService   (event emitters)         │
            │            │ Redactor (pure)                                    │
            │            ▼                                                    │
            │   MessageStore + EventStore ──► messages.db                     │
            │   (one connection, one write serialiser, one transaction)      │
            │      Messages | Events | EventBlobs        (source of truth)   │
            │            │ append signal / 60 s poll                         │
            │            ▼                                                    │
            │   HistoryProjector ──► HybridIndex (MemoryMcp.Core) ──► history-index.db
            │            │                 ▲  ▲                     (derived, disposable)
            │   DigestWorker ──────────────┘  │ embed: cortex-embeddings (Ollama, qwen3-embedding 0.6b)
            │                                 │ rerank: cortex-reranker (llama.cpp, optional)
            │   Tools: history_search · history_grep · history_read · event_get · memory_search
            └──────────────────────────────────────────────────────────────────┘
 Side tools (explicit): history-backfill · history-eval-snapshot · Cortex.Contained.HistoryEvals
```

### 4.1 Units

| # | Unit | Location | Responsibility | Depends on |
|---|---|---|---|---|
| 1 | EventStore | `Agent.Host/Storage` (shares `MessageStore`'s connection and serialiser) | Append events and blobs; read by `Seq` range or by channel and time; purge with a tombstone | SQLite |
| 2 | Event emitters | Existing write points (see 5.2) | Emit typed events with author, channel, run and causation | EventStore, Redactor |
| 3 | Redactor | `Agent.Host` | Pure function: secret patterns plus known vault values → `[REDACTED:kind]` | none (vault values injected) |
| 4 | HybridIndex | `MemoryMcp.Core` | Generic document index: FTS5 + sqlite-vec + metadata filters + RRF + optional rerank | IEmbeddingService, IRerankClient |
| 5 | RerankClient + `cortex-reranker` | Client in `MemoryMcp.Core`; sidecar in a new repo/submodule | Score (query, documents); circuit breaker and fallback | HTTP |
| 6 | HistoryProjector | `Agent.Host` hosted service | Events → segments → index; tombstones; rebuild | EventStore, HybridIndex |
| 7 | DigestWorker | `Agent.Host` hosted service | LLM digest per closed segment, then re-embed | ILlmClient, HybridIndex |
| 8 | History tools | `Agent.Host/Tools/BuiltIn` | Agent-facing search and read | HybridIndex, EventStore |
| 9 | history-backfill | `tools/Cortex.HistoryBackfill` (shipped in the agent image) | Import legacy `Messages` as events (explicit) | messages.db schema |
| 10 | history-eval-snapshot | `tools/Cortex.HistoryEvalSnapshot` | Export → sanitise → label → freeze | EventStore schema, Redactor, LLM |
| 11 | HistoryEvals | `tests/Cortex.Contained.HistoryEvals` | Run the quality gates on the frozen dataset | everything above |

Boundaries:
- MemoryMcp.Core knows nothing about Cortex events.
- The projector is the only translator from events to documents.
- Every existing `Messages` reader is unchanged.

## 5. Event log (source of truth)

### 5.1 Schema (in `messages.db`)

```sql
CREATE TABLE IF NOT EXISTS Events (
  Seq            INTEGER PRIMARY KEY AUTOINCREMENT,
  Id             TEXT NOT NULL UNIQUE,     -- 'ev_' + ULID (the evidence ID)
  Ts             TEXT NOT NULL,            -- UTC ISO-8601 (original time for legacy imports)
  TzOffsetMin    INTEGER NOT NULL,         -- user's local offset at Ts (Europe/Copenhagen)
  Type           TEXT NOT NULL,
  AuthorKind     TEXT NOT NULL,            -- user|main|scheduled_task|subagent|timer|tool|system|legacy
  AuthorId       TEXT,                     -- task id, subagent id, tool name, …
  ChannelId      TEXT,                     -- NULL for worker-internal events
  RunId          TEXT,                     -- one agent turn / worker run
  CausationId    TEXT,                     -- Id of the event that caused this one
  MessageRowId   INTEGER,                  -- Messages.Id when mirroring a chat row
  IdempotencyKey TEXT UNIQUE,              -- e.g. 'legacy:{Messages.Id}'
  Payload        TEXT NOT NULL,            -- JSON, redacted, ≤ 8 KB
  BlobId         TEXT                      -- full redacted large payload
);
CREATE INDEX IF NOT EXISTS ix_events_channel_ts ON Events(ChannelId, Ts);
CREATE INDEX IF NOT EXISTS ix_events_run        ON Events(RunId);
CREATE INDEX IF NOT EXISTS ix_events_msgrow     ON Events(MessageRowId);
CREATE TABLE IF NOT EXISTS EventBlobs (Id TEXT PRIMARY KEY, Bytes INTEGER NOT NULL, Content TEXT NOT NULL); -- ≤ 256 KB
```

The migration is idempotent `CREATE … IF NOT EXISTS` in `MessageStore` initialisation.

### 5.2 Event types and emit points

| Type | Emit point | Payload |
|---|---|---|
| `message_received` | `AgentRuntime`, with the inbound `Messages` save | `{text, attachments[]}` |
| `message_sent` | Final response delivery; `ProactiveMessageDispatcher` for `send_message` | `{text, via: reply\|send_message, evidence: [eventIds]}` |
| `tool_call` | `ExecuteToolRoundAsync`, before execution | `{tool, args}` (full, redacted) |
| `tool_result` | After execution | `{ok, digest (≤500 chars), error?}` + `BlobId` |
| `task_fired` | `SchedulerService` enqueue | `{taskId, description, instructions}` |
| `subagent_started` / `subagent_done` | Subagent runner | `{taskId, goal?, resultDigest}` |
| `timer_fired` | `SessionTimerService` | `{timerId, label}` |
| `internal_note` | Existing Category=1 assistant summaries of worker runs | `{text}` |
| `history_purged` | Delete paths (see 10) | `{channelId?, from, to}` (no content) |
| `legacy_import` | `history-backfill` only | `{role, category, text, toolNames[], argPreviews[]}` |

Rules:
- An event that mirrors a `Messages` row is written in the **same transaction**.
- `message_sent.evidence` is the set of `tool_result` IDs in the same `RunId`.
- Worker events carry the worker's `AuthorKind`/`AuthorId` and `RunId`, never `main`.
- Redaction happens before persistence.

## 6. HybridIndex (MemoryMcp.Core)

### 6.1 Storage (one SQLite file per collection)

- `docs(DocId PK, Text, EmbedText, StartTs, EndTs, Channel, Kind, Tags JSON, Meta JSON, UpdatedAt)`.
- `docs_fts`: FTS5 over the folded `Text` and `EmbedText`, tokenizer `unicode61 remove_diacritics 2`, no stemming.
- `docs_vec`: vec0, 1024-dim cosine, one row per chunk (512 words, 64 overlap). Channel, Kind, StartTs and EndTs are metadata columns, which needs sqlite-vec ≥ 0.1.6. Otherwise over-fetch ×10 and filter.
- `index_state(schema_version, …)`.

**Text folding** (applied to indexed text and queries): lowercase; ø→o, æ→ae, å→a, ß→ss; strip diacritics; normalise whitespace and punctuation. Raw text is kept for display.

### 6.2 Query pipeline

`SearchAsync(query, filters{since, until, channel, kind, tags}, limit, mode=hybrid|keyword|dense, order=relevance|time)`

1. **Keyword:** FTS5 `MATCH` with the folded terms joined by OR (quoted phrases kept), ranked by `bm25()`, top 50 within the filters.
2. **Dense:** embed the query, KNN top 50 within the filters.
3. **RRF:** `score = Σ 1/(60 + rank)` over the lists a document appears in.
4. **Rerank** (if enabled): top `TopN` (30) → reranker. On failure, timeout or open circuit, keep the RRF order and set `reranked=false`.
5. **Return:** doc, score, matched signals, FTS5 `snippet()` lines and metadata.
- `order=time` returns *all* filtered matches above a minimum fused score, sorted by time (for "pattern" queries), up to `limit`.

### 6.3 Curated memory

- `memory.db` gains `chunks_fts`, built at startup from the content files if missing (idempotent). It stays inside SQLCipher when encryption is enabled.
- `IMemoryService.SearchAsync` routes through the hybrid pipeline when `MemoryMcp:Search:Mode=Hybrid`.
- **The duplicate guard, `MemoryConsolidationService` and `MemoryCompactionService` keep using dense cosine similarity** through an unchanged similarity method, because their thresholds (0.90 / 0.70) are cosine-based.
- The default `Search:Mode` stays `Dense` until the quality gate (12) passes. The standalone MemoryMcp server (the user's Copilot memory MCP) gets the same option, off by default.

## 7. Reranker sidecar (`cortex-reranker`)

- **Repo and image:** new repo `cortex-reranker` (submodule `lib/cortex-reranker`), image `ghcr.io/yury-opolev/cortex-reranker:0.1`, mirroring `cortex-embeddings`: baked offline model, native run scripts, `model.env`, optional API key, lockdown docs.
- **Runtime:** `llama-server --reranking` on CPU, exposing `POST /v1/rerank` (Jina/Cohere shape).
- **Model:** Qwen3-Reranker-0.6B GGUF Q8_0. **Spike first:** confirm correct scoring with its prompt template in llama.cpp, otherwise use **bge-reranker-v2-m3** GGUF.
- **Compose:** service `reranker`, `mem_limit: 1.5g`, no GPU, internal network only, healthcheck.
- **Client config:** `MemoryMcp:Rerank:{Enabled=false, Endpoint, Model, TopN=30, TimeoutMs=3000, MaxDocChars=2000, ApiKey?}`.
  - Each document is sent as its digest plus its best-matching lines, truncated.
  - Circuit breaker: 3 consecutive failures → skip the reranker for 60 s.

## 8. HistoryProjector and segmentation

- **Trigger:** in-process append signal plus a 60 s poll. Processes events in `Seq` order after the checkpoint.
- **State** (in `history-index.db`): `projector_state(checkpoint_seq, schema_version, segmenter_version)`. A version mismatch means an automatic full rebuild. Also `projector_errors(seq, error, attempts)`.
- **Conversation segments**, one stream per `ChannelId`. A segment closes on an idle gap ≥ `IdleGapMinutes` (30), at a local midnight crossing, or above `MaxSegmentWords` (1,500).
- **Worker segments:** all events of one `RunId` whose `AuthorKind` is scheduled_task, subagent or timer. `Channel` is the delivery channel, or NULL; `Kind` is the author kind. Linked to the conversation by `CausationId`.
- **Legacy segments:** `legacy_import` events are segmented by `(channel, Ts)` with the same rules and **never merge into a live open segment**.
- **Document text:**
  - header `[<channel> · <Ddd DD Mon YYYY> · <HH:MM>–<HH:MM> <tz> · events <firstId>–<lastId> · <kind>]`;
  - lines `HH:MM <User>: …`, `HH:MM Emma: …`, `HH:MM Emma → tool(args ≤120 chars) ✓|✗ ⇒ <result digest ≤200 chars>`, `HH:MM [scheduled task "<desc>"] → send_message(<channel>) ✓`.
  - `Meta` stores the event ID range, the tool names, run IDs and the digest status.
- **Live updates:** the open segment is upserted to FTS on each event. It is embedded and queued for a digest when it closes.
- **Tombstones:** delete the overlapping segments (FTS, vectors, digest), re-project the surviving events of affected segments, and advance the checkpoint. This step is idempotent.

## 9. DigestWorker

- Queue: closed segments with `digest=pending`, lowest priority for legacy segments.
- Model: `History:DigestModel` (cheapest configured, Haiku-class). Throttled to `DigestPerMinute`, with retry and backoff.
- Prompt: 2–3 lines naming topics, named entities (products, people, projects, places) and decisions, using **only facts present in the segment**.
- Result: `EmbedText = digest + first ~300 words of raw text`, then re-embed.
- If the LLM is unavailable, segments stay keyword-searchable and dense-searchable on raw text.
- Digests are derived summaries, deleted with their segment, and never cited as evidence.

## 10. History deletion (preserved)

The existing operations stay, and all go through `MessageStore`:
- per channel;
- older than a date;
- per channel older than a date;
- delete-all;
- tenant import (delete-all + bulk insert).

Each operation, **in one transaction**:
1. delete the matching `Messages` rows;
2. delete the `Events` and `EventBlobs` in the same channel and time range;
3. append `history_purged{channelId?, from, to}`.

Then:
- Delete-all also deletes `history-index.db`, and the projector recreates it.
- After a tenant import, the imported rows are mirrored as `legacy_import` events automatically.

Invariant (tested): after any purge, deleted text is not findable through `history_search`, `history_grep`, `event_get`, the FTS tables or the vector tables.

## 11. Agent tools

- **`history_search`** `{query, since?, until?, channel?, kind?, order?, limit=8 (≤30)}`
  - Returns hits: channel, local date and time window, event ID range, kind, digest, best-matching lines, matched signals, score, `reranked`.
  - Description: a *locator*. Use `history_read`/`history_grep` for exact content. Digests are not evidence.
- **`history_grep`** `{pattern, regex?, channel?, since?, until?, limit}`
  - Searches `Events` (folded, case-insensitive) over message text, tool args and result digests. Default window is the last `GrepDefaultDays` (90).
  - Returns lines with the event ID and timestamp.
- **`history_read`** (existing) gains `aroundEvent` (ID ± N) and `includeTools` (renders tool calls, result digests and evidence IDs).
- **`event_get`** `{id}` returns the full redacted event, with blob content capped at 20 KB.
- **`memory_search`** gains `mode?`.
- All of these are read-only and available to the main agent and to subagents.

## 12. Quality gate: the frozen, sanitised dataset

### 12.1 Dataset

- **Source:** a one-time snapshot of real history, exported through the event format: `events.jsonl` and `memories.jsonl`. The window is `--from/--to`, defaulting to 2026-08-01 to the snapshot date. The window used is recorded in `manifest.json`.
- **Sanitisation** (`history-eval-snapshot sanitize`), **deterministic**:
  1. *Discover:* regexes plus an optional LLM pass **propose** `pseudonyms.json` entries (people, emails, phone numbers, addresses, Discord/account IDs, personal paths/URLs, **places**), with variants and occurrence counts.
  2. *Review:* the user edits and approves each entry (`replace` or `keep`). Pseudonyms keep the same gender and form (Yury → Sam, Юрий → Сэм), and places map consistently (Søborg → Manchester).
  3. *Replace:* deterministic, whole word, case-preserving, longest match first, with listed spelling, transliteration and inflection variants. Applied to every text field, then the production Redactor runs.
  4. *Verify:* a deterministic leak check fails the run if any mapped original or known identifier remains. An LLM audits about 200 random segments. `--llm-fix` (off by default) may only **propose new mapping entries** for residual matches; the text itself is only ever changed by the deterministic replacer.
- **Labels** (`label`), produced after sanitisation:
  - An LLM extracts facts pinned to event IDs.
  - 500 queries: 400 history and 100 curated memory, including about 60 derived from real past recall requests.
  - Categories: exact entity 20%, paraphrase 25% (word-overlap guard), temporal 15% (fixed reference date per query), multilingual 10%, tool/provenance 10%, channel-scoped 10%, no-answer 10%.
  - Pooled relevance (top 10 of each method, judged 0–2) plus a 10% manual review.
- **Freeze** (`freeze`): `v1/` with a sha256 `manifest.json`. The tool refuses to modify a frozen version; changes create `v2/`.
- **Storage:** `/app/state/eval/frozen/v1/`, a host copy, and a hand-made copy in SafeExchange. The mapping file stays in `/app/state/eval/private/` and is **never** stored with or next to the dataset. Nothing derived from real data is committed to the public repo.
- **Split:** 400 dev, used for tuning (RRF k, chunk size, folding, idle gap), and 100 holdout, used only for gate decisions.
- **Caveat:** history from before this chunk has no tool results, so provenance coverage is thin. A `v2` snapshot after a few weeks of live event capture will fix that.

### 12.2 Harness and gates (`tests/Cortex.Contained.HistoryEvals`)

- Verifies checksums, then loads `v1` into temporary databases through the real EventStore → projector → index pipeline.
- Runs dense, keyword, hybrid and hybrid + rerank.
- Reports recall@5/@10, MRR and nDCG@10 per category, the false-positive rate on no-answer queries, and p50/p95 latency.
- Skips with a clear message if the dataset is absent. It is excluded from the Self-Update test gate, like the existing evaluation suites.
- **Gate H (hybrid becomes the default for curated `memory_search`; confirms the history mode):** on the holdout, hybrid ≥ dense on nDCG@10 and recall@5, and no category is worse than −5% relative. `history_search` launches in hybrid mode because it is a new tool with no prior baseline. If Gate H fails for the history queries, `history_search` switches to whichever mode scores best.
- **Gate R (reranker enabled):** +≥3 points nDCG@10 over hybrid on the holdout, with p95 search latency ≤ 2.5 s on this machine's CPU.

## 13. Redaction

- Deterministic patterns: provider key formats (`sk-…`, `sk-ant-…`, `GOCSPX-…`, `ghp_…`/`github_pat_…`, `xox…`, AWS-style keys), JWTs, `Authorization:` headers, `password=`/`secret=`/`token=` pairs, PEM blocks, and long high-entropy tokens. Known secret values from the Bridge-pushed credentials and the vault are matched exactly.
- Output: `[REDACTED:<kind>]`.
- Fails closed: on error, the payload is replaced by `[REDACTION_ERROR]` and a warning is logged.
- Applied to event payloads, blobs, segment text, digests and the eval snapshot.

## 14. Error handling

- An event write failure fails the enclosing `Messages` save, as today, so the two can't drift apart.
- Projector failures never affect chat. They retry with backoff from the checkpoint; a poison event is quarantined in `projector_errors` after 3 attempts.
- Embedding sidecar down: keyword indexing continues, and a re-embed queue fills in the vectors later.
- Digest or reranker down: results degrade and say so (`digest=pending`, `reranked=false`).
- Corrupt index or version change: automatic rebuild.
- Backfill and snapshot tools use WAL, a busy timeout and short batches, so they are safe to run alongside the live agent.

## 15. Configuration

```yaml
history:
  enabled: true
  idleGapMinutes: 30
  maxSegmentWords: 1500
  digestModel: claude-haiku-5.5
  digestPerMinute: 20
  toolResultDigestChars: 500
  blobMaxBytes: 262144
  grepDefaultDays: 90
memory:            # existing section, extended
  search: { mode: dense }          # → hybrid after Gate H
  rerank: { enabled: false, endpoint: http://reranker:8080, topN: 30, timeoutMs: 3000, maxDocChars: 2000 }
```

These use the existing YAML and `IOptionsMonitor` patterns. Runtime-mutable settings follow `MemorySettingsStore`.

## 16. Testing

- **Unit tests** (xUnit + NSubstitute; synthetic slice of about 50 events, committed):
  - Redactor;
  - text folding;
  - segmentation (idle gap, midnight, size, worker runs, legacy events not merging into live ones);
  - RRF;
  - rerank fallback and circuit breaker;
  - purge and re-projection, including the not-findable invariant;
  - backfill idempotency;
  - sanitiser determinism, leak check, and the proposal-only behaviour of `--llm-fix`;
  - tool argument validation.
- **Integration tests:** a real SQLite + FTS5 + sqlite-vec HybridIndex; EventStore → projector → `history_search` end to end; the curated-memory FTS migration on an existing `memory.db` copy.
- **Evaluation:** `Cortex.Contained.HistoryEvals` on the frozen v1 dataset (12).

## 17. Rollout

1. Spike: Qwen3-Reranker in llama.cpp, the sqlite-vec metadata-column version, and FTS5 in the bundled SQLite/SQLCipher provider.
2. Ship the event log (dual write), the Redactor, HybridIndex, the projector, the digest worker and the tools. History search starts in `hybrid` mode without the reranker; curated memory stays `dense`.
3. Run `history-backfill --months N` by hand.
4. Build and freeze dataset v1 (sanitise → review the mapping → label → freeze), then copy it to SafeExchange.
5. Run the evaluation, apply Gate H, then switch `memory.search.mode` to `hybrid`.
6. Deploy `cortex-reranker` and apply Gate R before enabling it.
7. Add one grounding line to self-notes: "When unsure whether something was said or done, search history and verify instead of guessing or apologising."

## 18. Fit with the ambient model

- `Events` is the ambient event log. Its envelope (author, channel, run, causation, evidence) is the one the ambient design needs.
- Event IDs are the evidence IDs for provenance labels and `event_get` (later `evidence_get`).
- The projector pattern (checkpoint, tombstones, rebuild) is how the ambient context compiler and state snapshot will be built.
- Segment digests are the first "sleep-time" consolidation job, and the input to the future rolling digest.
- `history_search`/`history_read`/`event_get` are the ambient recall tools, unchanged.
- Subagents and scheduled tasks keep their current loop. Only their events are recorded, which is all the ambient boundary needs.

## 19. Risks and open items

- Qwen3-Reranker support in llama.cpp is unconfirmed; the fallback is bge-reranker-v2-m3. Measured in the spike.
- The sqlite-vec version bundled in the image may lack metadata columns; the fallback is over-fetch and filter.
- FTS5 availability in the SQLCipher provider build used by MemoryMcp.Core needs confirming in the spike.
- CPU embedding throughput for backfill and rebuilds isn't measured yet (estimated 10–30 minutes for about 30k events).
- Storage growth of `Events` and `EventBlobs` is estimated at 5–20 MB a month and needs checking against real volume after a week.
- Sanitisation quality depends on mapping review. The leak check is deterministic, but contextual identifiers (e.g. unusual routines) can't be fully removed. Hence local-only storage.
