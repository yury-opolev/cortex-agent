# Memory search: scale path (decision record)

Date: 2026-11-21
Status: Accepted. SQLite with exact (brute-force) search **now**. Qdrant or PostgreSQL are recorded as **future** options.
Evidence: six research reports in [`2026-11-21-memory-search/`](2026-11-21-memory-search/):
- [01 ANN & quantization](2026-11-21-memory-search/01-ann-algorithms-and-quantization.md)
- [02 SQLite vs Postgres options](2026-11-21-memory-search/02-sqlite-postgres-options.md)
- [03 standalone engines](2026-11-21-memory-search/03-standalone-engines.md)
- [04 unified memory services](2026-11-21-memory-search/04-unified-memory-services.md)
- [05 Postgres deep dive](2026-11-21-memory-search/05-postgres-deep-dive.md)
- [06 Qdrant deep dive](2026-11-21-memory-search/06-qdrant-deep-dive.md)

Related design: [`../specs/2026-11-21-episodic-history-memory-design.md`](../specs/2026-11-21-episodic-history-memory-design.md).

## Decision

1. **Now, and up to about 1M chunks:** keep everything in **SQLite**.
   - FTS5 BM25 for keywords.
   - **Exact vector search.** Filter first, so time, channel and kind restrict the candidate set *before* scoring, then brute force over that set. Use sqlite-vec ≥ 0.1.9 metadata/partition columns, or an in-process SIMD scan.
   - Fuse the two result lists with RRF.
   - Exact search keeps **100% recall under any filter** and avoids the known failure modes of filtered ANN.
2. **Between 100k and ~1M chunks:** stay exact, and add these when latency requires it:
   - an int8 vector copy in RAM with a full-precision rescore;
   - monthly segments;
   - optional Matryoshka truncation, only if measured recall on qwen3-embedding allows it.
3. **Beyond ~1M chunks, or under concurrent load:** add **Qdrant** *or* **PostgreSQL (pgvector + pg_textsearch)** as a rebuildable projection of the event log. Decide by a bake-off on a copy of real data (§5).
4. **Not pursued:**
   - Memory platforms (Mem0, Graphiti/Zep, Letta, Cognee): documented erasure bugs, retired or unmaintained servers, Python/TS + graph-DB footprint.
   - AGPL/ELv2 Postgres extensions (ParadeDB, VectorChord).
   - Heavy engines (Milvus, Weaviate, Elasticsearch/OpenSearch, Vespa).
   - A custom Rust retrieval service, unless a measured requirement fails with both candidates.

The architecture allows this deferral: the event log is the source of truth and every search index is a derived, rebuildable projection. Switching engines means adding a projector and rebuilding, not migrating data.

## Measured exact-search cost

Measured on the host CPU (i9-13900H, .NET 10 `TensorPrimitives` / AVX2 / popcount, 1024-dim, top-10). Windows host, not Docker; expect 1.5–3× slower inside a busy Docker VM.

| Vectors | float32, 1 thread | float32, all threads | int8, all threads | binary, all threads |
|---|---|---|---|---|
| 10k | 1.5 ms | 0.5 ms | 0.13 ms | 0.19 ms |
| 100k | 18 ms | 7.5 ms | 2.2 ms | 0.9 ms |
| 1M | 166 ms | 67 ms | 17 ms | 4.4 ms |

Vector memory at 1024 dims:

| Encoding | Per vector | 1M vectors |
|---|---|---|
| float32 | 4 KB | 4.1 GB |
| int8 | 1 KB | 1.0 GB |
| binary | 128 B | 128 MB |

A selective filter (one channel, one week ≈ 0.1–1%) makes exact search *faster*, and recall stays exact.

Why not a graph ANN index (HNSW) now:
- Its recall collapses under restrictive filters. Qdrant's own benchmark shows plain HNSW at 0.1% recall with a 1% filter. pgvector's post-filtered HNSW gives 0.79 recall@10 at 0.1% selectivity, against 1.00 for B-tree pre-filter + exact search (100k rows).
- It only clearly pays off beyond roughly 1–5M vectors.

## Current-code defects found (to fix)

1. **Post-filtering.** `MemoryMcp.Core` `SqliteVecMemoryStore.SearchAsync` fetches `limit×10` nearest neighbours, then filters by tags. With a selective filter, recall collapses (0.106 recall@10 at 1% selectivity in a synthetic test). Filters must move into the scan.
2. **Deleted vectors are not erased.** The pinned sqlite-vec `0.1.7-alpha.2.1` predates the DELETE fix (v0.1.7, 2026-03-17), so a delete only flips a validity bit. `PRAGMA secure_delete` and FTS5 `secure-delete` are also off.
   - Fix: upgrade sqlite-vec to ≥ 0.1.9, enable both, then VACUUM and truncate the WAL after purges.

## Future option A: Qdrant 1.20 (projection of the event log)

**Search mechanics**
- **Dense:** HNSW with rich quantisation (int8, binary 1–2 bit, PQ, TurboQuant) plus oversampling and rescoring, and per-component memory tiers (pinned/cached/cold).
- **Filtered search:**
  - A per-segment planner estimates filter cardinality.
  - It then picks exact scoring of payload-index candidates, filter-aware HNSW with per-value extra links (`payload_m`), or ACORN for the mid-selectivity band.
  - Raise `full_scan_threshold` so very selective filters take the exact path.
- **BM25:** native on a payload full-text index since v1.20 (released October 2026).
  - MaxScore top-k; `k1`/`b` set per query.
  - Statistics are global per shard and include deleted documents until segments rebuild.
  - Filters do not shorten posting traversal.
- **Multilingual:**
  - The `multilingual` tokenizer (charabia) handles CJK/Thai/Arabic segmentation and language detection.
  - Snowball stemmers for 19 languages, **one per field**; stopwords for 30 languages.
  - Mixed EN/RU/DA text needs per-language fields plus an unstemmed field, fused.
- **Hybrid:**
  - One Query API request with nested prefetches.
  - RRF (**default k=2**, configurable), weighted RRF, DBSF, and formula queries with decay functions (e.g. a recency boost).
  - No self-hosted cross-encoder.

**Scale on a 1–4 GB RAM budget (estimates)**

| Chunks | Setup | Hybrid latency |
|---|---|---|
| 1M | ~1.3–2.2 GB resident (int8/4-bit pinned, originals cold) | 5–25 ms |
| 10M | 1–2-bit pinned, cold originals and text index | p95 100–250 ms (<50 ms needs 6–10 GB) |
| 50M | not feasible globally (binary 6.4 GB + HNSW 7.7 GB) | workable only with time-scoped shard keys |

**Consistency and erasure**
- It is a separate system: a projection with lag. It needs the purge-generation fencing from the history-memory design.
- Deletes are soft until segment vacuum (>20% deleted by default). Force it by temporarily lowering thresholds.
- Snapshots and WAL segments retain data; time-based shard keys allow dropping whole periods.
- WAL fsync is periodic (~5 s), inferred from code.
- No encryption at rest in open source.

**Operations**
- A single container; quotas; snapshots.
- Upgrades must step through every minor version.
- Configuration changes rebuild all segments.
- Use Docker volumes, not WSL bind mounts.
- BM25 is new (recent fixes in 1.20.0/1.20.1).

## Future option B: PostgreSQL 18/19 + pgvector 0.8.x + pg_textsearch 1.5.x (log and index in one DB)

**Search mechanics**
- **Dense:** HNSW or IVFFlat on `halfvec`/`bit`. `binary_quantize` → bit-HNSW, with a manual rerank against `halfvec`.
- **Filtered search:**
  - HNSW is post-filter only; 0.8 iterative scans mitigate this.
  - Selective filters must use B-tree/BRIN pre-filter + exact distance.
  - The planner needs care: custom plans, extended statistics, partitioning.
- **BM25 (pg_textsearch):**
  - An LSM memtable plus immutable segments, with Block-Max WAND.
  - Statistics are global per index, partial index or partition.
  - No positions: phrases are rechecked against the row.
  - The pre-filter path re-tokenises candidate rows (slow). Prefer the seeded index scan or per-channel partial indexes.
  - Lower `pg_textsearch.memory_limit` from its 2 GB default.
- **Multilingual:**
  - 29 Snowball configurations (31 in PG19), hunspell, unaccent.
  - One partial BM25 index per language plus a `simple` index, fused.
  - No built-in CJK segmentation.
- **Hybrid:** SQL CTEs (BM25 + vector + RRF), with full SQL filters and joins to the event log.

**Scale on a 1–4 GB RAM budget (estimates)**

| Chunks | Setup | Hybrid latency |
|---|---|---|
| 1M | hot set ~0.6–2 GB (bit-HNSW 0.43 GB + rerank) | 10–60 ms |
| 10M | bit-HNSW 4.3 GB exceeds RAM | dense p95 150–500 ms; selective exact 30–400 ms |
| 50M | not with pgvector HNSW; IVF + RaBitQ only (VectorChord AGPL/ELv2, or Tiger PRISM in alpha) on more RAM | — |

Measured warning: full `halfvec` HNSW at 1M on ≤4 GB RAM gave 122 ms p50 at 95% recall, because the graph doesn't fit in RAM.

**Consistency and erasure**
- The **event log, chunks, vectors and BM25 index commit in one transaction**, with one WAL, one backup and PITR.
- HNSW vacuum zeroes vector bytes.
- Full erasure needs VACUUM → REPACK (PG19) or VACUUM FULL → BM25 force-merge → CHECKPOINT, plus WAL and backup retention.
- No TDE in core.

**Operations**
- A custom image (`shared_preload_libraries`) and memory tuning.
- Extension compatibility per major version.
- After upgrading to PG19, REINDEX the text indexes (stemmer changes).
- pg_textsearch is young (corruption fixes shipped in 2026).

## How the two options compare

| | Qdrant | Postgres |
|---|---|---|
| Filtered dense search | ★★★ planner + ACORN | ★★ exact pre-filter path needs planner care |
| RAM efficiency | ★★★ quantisation + tiers | ★★ HNSW RAM-hungry; bit-HNSW + rerank |
| Hybrid ergonomics | ★★★ one request, many fusions | ★★ SQL, fully flexible |
| Multilingual keyword | stemmers 19, strong non-Latin tokenisation | stemmers 29–31, weak CJK |
| Consistency with event log | projection (fencing needed) | same transaction |
| Erasure | lazy (vacuum, snapshots, WAL) | explicit (REPACK, merge, WAL/backup) |
| Durability | WAL fsync periodic | full ACID |
| Maturity risk | BM25 new | pg_textsearch new |

**Rule of thumb:**
- Choose **Postgres** if the source of truth moves to a server database and transactional simplicity and erasure matter most.
- Choose **Qdrant** if search latency and RAM efficiency at 10M+ chunks matter most.

## Trigger and bake-off protocol

**Re-evaluate when any of these holds:**
- total chunks exceed **~500k**;
- p95 vector-search latency in production exceeds **100 ms**;
- concurrent-load requirements appear.

**Bake-off:**
- Restore a copy of real data.
- Use the frozen evaluation queries from the history-memory design.
- Apply the same filters and a 4 GB memory cap to both engines.
- Report: recall@10 vs exact float32, p50/p95 latency (warm and cold), resident memory, index build time, delete-and-erasure behaviour (byte-level check), and operational steps.

**Data handling (privacy review, PS-16):**
- The bake-off copy of real data stays local and encrypted, and is destroyed at the end of the run, including engine snapshots and WAL.
- No real-data bake-off runs on an engine that cannot pass the same byte-level erasure test.
