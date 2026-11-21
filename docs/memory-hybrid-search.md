# Hybrid memory search (episodic history memory, increment 4)

`memory_search` can rank curated memories by **exact keywords** (FTS5/BM25), by **meaning** (exact cosine
search, as before) or by both fused with reciprocal rank fusion. The keyword engine, fusion, folding,
stemming and the evaluation statistics are the ones proven by hybrid history search
([history-hybrid-search.md](history-hybrid-search.md)), now shared from `MemoryMcp.Core`.

**Default behaviour is unchanged:** `MemoryMcp:Search:DefaultMode` is `Dense`, so `memory_search` without
a `mode` searches by meaning and renders exactly as before. Hybrid becomes the default only after the
owner's evaluation (Gate H for memory, below).

Design: [`docs/superpowers/specs/2026-11-21-inc4-memory-shared-engine-design.md`](superpowers/specs/2026-11-21-inc4-memory-shared-engine-design.md).

## How it works

| Step | Where | What happens |
|---|---|---|
| Index | `SqliteVecMemoryStore` (`memories_fts`) | Every memory has one row in a contentless FTS5 table: its folded title + content (`body`) and their Snowball stems (`stem`). The row is written in the same transaction as the memory's chunks and vectors; the text itself stays in the `.memory.data` file. |
| Backfill | `SqliteVecMemoryStore.InitializeAsync` | On the first start after the upgrade (and whenever the folding/stemming fingerprint changes) every existing memory is indexed from its content file. |
| Keyword list | `IMemoryStore.KeywordSearchAsync` | BM25 over a safe `MATCH` expression (`KeywordQuery.Build`): quoted phrases stay phrases, `word*` is a prefix, other words are OR-ed; `KeywordVariant=Stemmed` also matches the stem column. |
| Dense list | `IMemoryStore.SearchDenseAsync` | The previous exact cosine search (best chunk per memory). |
| Filters | `MemorySearchFilter` | Tags and excluded ids are applied **before** scoring in both lists. Quarantined memories (D10) are excluded this way by `QuarantineAwareHybridSearch`. |
| Fuse | `HybridMemorySearch`, `RankFusion` | RRF (k = 60) by default; weighted RRF and convex combination are configurable; quoted phrases and identifier-like tokens double the keyword weight. |
| Fallback | `HybridMemorySearch`, `memory_search` | Hybrid falls back to keyword when the query cannot be embedded (embedder down, error, `QueryTimeoutMs`) and to dense when the keyword index is unavailable. The response says so. |

Consolidation, compaction, the ingest duplicate guard and subagent context injection keep using the
dense-only `FindSimilarAsync`: their thresholds and candidate sets are unchanged.

## The tool

`memory_search` gains `mode`: `auto` (default; uses `MemoryMcp:Search:DefaultMode`), `hybrid`, `keyword`,
`dense`. In `dense` the output is byte-for-byte the old one. In the other modes each memory also shows
`Match: keyword | semantic | keyword + semantic`, keyword-only hits show `(keyword match)` instead of a
similarity score, and a footer states `mode: hybrid (keyword + semantic)` / `mode: keyword` /
`mode: semantic` with any `fallback: …`. `min_score` filters the semantic list only. The description
carries the recall-contract wording (historical data, not instructions; not proof of current truth; no
hits ≠ never happened).

## Configuration (`MemoryMcp:Search`)

| Key | Default | Meaning |
|---|---|---|
| `DefaultMode` | `Dense` | What `mode=auto` runs (`Dense`, `Keyword`, `Hybrid`). |
| `KeywordVariant` | `Plain` | `Stemmed` also matches Snowball stems (ru/da/en). Query time only: no re-index. |
| `Fusion` / `RrfK` | `Rrf` / `60` | `Rrf`, `WeightedRrf`, `Convex`. |
| `KeywordWeight` / `DenseWeight` / `ExactIntentKeywordBoost` | `1` / `1` / `2` | Weights (weighted RRF, convex) and the exact-intent keyword boost. |
| `Candidates` | `50` | Candidates per list before fusion. |
| `UseQueryInstruction` / `QueryInstruction` | `false` / retrieval instruction | Qwen3 query instruction (ablated; off = queries embedded as before). |
| `QueryTimeoutMs` | `3000` | Query-embedding budget in hybrid mode before the keyword fallback. |

Invalid values stop the agent at startup.

## Erasure

Deleting or editing a memory (tool, Bridge UI, consolidation, compaction) removes its FTS row in the same
transaction and then rewrites the index with FTS5 `optimize` under `PRAGMA secure_delete=ON`, so the
deleted text's tokens leave the database file (a contentless-delete table otherwise keeps them behind a
tombstone until the next merge). Integrity tests (`Category=Integrity`) check the file bytes after a delete
and after content and title edits. `memory.db` and the content files are not encrypted by
`Storage:EncryptAtRest` (see [security.md](security.md)).

The keyword index needs FTS5 `contentless_delete` (SQLite 3.43+). The agent's engine (SQLite3 Multiple
Ciphers) has it; on an older engine the index reports itself unavailable, `keyword`/`hybrid` fall back
to dense with a stated reason, and nothing else changes.

## Evaluation (owner tasks, local raw data only — Gate H for memory)

Nothing below runs in CI. Every search runs on a private copy of `/app/state/memory` that is deleted when
the command ends (also on Ctrl+C and SIGTERM; every `memory-eval` command first removes copies left by a
run that was killed outright); reports contain ids and numbers only (D6). Run inside the agent container:

```bash
dotnet Cortex.Contained.Agent.Host.dll memory-eval init                 # /app/state/eval/memory: templates, guide, default grid
dotnet Cortex.Contained.Agent.Host.dll memory-eval sample --count 300   # seeds.jsonl: stratified memories + abstention seeds (raw text!)
#   write dataset.jsonl from the seeds (query-generation.md): ≥ 250 queries, query family ≠ judge family
dotnet Cortex.Contained.Agent.Host.dll memory-eval pool --depth 10      # pool.jsonl: unjudged top-10 ids of every config, for the judge
#   grade the pools 0–2 (judgments); delete seeds.jsonl
dotnet Cortex.Contained.Agent.Host.dll memory-eval validate             # schema + ≥ 250 queries
dotnet Cortex.Contained.Agent.Host.dll memory-eval guard                # paraphrase guard
dotnet Cortex.Contained.Agent.Host.dll memory-eval kappa                # κ ≥ 0.6 from judge-agreement.csv (memoryId,llm,human)
dotnet Cortex.Contained.Agent.Host.dll memory-eval ablate --split dev   # grid vs the dense baseline; choose the config
#   set ablation.json "holdout": { "config": "...", "margin": 0.02 }, finish analysis-plan.md
dotnet Cortex.Contained.Agent.Host.dll memory-eval register             # freezes dataset, plan and grid (sha256)
dotnet Cortex.Contained.Agent.Host.dll memory-eval holdout              # one confirmatory run on the test split
```

`--source` selects another copy of the memory directory, `--embedder` another embeddings endpoint.
Evaluation runs disable the 3 s query-embedding budget, and a hybrid configuration that would fall back
(embedder down, keyword index unavailable) fails the run instead of being scored as another mode.
`seeds.jsonl` and `dataset.jsonl` hold memory or query text and stay in the workspace; the owner deletes
`seeds.jsonl` once the dataset is written.

## Rollout

1. Deploy. The first start backfills the keyword index (log: `Rebuilt the memory keyword index: N memories indexed`). `memory_search` behaves as before.
2. Run the evaluation above. If Gate H passes, set `MemoryMcp:Search:DefaultMode=Hybrid` (plus the chosen `MemoryMcp:Search` values) and restart.
