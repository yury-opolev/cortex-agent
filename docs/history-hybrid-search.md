# Hybrid history search (episodic history memory, increment 3)

`history_search` combines the keyword (FTS5/BM25) locator of [increment 1](history-locator.md) with
**exact dense search** over deterministic transcript chunks, fused with reciprocal rank fusion. It
finds paraphrases and wording in another language, and it never weakens the increment 1 guarantees:
every hit is verified against `messages.db`, deletions are linearizable with recall, and the index
holds no readable transcript.

Design: [`docs/superpowers/specs/2026-11-21-inc3-hybrid-history-search-design.md`](superpowers/specs/2026-11-21-inc3-hybrid-history-search-design.md).

## How it works

| Step | Where | What happens |
|---|---|---|
| Chunk | `HistoryChunker`, called by `HistorySegmenter` | Each segment's transcript (`label: content [tools: …]`, no timestamps) is split into ~256-word chunks with a 32-word overlap, preferring message boundaries, with a 3,000-character hard cap. Chunk text is never stored; its SHA-256 (`TextHash`) is its revision. |
| Queue | `HistoryIndexDatabase.Apply` | Chunk rows are written in the same index transaction as their segments. A chunk without a vector **is** the embedding job. Unchanged chunk texts keep their vectors when a channel is recomputed. |
| Debounce | `ReadPendingChunks` | A channel's last (open) segment is embedded only after 10 new messages or 2 minutes; closed segments immediately. |
| Embed | `HistoryProjector.EmbedPendingAsync` | Reads due jobs, recomputes each chunk text from the source, embeds outside every lock, and publishes in one transaction only if the generation, the purge generation and the `TextHash` are unchanged. Rejected (too long) chunks count attempts and are reported as `failed` after 3. |
| Search | `HistoryRecallService` (hybrid) | The query is embedded (with the Qwen3 instruction) before the read lease. Keyword candidates are verified and re-matched; dense candidates come from an exact scan with filters applied before scoring, best chunk per segment, and are served only while the segment fingerprint matches the source. |
| Rank | `HybridFusion`, `NearDuplicateCollapse`, `RecencyPrior` | RRF (k = 60) by default; quoted phrases and identifier-like tokens double the keyword weight. Near-duplicate worker segments (same `delivered`/`internal` author) are collapsed by MMR. The recency prior is off by default. Fusion, exact-intent detection, the recency prior, vector math, folding and stemming are the shared engine in `MemoryMcp.Core.Retrieval` (also used by [memory search](memory-hybrid-search.md)). |

`order=time` stays exhaustive keyword search. `mode=keyword` forces keyword-only relevance.

## Configuration

`History:KeywordVariant` (`Plain` default; `Stemmed` adds a Snowball ru/da/en shadow column;
`Trigram` uses a trigram table) — part of the index fingerprint.

`History:Dense`:

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Chunk, embed and enable hybrid search. |
| `ChunkWords` / `ChunkOverlapWords` / `MaxChunkChars` | `256` / `32` / `3000` | Chunking (fingerprinted: changing them rebuilds and re-embeds). |
| `QueryInstruction` / `UseQueryInstruction` | Qwen3 retrieval instruction / `true` | Query-side instruction prefix (ablated). |
| `AllowedEndpointHosts` | `embeddings` | Hosts that may receive history text without an API key. |
| `OpenSegmentDebounceMessages` / `OpenSegmentDebounceSeconds` | `10` / `120` | Open-segment debounce. |
| `EmbedBatchSize`, `Candidates`, `QueryTimeoutMs` | `16`, `100`, `3000` | Job batch, dense candidates per query, query-embedding budget. |
| `Int8Threshold` / `Int8Oversample` | `100000` / `8` | Above this many embedded chunks the scan uses the int8 copy and rescores `k × 8` with float32. |

`History:Ranking`: `Fusion` (`Rrf`, `WeightedRrf`, `Convex`), `RrfK` (60), `KeywordWeight` / `DenseWeight` (1),
`ExactIntentKeywordBoost` (2), `MmrLambda` (0.7), `DuplicateSimilarity` (0.95), `CollapseWorkerDuplicates` (true),
`RecencyWeight` (0 = off), `RecencyHalfLifeDays` (30). Ranking settings apply at query time.

`MemoryMcp:Ollama:NumCtx` (default 4096) is sent with every embed request together with
`truncate=false`, so an over-long input fails instead of being embedded cut.

The embedder fingerprint (`model;dims;ctx`) is part of the index fingerprint: changing the model
rebuilds the generation and re-embeds every chunk. Keyword search keeps working meanwhile and the
coverage block reports the embedding lag.

## Embedder exposure

History text is sent only to the in-compose `embeddings` sidecar: it publishes no host port, runs with
`OLLAMA_DEBUG=0` (no request bodies in logs), and is reachable only on `cortex-internal`. A remote
embedder needs HTTPS and an API key (or an explicit entry in `AllowedEndpointHosts`); otherwise dense
search reports `unavailable: …` and nothing is sent. `OllamaHistoryEmbedder` re-checks this on every call.

## Coverage, health and fallback

Every `history_search` response states `mode: hybrid (keyword + semantic)` or `mode: keyword` and a
`dense:` line: `on`, `off`, `not used: …`, `unavailable: …` or `fallback: …` (embedder failed or timed
out, index withheld, nothing embedded yet), with `embedded/total`, `pending`, `failed` and the oldest
pending job. Relevance hits carry `match keyword|semantic|both`.

`/health → history.dense`: `state`, `embedded`, `total`, `pending`, `failed`, `oldestPendingQueuedAt`,
`lastError`. An embedder failure never halts the projector: embedding pauses for 30 ticks and retries.

## Erasure and encryption

Vectors (float32 in `chunks`, the int8 copy in `chunk_q8`) live in the generation file, so they are encrypted
with it (`Storage:EncryptAtRest`) and erased with it: a purge recomputes the channel inside the index
transaction (`secure_delete=ON`), then the erasure step optimizes and truncates the WAL. Integrity tests
check that the vector bytes of a purged or edited chunk are absent from every index file, and that an
encrypted generation holds no plaintext vector bytes.

### Evaluation run artifacts (D3)

Every `history-eval` run directory (`<workspace>/runs/<kind>-<split>-<stamp>`) holds a raw copy of
`messages.db` and index copies. Each one gets a `manifest.json` (`corpusFrom`, `corpusTo`) as soon
as the copy exists. The `smoke-artifacts` erasure step of every purge destroys the run directories —
of the smoke workspace (`History:SmokeArtifactsDirectory`) and of the `history-eval` workspace
(`History:EvalArtifactsDirectory`, default `/app/state/eval/hybrid`) — whose range overlaps the
purged range, and any run directory whose manifest is missing or unreadable (overlap cannot be
determined). Only the children of each workspace's `runs` directory are enumerated; the dataset,
analysis plan, ablation plan and pre-registration in the workspace root are kept. A workspace chosen
with `--dir` outside the configured directory is **not** covered: the CLI warns, and the owner deletes
its `runs` after use.

## Evaluation (owner tasks, local raw data only)

Nothing below runs in CI, and no output contains message text (D6). Run inside the agent container:

```bash
dotnet Cortex.Contained.Agent.Host.dll history-smoke embedder-check            # live sidecar: allowed, dims, num_ctx, no truncation
dotnet Cortex.Contained.Agent.Host.dll history-eval init                       # /app/state/eval/hybrid: templates + default grid
#   label dataset.jsonl (graded 0–2 message labels, gold filters, model-written queries, strata, families)
dotnet Cortex.Contained.Agent.Host.dll history-eval validate
dotnet Cortex.Contained.Agent.Host.dll history-eval guard                      # paraphrase guard: Jaccard ≤ 0.2, no shared rare token
dotnet Cortex.Contained.Agent.Host.dll history-eval kappa                      # LLM-vs-human κ ≥ 0.6 from judge-agreement.csv (messageId,llm,human)
dotnet Cortex.Contained.Agent.Host.dll history-eval ablate --split dev         # grid on the dev split; choose the config
#   set ablation.json "holdout": { "config": "...", "margin": 0.02 }, finish analysis-plan.md
dotnet Cortex.Contained.Agent.Host.dll history-eval register                   # freezes dataset, plan and grid (sha256)
dotnet Cortex.Contained.Agent.Host.dll history-eval holdout                     # one confirmatory run on the test split
```

Evaluation runs disable the 3 s query-embedding budget, and a hybrid configuration that would run
keyword-only (embedder down, index withheld) fails the run instead of being scored as keyword.

Then re-run the behaviour gate with `ScenarioEval:HistoryGate:SearchMode=hybrid` on a staging agent
with `History:Dense:Enabled=true` and `History:Dense:OpenSegmentDebounceSeconds=0`. The Gate H decision
is the owner's; a result on a mostly-legacy snapshot is provisional (re-run after ≥ 4 weeks of events).

## Rollout

1. Deploy with `History:Dense:Enabled=false` (only the schema v3 rebuild happens).
2. Run `history-smoke embedder-check`; enable `History:Dense:Enabled`; watch `history.dense.pending` drain.
3. Run the evaluation and the behaviour gate (above); ship the chosen ranking settings.
