# Increment 3: Dense + hybrid history search — design

Date: 2026-11-21
Status: Draft for implementation.
Parent: [`2026-11-21-episodic-history-memory-design.md`](2026-11-21-episodic-history-memory-design.md) (Part I §3–§6, §4.2 binding commitments).
Builds on: increment 1 (keyword locator) and increment 2 ([event log](2026-11-21-inc2-event-log-design.md): purge-generation fencing, encrypted index generations, single writer) as implemented on `feat/episodic-history-memory`.
Scale path: [`../research/2026-11-21-memory-search-scale-path.md`](../research/2026-11-21-memory-search-scale-path.md).
Plan: [`../plans/2026-11-21-inc3-hybrid-history-search.md`](../plans/2026-11-21-inc3-hybrid-history-search.md).

---

## 1. Goal

`history_search` must also find what was said **in other words** (paraphrases, another language,
a vague description) without weakening any increment 1–2 guarantee:

- every hit is verified against the single source of truth (`messages.db`) at read time;
- deletion and recall stay linearizable, and deleted text — including its vectors — is erased physically;
- the index holds no readable transcript and is encrypted with the other index content;
- coverage, lag and fallbacks are stated honestly to the agent.

The increment ships the engine with reasonable defaults **and the evaluation tooling**. The owner-run
evaluation on raw data and the Gate H decision are owner tasks (§12), not part of the implementation.

## 2. Scope

**In scope**
- Deterministic transcript chunks per segment (~512 words, 64-word overlap), written atomically with their segments.
- Embedding jobs with generation, purge-generation and document-revision fencing; open-segment debounce.
- `IEmbeddingService` (MemoryMcp.Core): explicit `num_ctx`, `truncate=false`, a Qwen3 query instruction.
- Exact dense search inside the history index generation with filters applied before scoring; int8 copy plus full-precision rescore above 100k chunks.
- Fusion (RRF, weighted RRF, convex), BM25 priority for exact intent, near-duplicate worker collapse (MMR by author), optional recency prior.
- Keyword-quality ablation infrastructure: Snowball-stemmed shadow column and a trigram table.
- `history_search mode=auto|hybrid|keyword`, graceful keyword fallback, coverage of embedding lag.
- Embedder exposure hardening (compose, guard, logging).
- Erasure and encryption of vectors with integrity tests.
- Evaluation protocol **tooling**: dataset schema, paraphrase guard, metrics, paired statistics, splits, ablation grid and runner, pre-registration, CLI; a live embedder check; a hybrid-mode behaviour gate.

**Out of scope** (deliberately; §14)
- Running the owner evaluation, labelling, and the Gate H decision.
- Cross-generation vector reuse (a fingerprint change re-embeds everything).
- ANN indexes, an in-RAM vector cache, Matryoshka truncation, a reranker (increment 5), curated-memory hybrid search (increment 4).
- Indexing `Events` or tool payloads; dense search for `order=time` (which stays exhaustive keyword search).

## 3. Decisions

| ID | Decision |
|---|---|
| I3-1 | **Chunks are transcript windows, never stored.** A segment's rows render as deterministic lines `label: content [tools: a, b]` (honest labels, attachment markers, no timestamps). `HistoryChunker` cuts ~`ChunkWords`=512 whitespace words with `ChunkOverlapWords`=64 overlap (owner decision 2026-11-22: the same window as curated memory chunks; originally 256/32), prefers a message boundary in the second half of the window, and enforces `MaxChunkChars`=6000 (long tokens are hard-split). The index stores each chunk's member ids, word count, an `AllInternal` flag and `TextHash` = SHA-256 of its text — the chunk's **document revision**. Its text is recomputed from the source whenever it is needed. A segment's score is its **best chunk**. |
| I3-2 | **Embedding contract** (`MemoryMcp.Core`). `OllamaOptions.NumCtx` (default 4096) is sent with every `/api/embed` request together with `truncate=false`. Ollama's context-length error becomes `EmbeddingInputTooLongException`; a vector count mismatch throws. `IEmbeddingService.EmbedQueryAsync(query, instruction)` (default interface method) formats Qwen3's `Instruct: {task}\nQuery:{query}`; documents never get an instruction. `History:Dense:QueryInstruction`/`UseQueryInstruction` configure it and the grid ablates it. |
| I3-3 | **Vector storage: a plain `chunks` table plus an exact scan in .NET, not sqlite-vec `vec0`.** Reasons: (1) filters are joins on `segments` (channel, time) plus the chunk's `AllInternal` flag — a SQL `WHERE` gives exact pre-filtering for free, while `vec0` KNN caps `k` at 4096 and needs the slower exact-join fallback beyond (increment 0 measurement); (2) the history index runs on SQLite3 Multiple Ciphers with encrypted generations — a plain table is encrypted and `secure_delete`-erased by the same mechanisms already tested, with no second native library to load into that connection; (3) incremental-equals-rebuild and byte-level erasure tests stay simple; (4) the scale-path record measured an exact .NET scan at 1.5 ms (10k) to 67 ms (1M, all threads) — far inside budget at today's ~10k chunks. |
| I3-4 | **Exact search with filters before scoring.** `HistoryIndexReader.SearchDense` selects only candidate chunks (`segments.ChannelId`, segment time overlap, `AllInternal`), scores each with a SIMD dot product of unit vectors, and keeps the top `k` in a bounded heap (ties by lower `ChunkId`). Above `Int8Threshold` (100,000) embedded chunks it scans the int8 copy (`[float32 scale][sbyte×dims]`, stored for every vector) and rescores the best `k × Int8Oversample` (8) with float32. |
| I3-5 | **The job queue is the chunk table.** `IndexBatch` application writes chunk rows in the **same index transaction** as segments, FTS and checkpoint. A chunk without a vector is a job. When a channel is recomputed, chunks whose `TextHash` already has a vector in that channel keep it (unchanged history is never re-embedded); pending jobs keep their queue time. A channel's **last segment is "open"**: its jobs wait until `OpenSegmentDebounceMessages` (10) messages arrived since queueing or `OpenSegmentDebounceSeconds` (120) elapsed; closed segments are due at once. |
| I3-6 | **Publication fences** (§4.1, inc2 I2-10). An embedding round reads due jobs under the projector lock, recomputes every chunk text from one source snapshot (purge generation P) and drops jobs whose recomputed hash differs, embeds **outside every lock**, then publishes in one index transaction only if (a) the served generation is still the same database object, (b) the committed purge generation still equals P, and (c) per vector, `UPDATE … WHERE TextHash=@hash AND Vec IS NULL` (document revision). A failed fence discards the round's vectors. |
| I3-7 | **Read-side revision check.** A dense candidate is served only if its segment's member rows still exist and their fingerprint equals the stored segment fingerprint (which determines every chunk text). Otherwise the hit is dropped and the channel queued for recomputation, exactly like a keyword hit. So no vector ever answers for text that changed or was deleted. |
| I3-8 | **Embedder identity is part of the index fingerprint** (`model;dims=…;ctx=…`, plus chunking parameters and chunker version, only when dense is enabled). A different model rebuilds the generation; vectors of different spaces never mix. There is **no cross-generation vector reuse**: a fingerprint change re-embeds every chunk (minutes at current scale; keyword search is unaffected and the lag is reported). |
| I3-9 | **Embedder exposure.** History text goes only to a host listed in `History:Dense:AllowedEndpointHosts` (default `embeddings`, the in-compose sidecar) or to a remote endpoint that uses HTTPS **and** an API key. `OllamaHistoryEmbedder` re-checks this on every call (runtime endpoint changes cannot leak text). The compose `embeddings` service publishes no host port and runs with `OLLAMA_DEBUG=0`. An embedder failure never halts the projector: it is reported (`LastEmbeddingError`) and embedding backs off for 30 ticks. |
| I3-10 | **Fusion.** Default **RRF with k = 60** (literature default; the dev-set ablation decides). Weighted RRF and a convex combination of min-max-normalised scores are configurable. **Exact intent** — a quoted phrase, or an identifier-like token (letters with digits, inner underscore, camelCase, `#123`) — multiplies the keyword weight by `ExactIntentKeywordBoost` (2). Ties prefer the better keyword rank, then the lower segment id. |
| I3-11 | **Ranking tweaks.** Near-duplicate **worker** segments are collapsed by greedy MMR (λ = 0.7) where only segments of the same worker author (all shown lines `delivered` or all `internal`, per channel) penalise each other, and same-author segments with cosine ≥ 0.95 are dropped (`CollapsedDuplicates` reported). An optional **recency prior** multiplies scores by `1 + w·2^(−age/halfLife)`; `w = 0` (off) by default. |
| I3-12 | **Keyword variants** (ablation infrastructure; production default `Plain`). `segments_fts` gains a `stem` column (Snowball, applied before indexing) and a trigram table `segments_tri` exists in every generation; they are populated only for `KeywordVariant=Stemmed` / `Trigram` (fingerprinted). Stemming runs on **folded** tokens on both sides, so index and query always agree: Cyrillic → Russian; Latin → Danish **and** English stems (both kept when they differ, since a token's language is unknown). Relevance queries become `body : (exact) OR stem : (stems of plain words)`; time order keeps exact word semantics on `body`; the df cap reads the `body` column only. Trigram relevance matches each ≥ 3-character term as a substring. The re-matcher builds the same column/tokenizer layout in memory. **Library:** `libstemmer.net` 2.2.3 (MIT wrapper around the official Snowball 2.2 C# output, BSD-3; netstandard2.0, no dependencies). Lucene.NET's analyzers were rejected (prerelease, heavy); vendoring Snowball's generated C# is the fallback if the package ever goes stale. |
| I3-13 | **Tool contract.** `history_search mode=auto|hybrid|keyword` (default `auto` = hybrid when dense is on, the embedder is allowed and at least one chunk is embedded). The query is embedded **before** the read lease (a slow embedder never delays a purge), with a `QueryTimeoutMs` budget. Any failure falls back to keyword with a stated reason. The cursor binding includes the **effective** mode. Each response states `mode:` and a `dense:` coverage line; relevance hits carry `match keyword|semantic|both`. |
| I3-14 | **Evaluation as tooling.** Everything the §4.2 protocol needs is code with unit tests; nothing runs on raw data in CI. Outputs contain ids and numbers only (D6) and live under `/app/state/eval/hybrid` (never the repository). The holdout command refuses to run unless the dataset, analysis plan and grid were registered (SHA-256) and are unchanged. |

## 4. Schema (each index generation, `HistoryConfigFingerprint.SchemaVersion = 4`)

```sql
CREATE VIRTUAL TABLE segments_fts USING fts5(body, stem, content='', contentless_delete=1,
  tokenize='unicode61 remove_diacritics 2', prefix='3 4');            -- + secure-delete
CREATE VIRTUAL TABLE segments_vocab_col USING fts5vocab(segments_fts, 'col');   -- df of body terms
CREATE VIRTUAL TABLE segments_tri USING fts5(body, content='', contentless_delete=1,
  tokenize='trigram remove_diacritics 1');                             -- + secure-delete
CREATE TABLE chunks (
  ChunkId INTEGER PRIMARY KEY, SegId INTEGER NOT NULL, ChannelId TEXT NOT NULL,
  SegFirstMsgId INTEGER NOT NULL, Ordinal INTEGER NOT NULL,
  MsgIds TEXT NOT NULL,                 -- JSON, exact membership
  Words INTEGER NOT NULL, AllInternal INTEGER NOT NULL,
  TextHash TEXT NOT NULL,               -- document revision of the chunk
  IsOpen INTEGER NOT NULL,              -- chunk of the channel's last segment (debounce)
  QueuedAt TEXT NULL, QueuedMsgCount INTEGER NOT NULL DEFAULT 0, Attempts INTEGER NOT NULL DEFAULT 0,
  Vec BLOB NULL,                        -- unit float32, little-endian
  UNIQUE(SegId, Ordinal));
CREATE INDEX ix_chunks_seg ON chunks(SegId);
CREATE INDEX ix_chunks_hash ON chunks(TextHash);
-- Vec is a key column only so the partial-index predicate does not force a lookup into the vector rows.
CREATE INDEX ix_chunks_unembedded ON chunks(Attempts, QueuedAt, Vec) WHERE Vec IS NULL;
CREATE TABLE chunk_q8 (ChunkId INTEGER PRIMARY KEY, Q8 BLOB NOT NULL);   -- [float32 scale][sbyte x dims]
CREATE TRIGGER chunks_q8_cleanup AFTER DELETE ON chunks BEGIN DELETE FROM chunk_q8 WHERE ChunkId = OLD.ChunkId; END;
```

The int8 copy has its own table so the int8 scan never reads the float32 overflow pages, and the
embedding counters (total / pending / failed / oldest queued) are answered from covering indexes, never
from the vector rows. A reader holds one read transaction for its whole life, so a recall sees one WAL
snapshot even while the projector re-projects a channel.

The schema bump rebuilds every generation once on upgrade. The tables exist in every generation;
whether they are populated depends on the (fingerprinted) configuration.

## 5. Components

| Unit | Location | Responsibility |
|---|---|---|
| `EmbeddingInstruction`, `EmbeddingInputTooLongException`, `OllamaOptions.NumCtx`, `IEmbeddingService.EmbedQueryAsync` | `lib/memory-mcp/src/MemoryMcp.Core` | Embedding contract (I3-2) |
| `HistoryDenseOptions`, `HistoryRankingOptions`, `HistoryKeywordVariant`, `HistoryFusionMethod` | `Agent.Host/History/HistoryHybridOptions.cs` | `History:Dense`, `History:Ranking`, `History:KeywordVariant` |
| `HistoryTranscript`, `HistoryChunker`, `HistoryChunk` | `History/HistoryChunker.cs` | I3-1 |
| `VectorMath` | `History/VectorMath.cs` | Normalise, SIMD dot, BLOB codec, int8 copy |
| `HistoryStemmer` | `History/HistoryStemmer.cs` | I3-12 stems |
| `HistoryIndexDatabase` (+ `.Chunks.cs`), `HistoryIndexReader` (+ `.Dense.cs`) | `History/` | Schema v3, chunk writes, job reads, fenced publication, dense scan |
| `IHistoryEmbedder`, `OllamaHistoryEmbedder`, `HistoryEmbedderGuard` | `History/` | Embedder abstraction, exposure rule, dimension check |
| `HistoryProjector.Embedding.cs` | `History/` | Embedding rounds and fences (I3-6) |
| `HistoryProjectorService` | `History/` | Runs rounds after each tick; embedder failures back off, never halt |
| `HybridFusion`, `QueryIntent`, `NearDuplicateCollapse`, `RecencyPrior` | `History/HybridFusion.cs`, `HybridRanking.cs` | I3-10, I3-11 |
| `QueryParser.ForVariant`, `FtsRematcher`, `HistoryVerifier` | `History/` | Keyword variants at query time and in verification |
| `HistoryRecallService.Hybrid.cs` | `History/` | Hybrid relevance path, dense verification, coverage |
| `HistorySearchTool`, `HistoryResultRenderer` | `Tools/BuiltIn/`, `History/` | `mode`, `match`, `dense:` line |
| `HistoryHealthReporter` | `History/` | `/health → history.dense` |
| `EvalDataset`, `ParaphraseGuard`, `RankingMetrics`, `EvalStatistics` (extended), `EvalSplits`, `AblationGrid`, `AblationRunner`, `EvalWorkspace`, `EvalCorpus`, `HistoryEvalCli` | `History/Evaluation/` | Evaluation protocol tooling (§10) |
| `EmbedderCheck` (+ `history-smoke embedder-check`) | `History/Evaluation/Smoke/` | Live embedder check |
| `GateRunOptions`, scenario 12 | `tests/Cortex.Contained.ScenarioEvals/HistoryGate/` | Hybrid behaviour gate |

## 6. Data flow

**Projection** (unchanged triggers, §9.5 of the parent): the segmenter emits segments with their stem
body, trigram body and chunks; `Apply` replaces a channel's segments, FTS rows, trigram rows and chunk
rows in one transaction, carrying vectors by `TextHash` within the channel and stamping new jobs with
the projector clock.

**Embedding round** (`EmbedPendingAsync`, at most 8 per projector tick while vectors get published):

1. Under `applyLock`: take the served generation and up to `EmbedBatchSize` (16) due jobs.
2. One source snapshot: purge generation P; re-read each job's segment members; recompute the chunk; keep it only if its hash still equals `TextHash`.
3. Embed outside all locks. On `EmbeddingInputTooLongException` retry one by one to isolate the rejected chunk; its `Attempts` grows; after 3 it is reported as `failed` and no longer retried.
4. Under `applyLock`: generation fence, purge fence, then one transaction of revision-fenced updates (vectors are normalised; the int8 copy is written with them).

**Search** (`mode=hybrid`, relevance order):

1. Embed the query (instruction prefix) before the read lease; on any failure/timeout → keyword with a reason.
2. Read lease + source snapshot as in increment 1; purge-fenced or empty-vector indexes fall back to keyword.
3. Keyword list: top 50 BM25 segments on the variant's table, verified and re-matched (§10 of the parent).
4. Dense list: exact scan (top `Candidates` = 100 chunks) → best chunk per segment → revision check (I3-7) → shown lines = the best chunk's rows visible to the filter (`includeInternal`, message-level `since`/`until`).
5. Fuse (I3-10), apply the recency prior, collapse worker near-duplicates (I3-11).
6. Page: unindexed-tail keyword hits first (as in increment 1), then the fused list by offset. Echo-only segments are deferred and shown only when nothing else matches (or with `includeEchoes`).

## 7. Fencing, erasure and encryption

| Surface | Behaviour |
|---|---|
| Chunk rows and vectors | Deleted in the channel-recompute transaction that drops their segment (journal purge or edit). `secure_delete=ON` zeroes the cells; the erasure workflow's index step then runs FTS `optimize` and `wal_checkpoint(TRUNCATE)`. |
| Same-length edits | The segment fingerprint changes → recompute → new `TextHash` → old vector deleted; until then the read-side check withholds dense hits for that segment. |
| Retired generations | Unlinked with their vectors through `PendingErasure` (unchanged). |
| Encryption | Vectors live in the generation file, so `Storage:EncryptAtRest` encrypts them with the derived `history-index` key (I2-11). |
| Embedder | Receives text only through the allowed sidecar; it keeps no state (Ollama), `OLLAMA_DEBUG=0`. Provider-side copies of a remote embedder are out of scope (as for LLM providers). |
| Process memory | Query vectors and scan buffers are transient; there is no vector cache to invalidate. |

Integrity tests (`Category=Integrity`): incremental-with-embedding equals a rebuild for chunk rows,
and every stored vector equals the embedding of the chunk text recomputed from the source now (no
stale vector); a channel purge removes the float32 and int8 vector bytes from every index file; a
same-length edit removes the old vector bytes after the cleanup step; an encrypted generation holds
no plaintext vector bytes.

## 8. Configuration

`History:KeywordVariant` = `Plain` | `Stemmed` | `Trigram` (fingerprinted).

`History:Dense`: `Enabled` (false), `ChunkWords` (512), `ChunkOverlapWords` (64), `MaxChunkChars`
(6000), `QueryInstruction`, `UseQueryInstruction` (true), `AllowedEndpointHosts` ("embeddings"),
`OpenSegmentDebounceMessages` (10), `OpenSegmentDebounceSeconds` (120), `EmbedBatchSize` (16),
`QueryTimeoutMs` (3000), `Candidates` (100), `Int8Threshold` (100000), `Int8Oversample` (8).
Chunking values and the embedder fingerprint are part of the index fingerprint when `Enabled`.

`History:Ranking` (query time): `Fusion` (Rrf), `RrfK` (60), `KeywordWeight`/`DenseWeight` (1),
`ExactIntentKeywordBoost` (2), `MmrLambda` (0.7), `DuplicateSimilarity` (0.95),
`CollapseWorkerDuplicates` (true), `RecencyWeight` (0), `RecencyHalfLifeDays` (30).

`MemoryMcp:Ollama:NumCtx` (4096). `HistoryOptions.Validate()` covers every new value.

## 9. Tool and coverage contract

- `history_search` schema gains `mode` (`auto` default, `hybrid`, `keyword`); invalid values fail.
- Description additions: semantic search finds paraphrases/other languages in `auto`; use `mode=keyword`, a quoted phrase or an exact identifier when exact words matter; relevance pages cover the best candidates — use `order=time` for an exhaustive list. All §5.3 wording stays.
- Footer: `mode: hybrid (keyword + semantic)` | `mode: keyword`; `note: N near-duplicate worker segments collapsed`.
- Coverage: `dense: <state> embedded=E/T pending=P failed=F [oldestPendingSince=…Z]` where state is `on`, `off`, `not used: mode=keyword` / `order=time …`, `unavailable: <reason>` or `fallback: <reason>`. "No hits" still refers to the keyword coverage; dense coverage is partial while `pending > 0`.
- `/health → history.dense`: `state`, `embedded`, `total`, `pending`, `failed`, `oldestPendingQueuedAt`, `lastError`.

## 10. Evaluation protocol tooling (§4.2 Morita list)

| Protocol item | Tooling |
|---|---|
| Graded event/message-level labels (0–2), independent of segmentation | `EvalQuery.Judgments` (`messageId`, `grade`); metrics read the message ids of returned lines in reading order |
| Locator cost | `RankingMetrics.WordsToFirstRelevant` (words read up to the first relevant line) |
| Splits ~150 dev / 350 test or 5-fold CV | `EvalSplits.Assign` (SHA-256 of seed+id, 30% dev) and `EvalSplits.Fold` |
| Paired bootstrap 95% CI + randomisation test; non-inferiority | `EvalStatistics.PairedBootstrap`, `PairedRandomisationTest`, `NonInferior` (seeded, reproducible) |
| Per-category results as CIs only | `AblationRunner` comparisons: `category:*` slices carry no p-value |
| Pre-registered analysis plan | `history-eval init` writes the plan template and the grid; `register` hashes dataset, plan and grid; `holdout` refuses if anything changed |
| Different model families for generation and judging | `EvalDataset.Validate` rejects equal `queryModelFamily`/`judgeModelFamily` |
| Paraphrase guard (Jaccard ≤ 0.2, no shared rare token) | `ParaphraseGuard` + `history-eval guard` (numbers only) |
| Real recall requests as co-primary stratum | `stratum: real`, reported as its own paired slice |
| judged@10; LLM-vs-human κ ≥ 0.6 | `RankingMetrics.JudgedAt`; `EvalStatistics.CohensKappa` + `history-eval kappa` |
| Calibrated abstention; enumeration by set F1 | `RankingMetrics.Calibration` (Brier, ECE) over the best dense similarity; `SetF1` |
| `text` + `gold_filters`; oracle vs model-written filters | `EvalQuery.GoldFilters`, `ModelQuery`; grid configs `*-model-queries` |
| Raw data and sanitised copy | `--source` selects the `messages.db` copy; the plan template requires both runs |
| Ablations: RRF k ∈ {5,10,20,60}, weighted RRF, convex, instruction on/off, stemmed vs trigram, recency, collapse | `AblationGrid.Default()` |
| Provisional on a mostly-legacy snapshot | Stated by `holdout` and in the plan template |

**Dataset line** (`/app/state/eval/hybrid/dataset.jsonl`):

```json
{"id":"e001","text":"when does the night train to TownB leave","category":"lookup","stratum":"real",
 "judgments":[{"messageId":123,"grade":2},{"messageId":124,"grade":0}],
 "goldFilters":{"channel":"discord-dm","since":null,"until":null},
 "modelQuery":{"text":"night train TownB","filters":null},
 "queryModelFamily":"family-a","judgeModelFamily":"family-b","expectAbstain":false}
```

Categories: `lookup`, `paraphrase`, `cross_language`, `temporal`, `identifier`, `enumeration`, `abstention`.

**Metrics** per query and configuration: nDCG@10 (gain 2^grade − 1), recall@10, MRR, judged@10,
words to first relevant line, set F1 (enumeration), confidence for abstention calibration, first-page
latency; summaries add median locator cost, Brier/ECE and p95 latency.

**Run workflow** (owner, inside the agent container): `embedder-check` → `init` → label → `validate`
→ `guard` → `kappa` → `ablate --split dev` → choose and write `holdout` → finish the plan →
`register` → `holdout` → behaviour gate in hybrid mode → Gate H decision.

## 11. Behaviour gate

Same protocol as parent §14.3, re-runnable with the hybrid tools: `ScenarioEval:HistoryGate:SearchMode`
(`keyword` | `hybrid`) labels the run and its output file; in hybrid mode the runner waits
`EmbeddingSettleSeconds` (20) more after import. Staging for a hybrid run sets
`History:Dense:Enabled=true` and `History:Dense:OpenSegmentDebounceSeconds=0` so imported fixtures are
embedded at once. Scenario 12 (paraphrased recall: no content word shared with the fixture) measures
`citedSource`; it has no owner threshold yet and is reported as a rate with a Wilson CI.

## 12. Owner tasks (not part of the implementation)

1. `history-smoke embedder-check` on the production sidecar.
2. Build and label the dataset (≥ 500 queries for ~150 dev / 350 test, real-request stratum included), κ sample.
3. Dev ablation, choice of the shipped ranking settings (defaults stay unless the dev set shows otherwise).
4. Analysis plan, registration, holdout run on raw data and on the sanitised copy.
5. Behaviour gate in hybrid mode; Gate H decision; a v2 re-run after ≥ 4 weeks of live event capture.

## 13. Rollout

1. Deploy with `History:Dense:Enabled=false`. The schema v3 rebuild runs (keyword only, about a minute).
2. `history-smoke embedder-check`; enable `History:Dense:Enabled`; watch `history.dense.pending` drain (first full embedding: minutes on CPU at ~10k chunks).
3. Run the evaluation (§12); set the chosen `History:Ranking` values; run the behaviour gate in hybrid mode.
4. Keep `KeywordVariant=Plain` unless the stemming ablation wins on the dev set and holds on the holdout.

## 14. Risks and accepted gaps

- **Embedding inversion.** Vectors leak content; mitigated by treating them exactly like index text (encrypted, erased, byte-level tested, never logged).
- **Full re-embed on any fingerprint change** (deliberate, I3-8). Keyword recall is unaffected; dense coverage reports the lag.
- **Open-segment lag.** The live conversation's last chunk is up to 2 minutes / 10 messages behind for dense search; keyword search and the unindexed tail cover it.
- **Hybrid pagination is bounded** to the fused top candidates (50 keyword + 100 dense chunks); `order=time` remains the exhaustive path, and the description says so.
- **Rejected chunks.** A chunk whose tokens exceed `num_ctx` (pathological digit runs) is never truncated: it is reported as `failed`; raise `NumCtx` if it happens.
- **Stemming over-conflation** (Danish/English double stems) is ablation-only and off by default.
- **Author proxy.** MMR "by author" uses the honest line label per channel (`delivered`/`internal`) because the projector does not read `Events`; true producer identity arrives with event indexing.
- **Sidecar on another machine** requires HTTPS + key or an explicit allowlist entry; plain-HTTP bearer setups (e.g. the optional Caddy overlay on :11435) are refused by default.
- **Third-party stemmer package** (`libstemmer.net`, 3★): pinned, MIT/BSD-3, generated code only; vendoring is the fallback.

**Deliberately left out:** cross-generation vector reuse, an in-RAM vector cache, ANN/HNSW, Matryoshka
truncation, reranking, `Events` indexing, dense time-order search, automatic selection of ranking
settings in production, and any evaluation run in CI.

## 15. §4.2 commitment coverage

| §4.2 commitment | Where |
|---|---|
| Deterministic transcript, ~512-word chunks with overlap, best chunk per segment | I3-1, §6 |
| `num_ctx` explicit, assert no truncation | I3-2, `embedder-check` |
| Configurable Qwen3 query instruction on `IEmbeddingService`; ablation | I3-2, grid `hybrid-no-instruction` |
| Exact vector search, filters before scoring; int8 + rescore beyond 100k | I3-3, I3-4 |
| Embedder exposure (in-compose sidecar, no published port, auth for remote, logging off) | I3-9 |
| Open segment on a debounce | I3-5 |
| RRF k ablation {5,10,20,60}, weighted RRF, convex; BM25 priority for phrases/identifiers; ship dev choice | I3-10, §10 |
| Stemmed shadow column (Snowball ru/da/en) vs trigram ablation | I3-12 |
| MMR near-duplicate worker collapse; optional recency prior | I3-11 |
| Evaluation protocol (labels, locator cost, splits, statistics, pre-registration, model families, paraphrase guard, real stratum, judged@10, κ, abstention, enumeration F1, gold filters, raw vs sanitised, provisional legacy) | §10 |
| Behaviour gate re-run with hybrid tools | §11 |
| Inc2 carry-over: publications fenced on generation, purge generation and document revision; vectors encrypted and erased with the generation | I3-6, I3-7, §7 |
