# PostgreSQL hybrid retrieval for a personal-assistant memory: pgvector + pg_textsearch, with Postgres-native alternatives (Nov 2026)

**Report date:** 2026-11-21. **Scope:** PostgreSQL 18.x/19.0, pgvector 0.8.7, pg_textsearch 1.5.1, pgvectorscale 0.9.1, pg_vectorsearch (alpha), VectorChord 1.1.1, ParadeDB pg_search 0.27.0.
**Conventions:** Facts carry citations. Code citations use `org/repo:path:lines`, with the pinned commit SHA given in the source table. Anything I derived or extrapolated is marked **(est.)**. Benchmark numbers are as reported by their authors, with caveats noted.

---

## 0. Summary

- **1M chunks: works well within the 1–4 GB budget.** Use three pieces in one Postgres:
  - a binary-quantized HNSW index for dense search (~0.43 GB), re-ranked against `halfvec(1024)` vectors stored in the table;
  - pg_textsearch BM25 indexes for keywords (one per language plus a `simple` one);
  - B-tree indexes for the metadata filters.
- **pgvector's filtered search is post-filter only.** The HNSW index is scanned first and the `WHERE` clause is applied afterwards.
  - Version 0.8's iterative scans stop the "too few rows" problem.
  - At 0.1% selectivity, one independent benchmark still measured only ~0.79 recall@10 via the HNSW path, and ~70 ms latency at 100K rows.
  - The planner's B-tree + exact-distance plan gave 1.0 recall in 0.8 ms.
  - For our very selective filters (one channel + one week), the pre-filter + exact-distance plan should be the main path.
- **pg_textsearch (Tiger Data, PostgreSQL license, GA since 2026-03-31) is real BM25.**
  - Design: an LSM-style memtable that spills into immutable segments, searched with Block-Max WAND.
  - IDF is computed per index across the memtable and all segments. It is local to each partition or partial index, so scores aren't comparable across them.
  - It stores no positions. Phrase queries (added in 1.5) are answered by rechecking candidates against the table rows.
  - It is young: several correctness and corruption fixes shipped in 2026.
- **10M chunks is feasible but no longer "in memory" on 1–4 GB.** Even the binary HNSW index is ~4.3 GB, and HNSW builds degrade badly when the graph doesn't fit `maintenance_work_mem`.
- **50M chunks is not a realistic HNSW deployment on this hardware.** Partition-based quantized indexes fit better:
  - VectorChord vchordrq (RaBitQ; AGPLv3 or ELv2);
  - Tiger's pg_vectorsearch PRISM, which is alpha and has no filtered search until GA.
- **Postgres's distinctive advantage is that the source-of-truth event log, the chunks, the vectors and the BM25 index commit atomically and share WAL, backup and PITR.**
- **Physical erasure is achievable but needs extra steps beyond `DELETE`:** VACUUM, then REPACK or REINDEX, a BM25 segment merge, and WAL/backup retention.

### Version snapshot

| Component | Version / status | Evidence |
|---|---|---|
| PostgreSQL 19.0 | Released 2026-11-19 | [PG19 release notes](https://www.postgresql.org/docs/19/release-19.html) ("Release date: 2026-11-19") |
| PostgreSQL 18 | GA 2025-09-25: AIO, skip scan, uuidv7, data checksums on by default | [PG18 release notes](https://www.postgresql.org/docs/18/release-18.html) |
| pgvector | 0.8.7 (2026-10-01). 0.9.0 is unreleased on master: async I/O for indexes, drops PG13/14 | `pgvector/pgvector:CHANGELOG.md:1-27` |
| pg_textsearch | v1.5.1 (2026-10-02). v1.5.0 (2026-10-01) added Boolean/phrase support, PG19 beta, Windows. Supports PG17/18/19 | [releases](https://github.com/timescale/pg_textsearch/releases); `timescale/pg_textsearch:README.md:16-18` |
| pgvectorscale | 0.9.1 (2026-09-04, security hardening). Entering maintenance mode | [releases](https://github.com/timescale/pgvectorscale/releases); [pg_vectorsearch blog, 2026-10-13](https://www.tigerdata.com/blog/announcing-pg_vectorsearch-scalable-open-source-vector-search-postgresql) |
| pg_vectorsearch (PRISM) | Alpha. Requires PG18+. No filtered WHERE + ORDER BY until GA | `timescale/pg_vectorsearch:README.md:11-14,213`; [InfoQ 2026-11](https://www.infoq.com/news/2026/11/postgresql-vector-search/) |
| VectorChord | 1.1.1 (2026-02-28). Repo moved to `supervc-stack`. AGPLv3 / ELv2 | [releases](https://github.com/supervc-stack/VectorChord/releases) |
| ParadeDB pg_search | 0.27.0 (2026-11-19), AGPL-3. Vector-in-index beta since 0.25 | [PGXN](https://pgxn.org/dist/pg_search/); [ParadeDB indexing-vectors docs](https://www.paradedb.com/docs/reference/indexing/indexing-vectors.md) |

---

## 1. Architecture of search

### 1.1 Storage layout

**Heap**
- 8 KB pages, a 24-byte page header, a 4-byte line pointer per tuple, and a ~23-byte tuple header (24 after alignment).
- MVCC versions live in the heap. Indexes point to heap tuples by TID.

**TOAST**
- The toaster triggers when a row is wider than `TOAST_TUPLE_THRESHOLD` (normally 2 kB). It compresses and/or moves fields out of line until the row is under `TOAST_TUPLE_TARGET` (~2 kB).
- Out-of-line values are split into chunks of about 2,000 bytes ("four chunk rows will fit on a page"). The main row keeps an 18-byte TOAST pointer.
- `EXTERNAL` storage means out-of-line without compression. Source: [PG19 docs §TOAST](https://www.postgresql.org/docs/19/storage-toast.html).
- pgvector changed `vector`'s default storage from `extended` to `external` in 0.6.0 (`pgvector/pgvector:CHANGELOG.md`, 0.6.0 entry).
- What this means for vector sizes:

  | Type at 1024 dims | Size | Behaviour |
  |---|---|---|
  | `vector(1024)` | 4,104 B (`4*d+8`) | Always TOASTed unless `STORAGE PLAIN` |
  | `halfvec(1024)` | 2,056 B (`2*d+8`) | Just over the threshold, so it is TOASTed under the default storage |
  | `bit(1024)` | 136 B (`d/8+8`) | Stays inline |

  Type sizes: `pgvector/pgvector:README.md:966,1004,1042`.
- pgvector recommends `STORAGE PLAIN` "for best performance" (`README.md:684-692`).
- pg_vectorsearch warns that `EXTERNAL` makes reranking "pay an extra fetch the planner does not cost" (`timescale/pg_vectorsearch:README.md`, native-types example).
- PG19 changes default TOAST compression from pglz to lz4. That affects the compressible text column, not vectors under `EXTERNAL`/`PLAIN` ([PG19 notes](https://www.postgresql.org/docs/19/release-19.html)).

**Index access methods**

All of these keep their data in ordinary 8 KB buffer-managed pages, so they share `shared_buffers` and the OS cache:
- `hnsw` and `ivfflat` (pgvector);
- `bm25` (pg_textsearch);
- core `btree`, `brin`, `gin`.

AM capabilities:
- HNSW has `amcanorderbyop = true`, `amcanparallel = false` and `amcanreturn = NULL` (`pgvector/pgvector:src/hnsw.c:477-504`).
- So there are no parallel HNSW scans and no index-only scans: every candidate needs a heap visit for MVCC visibility and for reading columns.

**WAL**
- HNSW inserts use `GenericXLog`. New pages are registered with `GENERIC_XLOG_FULL_IMAGE`, existing pages as deltas (`src/hnswinsert.c:126-137,243-244`).
- HNSW builds WAL-log the whole index at the end with `log_newpage_range` (`src/hnswbuild.c:1194-1195`). WAL volume for a build is therefore about the index size.
- pg_textsearch has no custom WAL resource manager:
  - memtable and publication changes use `GenericXLog`;
  - new segment pages use `log_newpage_buffer()`;
  - reclaimed pages emit a stock btree reuse-conflict record for standbys.
  - Source: `timescale/pg_textsearch:ARCHITECTURE.md:27-45`.
  - Releases before 1.2.0 had essentially broken physical replication ([v1.2.0 notes](https://github.com/timescale/pg_textsearch/releases)).

**Buffer cache**
- `shared_buffers` plus the OS page cache (double buffering). `effective_cache_size` only informs the planner.
- HNSW graph walks are pointer-chasing random page reads. Tiger Data's PRISM post explains why this punishes graph indexes once they exceed RAM ([2026-10-13](https://www.tigerdata.com/blog/announcing-pg_vectorsearch-scalable-open-source-vector-search-postgresql)).

**Recommended physical schema for this use case (design inference)**
- `events` — the append-only log and source of truth.
- `chunks` — id, event_id, kind, channel, ts, lang, tags, and text (TOASTed, lz4).
- `chunk_vec` — chunk_id, ts, channel, kind, and `halfvec(1024) STORAGE PLAIN`.
- Why denormalize the filter columns into `chunk_vec`:
  - `ORDER BY vec <=> $q LIMIT k` and the filter `WHERE` then hit the same relation, which the index-ordered plan needs.
  - The vector heap stays narrow (3 rows per page; see §6).
- Why keep the vector table separate: frequent metadata updates on `chunks` can then be HOT updates and don't touch `chunk_vec`.

### 1.2 End-to-end flow of a hybrid query

1. **Parse and rewrite.**
   - Each CTE is inlined unless marked `MATERIALIZED`.
   - pgvector recommends `MATERIALIZED` CTEs for relaxed-order iterative scans and for distance filters (`README.md:478-516`).
2. **Plan.**
   - For `ORDER BY embedding <=> $2 LIMIT k`, the planner considers:
     - an ordered index path through `hnswcostestimate`, which returns an infinite cost when there is no ORDER BY (`src/hnsw.c:144-170`);
     - a B-tree or bitmap path on the filter columns, followed by an exact distance sort.
   - pg_textsearch planner hooks detect `<@>` and pick the BM25 index implicitly. Inside PL/pgSQL, for prepared/dynamic SQL, or with partial indexes you must name the index with `to_bm25query(text, 'index')` (`timescale/pg_textsearch:README.md:247-263,313-327,564-573`).
3. **Execute.**
   - The ordered index scan (`amgettuple`) streams TIDs in distance or score order.
   - The executor fetches each heap tuple, checks visibility, applies the remaining quals, and the Limit node stops early.
   - The two ranked lists are then joined (hash FULL JOIN), fused, sorted, and limited.
   - CTEs run sequentially in one backend. Running the dense and lexical queries on two connections and fusing in the app makes total latency the max of the two instead of the sum (design inference).

---

## 2. Dense vector search internals

### 2.1 HNSW (pgvector)

**Parameters**

| Parameter | Default | Range |
|---|---|---|
| `m` | 16 | 2–100 |
| `ef_construction` | 64 | 4–1000 |
| `hnsw.ef_search` | 40 | 1–1000 |

Source: `pgvector/pgvector:src/hnsw.h:65-73`.

**Dimension limits for indexing:** `vector` ≤ 2,000, `halfvec` ≤ 4,000, `bit` ≤ 64,000, `sparsevec` ≤ 1,000 non-zeros (`README.md:250-255`; `src/hnsw.h:43-44`).

**On-disk layout**
- Element tuple: type, level, deleted, version, **10 heap TIDs**, the neighbor-tuple TID, then the vector datum (`src/hnsw.h:376-388`). The fixed prefix is 72 bytes.
- Neighbor tuple size: `MAXALIGN(4 + 6·(level+2)·m)` (`src/hnsw.h:89-93`). At level 0 with m=16 that is 200 B.
- The build places an element and its neighbor tuple on the same page when they fit together (`src/hnswbuild.c:188-207`).
- Identical vectors share one element: up to 10 heap TIDs are appended to it (`src/hnswinsert.c:650-720`).
- Slots freed by VACUUM are reused by later inserts (`src/hnswinsert.c:49-100`).

**Index size formula (est., derived from that source)**

```
per_element = MAXALIGN(72 + datum) + MAXALIGN(4 + 12·m) + 8      (level-0 elements; ~1/m of nodes are larger)
elements_per_page = floor(~8160 / per_element)
index_bytes ≈ N · 8192 / elements_per_page
```

| Indexed type (1024-d), m=16 | Datum | Per element | Per page | Bytes per vector | 1M | 10M | 50M |
|---|---|---|---|---|---|---|---|
| `vector` | 4,104 | 4,384 | 1 | 8,192 | 8.2 GB | 82 GB | 410 GB |
| `halfvec` | 2,056 | 2,336 | 3 | ~2,731 | 2.7 GB | 27 GB | 137 GB |
| `bit` (binary_quantize) | 136 | 416 | 19 | ~431 | 0.43 GB | 4.3 GB | 21.6 GB |
| `bit`, m=8 | 136 | 320 | 25 | ~328 | 0.33 GB | 3.3 GB | 16 GB |

**The formula reproduces published sizes:**
- Katz, dbpedia 1536-d, 1M vectors ([2024-04-09](https://jkatz05.com/post/postgres/pgvector-scalar-binary-quantization/)):

  | Indexed type | Reported | Formula |
  |---|---|---|
  | `vector` | 7,734 MB | 1 per page |
  | `halfvec` | 3,867 MB | 2 per page |
  | `bit` | 473 MB | ~480 MB |

- Tiger Data, `halfvec(768)` × 1M: reported 2.05 GB; formula gives 4 per page = 2,048 B per vector ([2026-10-13](https://www.tigerdata.com/blog/announcing-pg_vectorsearch-scalable-open-source-vector-search-postgresql)).
- **For `bit`, the neighbor list (192 B) is larger than the code itself (136 B), so `m` drives size.**

**Search**
- Greedy descent from the entry point, then a beam search of width `ef_search` on layer 0.
- With iterative scans enabled, candidates that were pruned are kept in a `discarded` pairing heap. The scan resumes from them in batches of `ef_search` (`src/hnswscan.c:60-91`).

**Build**
- Fast while the graph fits `maintenance_work_mem`. Otherwise a NOTICE is shown and the build "will take significantly more time" (`README.md:289-320`; `src/hnswbuild.c:529-539`).
- Parallel workers are controlled by `max_parallel_maintenance_workers`.
- **Pitfall:** when vectors are TOASTed, the heap looks small, so no parallel workers are launched unless you set the table's `parallel_workers` storage option.
  - Measured on 100K × 1536-d, 4 vCPU (single run): 3.4 min with 0 workers vs 56 s with 3 workers ([pgvector.app, 2026-10-27](https://pgvector.app/benchmarks/)).
- In Docker, set `--shm-size` ≥ `maintenance_work_mem` for parallel builds (`README.md:1189-1192`).

**Speed/recall reference** (100K × 1536-d, m=16, ef_construction=64, PG18.6 + pgvector 0.8.7, 4 vCPU, warm, one client; [pgvector.app](https://pgvector.app/benchmarks/)):

| ef_search | Recall@10 | p50 | p95 | QPS (1 client) |
|---|---|---|---|---|
| 40 | 0.947 | 2.9 ms | 5.3 ms | 304 |
| 100 | 0.983 | 5.3 ms | 8.4 ms | 181 |
| 200 | 0.990 | 9.1 ms | 14 ms | 106 |

- At 500K rows: ef 40 → 0.931 recall at 4.1 ms; ef 100 → 0.968 at 6.2 ms.

### 2.2 IVFFlat

- Parameters: `lists` (default 100, max 32,768) and `ivfflat.probes` (default 1) (`src/ivfflat.h:61-64`).
- Starting points: `lists = rows/1000` up to 1M rows, `sqrt(rows)` beyond; `probes = sqrt(lists)` (`README.md:334-376`).
- Training happens at build time (k-means). Inserts don't re-center the lists, so plan to REINDEX after drift (design inference).
- Builds are much faster than HNSW: 100K × 1536-d in 9.8 s for 100 lists vs 3.4 min for HNSW ([pgvector.app](https://pgvector.app/benchmarks/)).
- The recall/latency trade-off is worse: 316 lists needs probes=32 to reach 0.953 at 19 ms (same source).
- Supports `bit` (Hamming), so a binary IVF is an option (`README.md:366-372`).

### 2.3 Types, quantization and re-ranking

**Types**
- `vector` — fp32, 4d+8 B.
- `halfvec` — fp16, 2d+8 B.
- `bit` — d/8+8 B.
- `sparsevec` — 8·nnz+16 B; up to 16,000 non-zeros stored, 1,000 indexable (`README.md:957-1075`).

**Recall impact on the 1536-d OpenAI dataset**
- `halfvec` indexing loses essentially no recall. At ef 40: 0.949 (halfvec) vs 0.947 (vector) at 100K ([pgvector.app](https://pgvector.app/benchmarks/)). Katz reports the same ([2024](https://jkatz05.com/post/postgres/pgvector-scalar-binary-quantization/)).
- Binary quantization + re-rank:
  - Index: `hnsw ((binary_quantize(embedding)::bit(1024)) bit_hamming_ops)`.
  - Query: over-fetch N by Hamming distance, then `ORDER BY embedding <=> $q LIMIT k` (`README.md:589-609`).

  | Re-rank candidates (1536-d) | Recall@10 | p50 |
  |---|---|---|
  | 20 | 0.854 | 1.8 ms |
  | 80 | 0.966 | 3.5 ms |
  | 160 | 0.985 | 5.7 ms |

  Source: [pgvector.app](https://pgvector.app/benchmarks/).
- **Binary quantization is strongly dataset dependent.** Katz: dbpedia 1536-d reaches 0.99 with re-ranking at ef=200, but gist-960 gives **0% recall** and sift-128 is unusable.
- **Not measured for qwen3-embedding-0.6b at 1024-d.** Binary recall must be measured on our data before relying on it.
- Tiger Data reports that pgvector binary quantization couldn't reach good recall at competitive speed in their tests (blog footnote [1], 2026-10-13).

### 2.4 Alternatives within Postgres

| Option | Mechanism | Status for us |
|---|---|---|
| **pgvectorscale** StreamingDiskANN + SBQ | Disk-oriented graph with statistical binary quantization. Label filtering only on `smallint[]` via `&&` (`timescale/pgvectorscale:README.md`, Filtered Vector Search) | 0.9.1 (2026-09-04). Tiger says it is entering maintenance mode and that "portions of the graph could become unreachable, causing recall to degrade" on some datasets ([2026-10-13](https://www.tigerdata.com/blog/announcing-pg_vectorsearch-scalable-open-source-vector-search-postgresql)). **Not recommended for new builds.** |
| **pg_vectorsearch** PRISM | IVF with a hierarchical centroid tree, ~1-bit/dim RaBitQ codes in SIMD-friendly posting lists, SOAR-style boundary spilling, error-bounded rerank from the heap. Single knob `prism.nprobe` (0 = auto, ~95% recall) (`timescale/pg_vectorsearch:README.md:36-63`) | **Alpha.** PG18+, dims ≤ 1968. **No WHERE + ORDER BY filtered search until GA.** Churn needs manual `prism_rebalance`/REINDEX. Deletes don't reuse space. Prerelease upgrades may require drop/recreate. Numbers below. |
| **VectorChord** vchordrq | IVF + RaBitQ with "autonomous reranking", hierarchical k-means; prefilter, prewarm, prefetch and rabitq8/4 types; pgvector type compatible | 1.1.1, AGPLv3/ELv2. Claims 100M × 768-d on a 4 vCPU / 32 GB i4i.xlarge and 100M indexed in 20 min (vendor claims). I could not fetch the prefilter docs. |
| **ParadeDB** pg_search vectors | Vectors inside the Tantivy-based index, k-means clustered; only pgvector `vector` (no `halfvec`), one vector field per index | Beta (0.25+). AGPL-3. |

**PRISM vs HNSW, Tiger Data's numbers** (rekall benchmark tool, Cohere embeddings, recall@10, HNSW on `halfvec`):

| Scale and hardware | PRISM | HNSW |
|---|---|---|
| 1M, c8g.large (4 GiB) | 280 MB index, ~3.1 min build, 3 ms @95% / 9 ms @99% | 2.05 GB index, ~21.7 h build, 122 ms @95% / 392 ms @99% |
| 1M, c8g.medium (2 GiB) | 15 ms @99% | not attempted |
| 100M, r8i.4xlarge (128 GB) | 25 GB index, 1 h 40 min build, 6 ms @95% / 21 ms @99% | could not build |
| 100M, r8i.16xlarge (512 GB) | 49 min build, 4 ms @95% / 19 ms @99% | 180 GB index, 42 h build, 8 ms @95%, 99% not reached |

These are vendor-run numbers on a single dataset.

---

## 3. Filtered search internals

### 3.1 Why filtered HNSW loses results

- Filters are applied after the index scan. With `ef_search = 40` and a filter matching 10% of rows, about 4 rows survive (`README.md:424-466`).
- Measured on 100K rows, LIMIT 10, iterative scan off ([pgvector.app](https://pgvector.app/benchmarks/)):

  | Filter selectivity | Avg rows returned | Recall@10 |
  |---|---|---|
  | 10% | 3.99 | 0.398 |
  | 1% | 0.39 | 0.039 |
  | 0.1% | 0.03 | 0.003 |

### 3.2 Iterative index scans (pgvector 0.8+)

- `hnsw.iterative_scan` = `off` | `strict_order` | `relaxed_order` (`src/hnsw.c:102-120`; `README.md:478-516`).
- Mechanism:
  - After the first `ef_search` batch is exhausted, the scan resumes from the `discarded` heap in further batches of `ef_search` (`src/hnswscan.c:64-91`).
  - It stops when `so->tuples >= hnsw.max_scan_tuples` (default 20,000) or when scan memory exceeds `work_mem × hnsw.scan_mem_multiplier` (default 1) (`src/hnswscan.c:140-163,258-272`).
  - `strict_order` additionally drops candidates that would break distance monotonicity (`src/hnswscan.c:309-325`).
- With `relaxed_order`, wrap the query in a `MATERIALIZED` CTE and re-sort with `ORDER BY distance + 0`; the `+ 0` is needed on PG17+ (`README.md:497-506`).
- IVFFlat equivalent: `ivfflat.iterative_scan` plus `ivfflat.max_probes`.

**Measured behavior** (100K rows, ef 40, LIMIT 10; [pgvector.app](https://pgvector.app/benchmarks/)):

| Selectivity | HNSW relaxed_order | HNSW strict_order | Planner's choice (B-tree + exact) |
|---|---|---|---|
| 10% | 0.946 @ 6.3 ms | 0.899 @ 6.2 ms | 1.000 @ 58 ms |
| 1% | 0.967 @ 37 ms | 0.901 @ 36 ms | 1.000 @ 6.3 ms |
| 0.1% | 0.791 @ 69 ms | 0.773 @ 70 ms | **1.000 @ 0.82 ms** |

(Recall@10 @ p50.)

- **Takeaway:** for our 0.1–1% filters, B-tree/BRIN pre-filter + exact distance wins on both recall and latency at 1M scale.
- Expected visited tuples for an uncorrelated filter (est.): about `k / selectivity`. For k=10 at 0.1% that is ≈ 10,000 tuples, which is near `max_scan_tuples` (20,000), with random page reads per visited element.

### 3.3 Planner choices and cost pitfalls

- `hnswcostestimate` models graph-traversal tuples:
  - formula: `entryLevel·m + layer0TuplesMax·layer0Selectivity`, with a hard-coded scaling factor 0.55;
  - it doesn't model the extra index traversal that iterative scans need under a selective filter;
  - it adjusts for TOAST not being counted in seq-scan cost.
  - Source: `src/hnsw.c:144-240`, specifically lines 215 and 233.
- 0.8.0 "improved cost estimation for better index selection when filtering" (`CHANGELOG.md` 0.8.0).

Pitfalls (design inference unless cited):
- **Correlated filters.** Channel and time are correlated with insertion order, and LIMIT costing assumes uniformly distributed matches.
- **Generic plans for prepared statements** can lock in the HNSW plan for selective parameters. Use `plan_cache_mode = force_custom_plan` for these queries.
- **Partial indexes** only match literal predicates, so prepared statements need custom plans.
- **Multi-column selectivity:** use `CREATE STATISTICS` (mcv/dependencies) on (channel, kind, tags).
- **PG19 `pg_plan_advice`** can pin plans ([PG19 notes](https://www.postgresql.org/docs/19/release-19.html)).
- **PG18 B-tree skip scan** lets a `(channel, ts)` index serve time-only predicates ([PG18 notes](https://www.postgresql.org/docs/18/release-18.html)).

### 3.4 Partial indexes and partitioning

**Partial HNSW per value** (for a few distinct channels): `CREATE INDEX ... WHERE (channel = 'x')` (`README.md:456-460`).

**Partitioning**
- LIST by channel or RANGE by month (`README.md:462-476`).
- Each partition gets its own HNSW and BM25 index.
- BM25 statistics are partition-local (`timescale/pg_textsearch:README.md:548-552`).

Trade-off for unfiltered queries (est.):
- A query with no time predicate must search every partition and merge (Merge Append of ordered index scans).
- 60 monthly partitions × ~1–3 ms per warm HNSW probe ≈ 60–180 ms (est.).
- Yearly partitions, or a global index plus time partitions for the exact-filter path, are the compromise.

### 3.5 Behavior by filter width (est. for 1024-d `halfvec PLAIN`, warm)

- **Very selective (0.1–1%).**
  - Candidate rows = 0.001–0.01·N. The exact-distance cost is roughly the candidate count × (heap fetch + 1024-d distance).
  - pgvector.app measured ~5.8 µs per row with TOASTed 1536-d fp32 vectors (10K rows in 58 ms). Narrower inline `halfvec` should be cheaper: ~2–4 µs per row (est.).
  - Resulting estimates:

    | Corpus | Candidate rows | Estimated latency |
    |---|---|---|
    | 1M | 1K–10K | ~2–40 ms |
    | 10M | 10K–100K | ~30–400 ms |
    | 50M | 50K–500K | ~0.15–2 s |

  - Parallel bitmap heap scan (`max_parallel_workers_per_gather`, `README.md:724-730`) can divide this.
  - Physical clustering by (channel, ts) keeps the pages contiguous: partition by channel, or run `REPACK ... USING INDEX` periodically.
- **Broad (≥10%).** HNSW with `relaxed_order` is best (0.946 @ 6.3 ms at 10% in the table above).

---

## 4. Keyword / BM25 internals

### 4.1 pg_textsearch index structure

**Write path**
- An LSM-like layout. The L0 memtable is a WAL-logged chain of pages inside the index — the durable source of truth.
- A disposable shared-memory cache sits in front of it, bounded by `pg_textsearch.memory_limit`, **default 2 GB**. This must be lowered for a 1–4 GB budget.
- The memtable spills into immutable segments that compact by level: `segments_per_level` = 8, `max_segment_size` = 4,095 MB.
- Sources: `timescale/pg_textsearch:README.md:434-470`; `ARCHITECTURE.md:1-45,142-195`.

**Segment format**
- A term dictionary (sorted offsets into a string pool).
- Posting blocks of 128 documents: delta + bitpacked doc IDs, packed term frequencies, quantized doc lengths.
- A skip index holding per-block max TF and min fieldnorm, used for Block-Max WAND bounds.
- 1-byte SmallFloat fieldnorms: exact for lengths 0–39, ~5–11% error above.
- A doc-ID → CTID map stored as split arrays.
- Compression is 41% smaller than raw, with ~6% decode overhead.
- Source: [Tiger blog, 2026-03-31](https://www.tigerdata.com/blog/pg-textsearch-bm25-full-text-search-postgres).

**Query path**
- WAND pivoting across terms plus Block-Max skipping within lists.
- CTIDs are resolved only for the final top-k.
- `LIMIT n` is pushed down as k. Filtered queries use "selectivity seeding": k is inflated from the planner's selectivity estimate (v1.4.0 notes; `README.md:358-372`).

**Deletes**
- VACUUM clears per-segment alive bits, costing O(dead documents).
- Dead documents are physically removed only when a segment is merged or rewritten. A segment with ≥ 50% dead documents is rewritten alone.
- Sources: v1.1.0 notes; `README.md:385-433`.

**Statistics (IDF and length normalization)**
- Per index, global across the memtable and all segments: `unified_doc_freq = memtable_doc_freq + segment_doc_freq`, with `total_docs = Σ segment.num_docs` (`src/types/query.c:1019-1057`; `src/index/metapage.h`).
- IDF = `ln(1 + (N − df + 0.5)/(df + 0.5))` (repo `test/sql/validation.sql`).
- These are **physical, not MVCC-filtered** statistics (`ARCHITECTURE.md:66-70`).
- After VACUUM, per-segment `total_docs`/`doc_freq` may stay stale until the next merge (repo `CLAUDE.md`). ARCHITECTURE mentions "corpus-statistic adjustment" in VACUUM (`ARCHITECTURE.md:525`), so exactly what gets adjusted is uncertain.
- Partition-local and partial-index-local statistics make scores **non-comparable across languages or partitions**.

### 4.2 Text-search configurations and multilingual handling

**Tokenization** uses core PostgreSQL text search configurations (`text_config` is required).

**Snowball stemmers in core:**
- PG18: 29 languages — arabic, armenian, basque, catalan, **danish**, dutch, **english**, estonian, finnish, french, german, greek, hindi, hungarian, indonesian, irish, italian, lithuanian, nepali, norwegian, portuguese, romanian, **russian**, serbian, spanish, swedish, tamil, turkish, yiddish. Plus `porter` and `simple`.
- PG19: adds polish and esperanto, and `dutch_porter`.
- Source: `postgres/postgres:src/backend/snowball/Makefile` on REL_18_STABLE and REL_19_STABLE (fetched 2026-11-21).
- Stopword files exist for only 15 languages (danish, english and russian included).

**PG19 upgrade caveat**
- The Dutch algorithm changed. The English, French, German, Lithuanian, Norwegian, Spanish, Swedish and Turkish stemmers were updated.
- FTS now classifies characters with the database's locale provider.
- Therefore **REINDEX BM25/GIN text indexes after pg_upgrade to 19** ([PG19 notes](https://www.postgresql.org/docs/19/release-19.html)).

**Other dictionaries**
- Hunspell/ispell dictionaries via the `ispell` template, `unaccent` (contrib), and the built-in `simple` configuration.
- There is no ICU word-break parser in core.
- CJK needs zhparser; the pg_textsearch README gives an example (`README.md:289-311`).

**Mixed languages**
- The documented pattern is one partial BM25 index per language, `WHERE lang = 'xx'`, queried with an explicitly named index (`README.md:265-287`).
- Design for us (inference):
  - detect language at ingest and store `lang`;
  - build partial indexes for en/ru/da (and others);
  - add one `simple`-config index over all rows as a language-agnostic fallback (also covers code and names);
  - at query time, run the query against `simple` plus the likely language indexes and fuse them with RRF, because their IDFs are independent.

**Limits**
- Lexemes longer than 2,047 bytes are ignored.
- Inputs over 256 KB are split (`README.md:554-562`).
- Core tsvector limits: < 1 MB per tsvector, ≤ 256 positions per lexeme, positions ≤ 16,383 ([PG19 docs](https://www.postgresql.org/docs/19/textsearch-limitations.html)).

### 4.3 Phrases, Boolean queries, filters

- **No positions are stored.** The index is 19–26% smaller than ParadeDB's partly for that reason ([benchmarks comparison](https://timescale.github.io/pg_textsearch/benchmarks/comparison.html)).
- Since 1.5.0, `@@ tsquery` supports `&`, `|`, `!`, prefix `:*`, weights, and the phrase operator `<->`. **Phrase and weight conditions are rechecked against the table row.**
- Combining Boolean filters with BM25 ranking is "not yet optimized as a single index scan" (`README.md:121-151`).

**Filter pitfall (verified in source):** the pre-filter path ("standalone scoring") re-tokenizes every candidate row's full text with `tp_tokenize_text` (`src/types/query.c:920-948`).
- For ~700-word chunks, est. 30–150 µs per row. So 10K filtered rows ≈ 0.3–1.5 s (est.).
- The alternative is the BM25 index scan with post-filter and selectivity seeding.
- For a fixed set of channels, per-channel partial BM25 indexes avoid this entirely.

### 4.4 Comparisons

**Built-in `tsvector` + GIN + `ts_rank`/`ts_rank_cd`**
- No IDF, no term-frequency saturation, and no top-k path: ranking scores every matching row ([Tiger blog](https://www.tigerdata.com/blog/pg-textsearch-bm25-full-text-search-postgres)).
- Fine for Boolean matching, `ts_headline` snippets, and as a recall-oriented candidate generator.

**ParadeDB pg_search** (Tantivy, AGPL-3, 0.27.0)
- Stores positions, so it supports phrase and proximity queries.
- Fuzzy matching, highlighting, synonyms, facets.
- Tokenizers: ICU, unicode_words, Lindera, Jieba.
- Snowball stemmers for 20 languages, including danish and russian ([stemmer docs](https://www.paradedb.com/docs/reference/token-filters/stemming.md)).

**Benchmark: pg_textsearch vs ParadeDB 0.21.6 on MS MARCO** (Tiger-run; warm cache; [comparison page](https://timescale.github.io/pg_textsearch/benchmarks/comparison.html), [blog](https://www.tigerdata.com/blog/pg-textsearch-bm25-full-text-search-postgres)):

| Metric | pg_textsearch | ParadeDB |
|---|---|---|
| **8.8M passages** (GitHub runner, 7 GB RAM) | | |
| p50 by query length (1 → 8+ tokens) | 0.70 → 26 ms | 18.8 → 44.9 ms |
| p95 by query length (1 → 8+ tokens) | 1.6 → 57 ms | — |
| Index size | 1,215 MB | 1,503 MB |
| Build time | 234 s | 140 s |
| **138M passages** (16 threads, 123 GB RAM, 31 GB shared_buffers) | | |
| Weighted p50 | 40.6 ms | 94.4 ms |
| Concurrent throughput (16 clients) | 198.7 TPS | 22.8 TPS |
| Index size | 17 GB | 23 GB |
| Build time | 17 m 37 s | 8 m 55 s |

- Tiger has not benchmarked index > RAM configurations.

### 4.5 `sparsevec` for BGE-M3 / SPLADE

- HNSW over `sparsevec` with `sparsevec_ip_ops`, ≤ 1,000 non-zeros indexable (`README.md:611-631`; `src/hnsw.h:44`).
- This is a graph over sparse vectors, not an inverted index; pgvector 0.9.0 improves sparsevec inserts.
- It needs a second embedding model. For our multilingual keyword need, pg_textsearch plus Snowball is cheaper (design inference).
- For long chunks, BGE-M3 non-zero counts could approach the 1,000 cap (est.).

---

## 5. Hybrid execution (SQL patterns)

**Reference pattern** (pgvector-python RRF example uses a `FULL OUTER JOIN` of two ranked CTEs with k=60; [rrf.py](https://raw.githubusercontent.com/pgvector/pgvector-python/master/examples/hybrid_search/rrf.py)), adapted:

```sql
-- $1 query text, $2 query halfvec(1024), $3 channel, $4 ts_from, $5 ts_to
BEGIN;
SET LOCAL hnsw.ef_search = 100;
SET LOCAL hnsw.iterative_scan = relaxed_order;     -- only matters if HNSW plan chosen
SET LOCAL plan_cache_mode = force_custom_plan;
WITH dense AS MATERIALIZED (
  SELECT chunk_id, embedding <=> $2 AS dist
  FROM chunk_vec
  WHERE channel = $3 AND ts >= $4 AND ts < $5      -- B-tree(channel, ts) → exact sort when selective
  ORDER BY dist LIMIT 50
), d AS (SELECT chunk_id, row_number() OVER (ORDER BY dist + 0) AS r FROM dense),
lex AS MATERIALIZED (
  SELECT id AS chunk_id, content <@> to_bm25query($1, 'chunks_bm25_simple') AS s
  FROM chunks
  WHERE channel = $3 AND ts >= $4 AND ts < $5
  ORDER BY s LIMIT 50                              -- negative BM25, ascending
), l AS (SELECT chunk_id, row_number() OVER (ORDER BY s) AS r FROM lex)
SELECT chunk_id,
       COALESCE(1.0/(60 + d.r), 0) + COALESCE(1.0/(60 + l.r), 0) AS rrf
FROM d FULL JOIN l USING (chunk_id)
ORDER BY rrf DESC
LIMIT 20;
COMMIT;
```

Notes:
- **Binary-quantized dense leg:** add an inner over-fetch on `binary_quantize(embedding)::bit(1024) <~> binary_quantize($2)` with LIMIT 100–200, then re-rank by `<=>` (`README.md:589-609`).
- **Several languages:** add `lex_en`/`lex_ru`/`lex_da` CTEs, each with `WHERE lang = 'xx'` and its named partial index, and add their RRF terms.
- **Weighted fusion:** min-max normalize each list. Dense: `1 − dist`. BM25: `−s`. Then combine as `α·dense + (1−α)·bm25`. RRF is more robust because BM25 scales differ across partial indexes.
- **Time ordering / recency:** either sort the fused top-N by `ts DESC`, or multiply RRF by a decay such as `exp(−age/τ)`. A pure "latest in channel" query should not use ANN at all; use the `(channel, ts)` B-tree.
- **Pagination:** ANN with relaxed ordering and OFFSET is not stable across calls. Fetch the top 100–200 once, cache in the app, and page there (design inference).
- **LIMIT behavior:**
  - HNSW without iterative scans returns at most `ef_search` candidates, fewer after filtering and dead tuples (`README.md:939-943`).
  - pg_textsearch post-filtered scans grow their internal batch until the LIMIT is filled (`README.md:190-193`).
- **Re-ranking outside the DB:** a cross-encoder over the fused top-20 to top-50 is cleaner done in the application ([cross_encoder.py](https://github.com/pgvector/pgvector-python/blob/master/examples/hybrid_search/cross_encoder.py) is referenced at `README.md:633-642`).

---

## 6. Performance at scale (1M / 10M / 50M × 1024-d)

### 6.1 Footprint formulas (est.)

**Assumptions:** ~700 words per chunk on average; text compressed with lz4 ≈ 2.5–4 KB per chunk (Cyrillic is 2 B per character in UTF-8).

| Component | Formula / estimate | 1M | 10M | 50M |
|---|---|---|---|---|
| chunks heap + TOAST text (lz4) | ~0.2 KB row + 2.5–4 KB text (est.) | 2.7–4.2 GB | 27–42 GB | 135–210 GB |
| `chunk_vec` `halfvec(1024)` PLAIN | tuple ≈ 24+22+2,056 → 3/page → ~2.73 KB per row | 2.7 GB | 27 GB | 137 GB |
| (alt.) `vector(1024)` default EXTERNAL | 4,104 B in 3 TOAST chunks + pointer ≈ 4.4 KB (est.) | 4.4 GB | 44 GB | 220 GB |
| HNSW on `bit(1024)`, m=16 | §2.1 table | 0.43 GB | 4.3 GB | 21.6 GB |
| HNSW on `halfvec(1024)`, m=16 | §2.1 table | 2.7 GB | 27 GB | 137 GB |
| PRISM (alpha), 1024-d | 280 MB per 1M at 768-d (Tiger) × 1024/768 ≈ 0.37 GB per 1M (est.) | ~0.37 GB | ~3.7 GB | ~19 GB |
| BM25 per configuration | 0.8–1.5 KB per chunk (est.)¹ | 0.8–1.5 GB | 8–15 GB | 40–75 GB |
| B-trees (PK, (channel, ts), event FK) | ~30–40 B per row per index | ~0.1 GB | ~1 GB | ~5 GB |

¹ Scaled from 138 B per passage on MS MARCO v1 and 123 B per passage on v2. Our chunks are ~12× longer, with sublinear growth in unique terms.

**Hot working set** (bit HNSW + 2 BM25 configurations + upper B-tree levels; est.):

| Corpus | Hot set | Fits a 1–4 GB budget? |
|---|---|---|
| 1M | ~0.6–2 GB | Yes |
| 10M | ~5–15 GB | No |
| 50M | ~25–75 GB | No |

Total disk with bit HNSW + 2 BM25 indexes: ~8–11 GB at 1M, ~80–110 GB at 10M, ~400–550 GB at 50M. Initial-load WAL is of the same order unless `wal_level = minimal` is used during the load.

### 6.2 Latency and QPS (est. unless cited; single client)

**1M — dense**
- Warm halfvec-HNSW analog: 1536-d at 100K–500K gives 2.9–6.2 ms p50 at 0.93–0.98 recall ([pgvector.app](https://pgvector.app/benchmarks/)).
- **But with ≤ 4 GB RAM**, Tiger measured 1M `halfvec` HNSW at **122 ms p50 for 95% recall and 392 ms for 99%** (cited above).
- So the plan is bit-HNSW (0.43 GB, resident) + rerank of 80–160 `halfvec` rows: est. p50 5–15 ms, p95 20–40 ms cold-ish.

**1M — filtered (0.1–1%)**
- B-tree + exact: 2–40 ms (§3.5).

**1M — BM25**
- 8.8M short passages, warm: p50 0.7–26 ms ([comparison](https://timescale.github.io/pg_textsearch/benchmarks/comparison.html)).
- Our 1M longer chunks: est. 2–20 ms p50.
- Filtered via standalone scoring: up to 0.3–1.5 s at 10K rows. Prefer the seeded index scan or partial indexes.

**1M — hybrid**
- Sum of the legs: ~10–60 ms p50 (est.), or the max of the two if fused in the app.
- QPS is irrelevant for one user. Warm HNSW gives ~300 QPS per client at ef 40 ([pgvector.app](https://pgvector.app/benchmarks/)).

**10M**
- Dense: bit-HNSW (4.3 GB) exceeds RAM.
  - With ~1–3K page visits per query and partial caching, expect ~0.5–1.5K NVMe random reads at ~0.08–0.1 ms each.
  - Est. p50 40–150 ms, p95 150–500 ms.
  - pgvector 0.9 + PG19 read streams may improve this (unmeasured).
- Filtered exact: 30–400 ms warm, more when cold.
- BM25 (8–15 GB index): unbenchmarked when cold; est. 20–200 ms.

**50M**
- HNSW is impractical. Even bit-HNSW is 21.6 GB, and building it under 1–2 GB `maintenance_work_mem` would be extremely slow (Tiger: halfvec 1M on 4 GiB took 21.7 h).
- PRISM/VectorChord-style IVF + RaBitQ is the only in-Postgres path.
  - PRISM at 100M: 6–21 ms, but with 128 GB RAM.
  - On 4 GB, est. 20–100+ ms p50 (unmeasured).

**pgvectorscale context:** on 50M × 768-d Cohere vectors on a 16 vCPU / 128 GB box, Tiger reported 28× lower p95 and 16× higher QPS than Pinecone s1 at 99% recall ([2024-06-11](https://www.tigerdata.com/blog/pgvector-is-now-as-fast-as-pinecone-at-75-less-cost)). That is a 2024 vendor benchmark on a much larger machine.

### 6.3 Build time and ingest

**HNSW build**
- 1M × 1536-d, `ef_construction` = 200: 1 h 27 m single-threaded (0.5.1) vs 9.5 min parallel (0.6.0) ([Supabase, 2024-01-30](https://supabase.com/blog/pgvector-fast-builds)).
- 100K × 1536-d bit index: 39 s single-process ([pgvector.app](https://pgvector.app/benchmarks/)).
- Estimates for us: bit-HNSW at 1M ≈ 3–10 min with 4–8 workers if the graph fits in ~0.5–1 GB. halfvec-HNSW at 1M on 4 GB ≈ hours.

**BM25 build**
- 8.8M passages in 234 s with 4 workers; 138M in 17.6 min with 15 workers.
- Ours (est.): 1M ≈ 3–10 min, 10M ≈ 0.5–1.5 h, 50M ≈ 3–8 h.

**Continuous inserts**
- HNSW, 1536-d: ~8 ms per row single-threaded (100K row-by-row in 13.8 min; [pgvector.app](https://pgvector.app/benchmarks/)). bit or halfvec at 1024-d is likely ~1–5 ms (est.).
- BM25 on short passages: 2,450 TPS with 1 client, 11,593 TPS with 8 clients ([v1.1.0 notes](https://github.com/timescale/pg_textsearch/releases)).
- Both are far above a personal assistant's ingest rate.

**Bulk re-embed**
- Build a shadow `chunk_vec_v2`, bulk COPY, `CREATE INDEX` with `parallel_workers` set, then swap in one transaction.
- Avoid 10M UPDATEs: they cause bloat and WAL ≈ 2× the data (design inference).

### 6.4 Updates and deletes

**HNSW VACUUM runs three passes** (`src/hnswvacuum.c:920-940`):
1. `RemoveHeapTids` — scans every index page (lines 42-136).
2. `RepairGraph` — re-links the neighbors of deleted elements, at roughly insert cost each (lines 401-512).
3. `MarkDeleted` — sets `deleted = 1` and **zeroes the vector bytes** (lines 731-869, specifically 815-816).

- Space is reused by later inserts (`src/hnswinsert.c:49-100`). The index file never shrinks without REINDEX.
- pgvector's advice: "Vacuuming can take a while… speed it up by reindexing first" (`README.md:748-755`).
- **Run ≥ 0.8.4:** 0.8.3 fixed possible index corruption with HNSW vacuuming and 0.8.4 fixed "hnsw graph not repaired" (`CHANGELOG.md:13-25`).

**BM25 deletes**
- Alive-bit flips, then segment rewrite at ≥ 50% dead, or REINDEX.
- Update-heavy workloads fragment pages; REINDEX if cold-cache latency degrades (`README.md:378-383`).

**HOT updates:** a non-HOT update of a chunk row adds entries to every index. Keeping vectors in a separate table and metadata updates HOT-eligible avoids HNSW churn (design inference).

**Cold behavior**
- Index larger than `shared_buffers`/RAM: graph walks turn into random NVMe reads. That explains Tiger's 1M-on-4 GiB numbers and the >1 s per query when a 100M HNSW index was moved to a 128 GB box.
- Use `pg_prewarm`/autoprewarm to reload hot indexes after restarts.

---

## 7. Scaling beyond one node

- **Partitioning** (declarative): per-partition HNSW and BM25 indexes, partition pruning on time and channel, partition-local BM25 IDF (`README.md:548-552`).
- **Read replicas:** physical streaming replication works for pgvector (WAL-based; `README.md:829-831`) and pg_textsearch (≥ 1.2.0).
  - pg_textsearch standbys read the memtable chain directly.
  - They need `hot_standby_feedback = on` (`README.md:424-433`).
- **Sharding:**
  - The pgvector README points to Citus and PgDog (`README.md:757-766`). Citus 14 supports PG18 ([Citus 14 notes](https://www.citusdata.com/updates/v14-0/)).
  - pg_textsearch on Citus is **unverified**.
- **Limits:**
  - 32 TB per non-partitioned table (`README.md:825-827`).
  - Index dimension caps from §2.1; `ef_search` ≤ 1000; `m` ≤ 100; IVFFlat `lists` ≤ 32,768.
  - pg_vectorsearch: ≤ 1968 dims, with exceptions.
  - None of these limits bind for a single user. RAM per index is the binding constraint.

---

## 8. Consistency, durability and erasure

**Transactional coupling (the main advantage)**
- Inserting the event, chunk, vector and BM25 entry in one transaction makes all of them visible at commit, with read-your-writes on the primary.
- One WAL stream, one backup, and one PITR timeline keep the log and every index consistent.
- Embeddings are computed outside the database. Use an outbox table (`FOR UPDATE SKIP LOCKED`) so the vector insert is its own transaction, with "event committed, vector pending" as an explicit state (design inference).
- Caveat: pg_textsearch spill and compaction publications are physical and "not undone by transaction rollback". They are logically invisible but physically present (`ARCHITECTURE.md:378`).

**What remains after `DELETE` + `VACUUM`**
- **Heap/TOAST:** dead tuples are pruned and space goes to the free-space map. Core does not guarantee zeroing the reclaimed bytes (inference).
- **HNSW:** vector bytes are zeroed in the element (verified above). Neighbor-list remnants are cleared.
- **BM25:** postings, term-dictionary strings and fieldnorms stay in segments until a merge or rewrite. Displaced pages wait for a transaction horizon before reuse (`ARCHITECTURE.md:438-520`).

**Erasure procedure (est.)**
1. `DELETE` from events, chunks and chunk_vec in one transaction.
2. `VACUUM` the affected tables.
3. `REPACK` the tables (PG19; `CONCURRENTLY` avoids blocking reads and writes) or `VACUUM FULL`. This rewrites the heap and all indexes into new files and unlinks the old ones; it needs free disk ≈ table + indexes ([REPACK docs](https://www.postgresql.org/docs/19/sql-repack.html)). Alternatively `REINDEX INDEX CONCURRENTLY` for indexes only.
4. `SELECT bm25_force_merge(...)` / `bm25_compact(...)`, or REINDEX the BM25 index.
5. `CHECKPOINT`.
6. WAL: insert records and full-page images keep the data until the segments are recycled and overwritten. Recycling renames files by default (`wal_recycle = on`). Archived WAL and base backups (pgBackRest) keep data for their retention window.
7. Replicas: they replay the same operations.
8. SSD/filesystem: unlinked blocks persist until TRIM or overwrite.

- Server logs (`log_min_duration_statement` with bound parameters) and temporary files can also hold content.
- **Erasure SLA ≈ max(WAL retention, backup retention)** unless backups are crypto-shredded. Full-disk encryption is advisable (design inference).

---

## 9. Operations

**Tuning for a 4 GB budget (est.)**

| Setting | Suggested value | Note |
|---|---|---|
| `shared_buffers` | 1 GB | ~25% of RAM (`README.md:668-682`) |
| `effective_cache_size` | 2.5–3 GB | |
| `work_mem` | 16–32 MB | Also caps the iterative scan: `× hnsw.scan_mem_multiplier` |
| `maintenance_work_mem` | 0.5–1 GB, builds only | |
| `max_parallel_maintenance_workers` | 4–8 | Plus the table's `parallel_workers` |
| `max_parallel_workers_per_gather` | 2–4 | |
| `random_page_cost` | 1.1 | NVMe |
| `pg_textsearch.memory_limit` | 128–256 MB | **Default is 2 GB** |
| `jit` | off | |
| `io_method` | default `worker`, or `io_uring` on Linux | PG18 AIO |
| `hnsw.ef_search` | 40–100 | Per query |
| `hnsw.iterative_scan` | `relaxed_order` | |
| `hnsw.max_scan_tuples` | 20,000–50,000 | |

**pg_textsearch specifics**
- Requires `shared_preload_libraries`, which means a restart (`README.md:53-60`).
- Inline compaction stalls the triggering writer. Use `compaction = 'manual'` plus scheduled `bm25_compact()`, or background mode via pg_durable (beta) (`README.md:385-433,499-546`).

**Monitoring**
- `pg_stat_statements`, `pg_stat_user_indexes`, `pg_statio_*`.
- `pg_buffercache` to check index residency.
- `pgstattuple` for bloat.
- `pg_stat_progress_create_index` / `_vacuum`, and `_repack` (PG19).
- `bm25_memory_usage()`, `bm25_level_counts()`, `bm25_pending_free_pages()`.
- Recall checks: compare against `SET LOCAL enable_indexscan = off` exact results (`README.md:768-779`).

**Upgrades**
- pg_upgrade needs the extension binaries for both major versions.
  - pgvector: PG13–19 (0.9 drops 13/14).
  - pg_textsearch: PG17–19.
  - pg_vectorsearch: PG18+.
- pg_textsearch on-disk header formats change: v1.5 writes header v9, which pre-1.5 binaries cannot read. Some upgrades need REINDEX ([UPGRADING.md](https://raw.githubusercontent.com/timescale/pg_textsearch/main/UPGRADING.md)).
- PG19 stemmer and locale changes need a REINDEX of text indexes (§4.2).

**Backup**
- `pg_dump` is logical: indexes are rebuilt on restore, so HNSW and BM25 builds are on the restore path.
- pgBackRest or pg_basebackup are physical: PITR and incremental backups, and indexes are restored as files.

**Docker (Windows host)**
- `--shm-size` ≥ `maintenance_work_mem` (`README.md:1189-1192`).
- cgroup memory limits count shared buffers plus backends.
- Set WSL2 VM limits in `.wslconfig`.
- Keep data on a Linux-filesystem volume, not `/mnt/c` ([Docker WSL best practices](https://docs.docker.com/desktop/features/wsl/best-practices/)).
- Allow a long stop grace period for clean shutdown spills.

**Common failure modes and lessons**
- TOASTed vectors hide heap size, so no parallel workers are launched.
- An HNSW graph that doesn't fit memory leads to very slow builds.
- Planner picks HNSW for a selective filter: too few rows or high latency.
- Generic plans and partial indexes don't match.
- PL/pgSQL needs explicit BM25 index names.
- Long transactions block VACUUM and BM25 page reclaim.
- A young extension shipped corruption fixes:
  - pgvector 0.8.2 (buffer overflow, parallel build), 0.8.3 (vacuum corruption);
  - pg_textsearch 1.3.1 (duplicate rows), 1.5.0 (silent truncation), 1.5.1 (spill/merge corruption).
  - Pin versions and test upgrades.

---

## 10. Strengths, weaknesses, verdict

**Strengths**
- One transactional store for the log, chunks, vectors and BM25: atomic, PITR-consistent, SQL joins and filters.
- Mature B-tree/BRIN pre-filtering with exact recall.
- Real BM25 with Block-Max WAND.
- 29–31 Snowball languages, including EN/RU/DA.
- Permissive licenses: pgvector, pg_textsearch and pg_vectorsearch under the PostgreSQL license.

**Weaknesses**
- HNSW is RAM-hungry and slow to build out of memory. 1024-d fp32 is one vector per page.
- Filtered ANN is post-filter only.
- BM25 IDF is local to each partial index or partition.
- No positions in pg_textsearch, so phrase queries rely on recheck.
- The filtered BM25 pre-filter path re-tokenizes every candidate row.
- pg_textsearch is young.
- Erasure needs REPACK/merge plus WAL and backup handling.
- No index-only or parallel HNSW scans.

**Verdict by scale band**

| Band | Verdict | Configuration |
|---|---|---|
| **1M** | **Recommended.** Hot set ~1–2 GB fits; est. 10–60 ms hybrid p50. | PG18/19 + pgvector 0.8.7: `halfvec(1024) PLAIN`, bit-HNSW (m=16) + rerank, or halfvec-HNSW if ~3 GB of index RAM is available. pg_textsearch 1.5.1: `simple` + en/ru/da partial indexes. B-tree on (channel, ts), with exact search for selective filters. Validate binary recall on qwen3 embeddings first. |
| **10M** | **Feasible with compromises.** Est. p95 hundreds of ms when cold. | Time/channel partitioning, exact search for selective filters, IVF-family index for unfiltered queries (VectorChord if AGPL/ELv2 is acceptable; PRISM once GA with filters), or 8–16 GB of RAM. |
| **50M** | **Not recommended on 1–4 GB with pgvector HNSW.** | Only an IVF + RaBitQ index on more RAM. Storage would be ~0.4–0.55 TB (est.), likely beyond a realistic personal corpus. |

---

## Sources

| Title | Org | Date | URL |
|---|---|---|---|
| pgvector README / CHANGELOG / source (master `1d56580`; tag v0.8.7 `f37c13f`) | pgvector | fetched 2026-11-21 | https://github.com/pgvector/pgvector |
| pg_textsearch README / ARCHITECTURE / UPGRADING / source (main `5419bfb`; v1.5.1 `b63fe58`) | Tiger Data | fetched 2026-11-21 | https://github.com/timescale/pg_textsearch |
| pg_textsearch releases v1.1–v1.5.1 | Tiger Data | 2026-04-22 → 2026-10-02 | https://github.com/timescale/pg_textsearch/releases |
| pg_textsearch 1.0: How We Built a BM25 Search Engine on Postgres Pages | Tiger Data | 2026-03-31 | https://www.tigerdata.com/blog/pg-textsearch-bm25-full-text-search-postgres |
| pg_textsearch vs ParadeDB benchmark | Tiger Data | 2026-03-10 | https://timescale.github.io/pg_textsearch/benchmarks/comparison.html |
| Announcing pg_vectorsearch (PRISM) | Tiger Data | 2026-10-13 | https://www.tigerdata.com/blog/announcing-pg_vectorsearch-scalable-open-source-vector-search-postgresql |
| pg_vectorsearch README (main `7d70243`) | Tiger Data | fetched 2026-11-21 | https://github.com/timescale/pg_vectorsearch |
| InfoQ: pg_vectorsearch | InfoQ | 2026-11 | https://www.infoq.com/news/2026/11/postgresql-vector-search/ |
| pgvectorscale README / releases | Tiger Data | 0.9.1, 2026-09-04 | https://github.com/timescale/pgvectorscale |
| pgvector now as fast as Pinecone (pgvectorscale) | Timescale | 2024-06-11 | https://www.tigerdata.com/blog/pgvector-is-now-as-fast-as-pinecone-at-75-less-cost |
| PostgreSQL 19 release notes | PGDG | 2026-11-19 | https://www.postgresql.org/docs/19/release-19.html |
| PostgreSQL 18 release notes | PGDG | 2025-09-25 | https://www.postgresql.org/docs/18/release-18.html |
| REPACK docs (PG19) | PGDG | 2026-11 | https://www.postgresql.org/docs/19/sql-repack.html |
| TOAST docs (PG19) | PGDG | 2026-11 | https://www.postgresql.org/docs/19/storage-toast.html |
| Text search limitations (PG19) | PGDG | 2026-11 | https://www.postgresql.org/docs/19/textsearch-limitations.html |
| WAL configuration (PG19) | PGDG | 2026-11 | https://www.postgresql.org/docs/19/wal-configuration.html |
| Snowball Makefile (REL_18 / REL_19) | PGDG | fetched 2026-11-21 | https://github.com/postgres/postgres/tree/REL_19_STABLE/src/backend/snowball |
| Scalar and Binary Quantization for pgvector | J. Katz | 2024-04-09 | https://jkatz05.com/post/postgres/pgvector-scalar-binary-quantization/ |
| pgvector benchmarks (PG18.6 / 0.8.7) | pgvector.app (third-party, unknown owner) | 2026-10-27 | https://pgvector.app/benchmarks/ |
| pgvector 0.6.0 parallel builds | Supabase | 2024-01-30 | https://supabase.com/blog/pgvector-fast-builds |
| pgvector 0.8.0 on Aurora | AWS | 2025-05-28 | https://aws.amazon.com/blogs/database/supercharging-vector-search-performance-and-relevance-with-pgvector-0-8-0-on-amazon-aurora-postgresql/ |
| pgvector-python RRF example | pgvector | fetched 2026-11-21 | https://github.com/pgvector/pgvector-python/blob/master/examples/hybrid_search/rrf.py |
| VectorChord README / releases | TensorChord (supervc-stack) | 1.1.1, 2026-02-28 | https://github.com/supervc-stack/VectorChord |
| ParadeDB pg_search (PGXN), tokenizers, stemmer, vectors | ParadeDB | 0.27.0, 2026-11-19 | https://pgxn.org/dist/pg_search/ ; https://www.paradedb.com/docs/llms.txt |
| Citus 14 release notes | Citus Data | 2026 | https://www.citusdata.com/updates/v14-0/ |
| Docker WSL2 best practices | Docker | fetched 2026-11-21 | https://docs.docker.com/desktop/features/wsl/best-practices/ |

---

## Uncertainty and gaps

1. **All latencies for our exact configuration are estimates.** That covers 1024-d qwen3 vectors, `halfvec PLAIN`, ~700-word multilingual chunks, 1–4 GB RAM and NVMe. No benchmark matches it. The closest cold data point is Tiger's 1M `halfvec` HNSW on 4 GiB (vendor-run).
2. **Binary-quantization recall for qwen3-embedding-0.6b is unknown.** Published results range from 0.99 down to 0% across datasets. Must be measured.
3. **BM25 index size per long chunk is an estimate** scaled from MS MARCO. Cold-cache pg_textsearch latency is explicitly unbenchmarked by Tiger.
4. **pg_textsearch statistics after VACUUM are ambiguous:** the repo's CLAUDE.md says stale until merge; ARCHITECTURE.md says VACUUM does a "corpus-statistic adjustment". Exact semantics need verification.
5. **Heap free-space zeroing** after VACUUM was not verified in core source; it is treated as not guaranteed. The HNSW vector zeroing is verified (`src/hnswvacuum.c:815-816`).
6. **Code citations come from current branches:** pgvector master `1d56580`, which contains unreleased 0.9.0 changes. The structs and vacuum logic are likely identical in v0.8.7 `f37c13f` but weren't line-diffed.
7. **Unverified items:**
   - whether `REPACK CONCURRENTLY ... USING INDEX` is supported;
   - pg_textsearch on Citus;
   - VectorChord prefilter internals (docs fetch failed);
   - Qwen3 MRL dimension-truncation quality;
   - whether timescaledb-ha or other Docker images bundle pg_textsearch.
8. **VectorDBBench and ann-benchmarks:** I found no pgvector rows for 1024-d or filtered workloads on comparable hardware. The leaderboard is vendor-run (Zilliz).
9. **pgvector.app** is an unknown third party (single 4-vCPU VM, single runs). Its methodology is published and plausible; treat it as directional.
10. **Tooling:** the `github/get_me` identity tool wasn't available in this environment. Research used web fetches, GitHub code search and raw file reads instead.
