# Memory storage and search for Cortex: SQLite vs PostgreSQL, as of 21 Nov 2026

## 1. Summary

**Recommendation:** stay on SQLite for now, improve it, and keep the storage layer behind an interface so it can be replaced later. When the corpus nears about 300k–500k chunks, or once Russian/Danish keyword search becomes critical, move to **PostgreSQL 18 + pgvector 0.8.x + pg_textsearch** in a sidecar container. Every part of that Postgres setup is under the permissive PostgreSQL license, and it is the most mature hybrid-search option that fits a 1–2 GB RAM budget.

What I found:

1. **sqlite-vec is maintained again and now has ANN indexes, but they are alpha.**
   - It was revived in March 2026 (v0.1.7–v0.1.9). The current stable release, v0.1.9, has only brute-force search plus metadata, partition-key and auxiliary columns.
   - The ANN indexes (`rescore`, `ivf`, `diskann`) exist only in pre-releases, v0.1.10-alpha.1 to alpha.4 (Mar–May 2026). IVF is experimental and behind a compile flag.
   - On the author's 1M × 1024-dim benchmark (MacBook M4), brute force takes 590 ms per query. The `rescore` bit index takes about 92–101 ms, IVF 37–61 ms, and DiskANN 4.6–34 ms. DiskANN costs about 42–55 minutes of insert time and a database 2.6–3.1× larger.
2. **libSQL / Turso and DuckDB do not fit well.**
   - libSQL, the C fork, has a DiskANN vector index, but Turso says new features now go into Turso (the Rust rewrite).
   - Turso 0.7 added a .NET/EF Core provider, and 0.5 added experimental Tantivy full-text search. However, the vector reference I found documents only brute-force distance functions, with no ANN index.
   - DuckDB 2.0 (LTS, 21 Oct 2026) still labels HNSW persistence as experimental, with possible data loss or index corruption after a crash. The HNSW index must also fit in RAM.
3. **In Postgres, the BM25 options moved a lot in 2026.**
   - **pg_textsearch v1.5.1** (Timescale/Tiger Data, PostgreSQL license, 2 Oct 2026) gives BM25 with Block-Max WAND on PG 17/18/19. It reuses Postgres text-search configurations, so the built-in `russian` and `danish` stemmers work, and its README shows one partial index per language.
   - **ParadeDB v0.27.0** (19 Nov 2026) is AGPL-3.0. It now also has beta vector and hybrid search inside one index.
   - **VectorChord** (1.1.1, Feb 2026) and **VectorChord-bm25** (0.3.0, Dec 2025) are dual AGPLv3 / ELv2, and their GitHub repos now redirect to a new org, `supervc-stack`.
   - **pgvectorscale** (0.9.1, 4 Sep 2026) is going into maintenance mode in favour of **pg_vectorsearch**, which is alpha.
   - **pgvecto.rs** is replaced by VectorChord.
4. **Postgres versions:** PostgreSQL 19.0 came out on 19 Nov 2026 and adds `REPACK [CONCURRENTLY]`. The latest PG18 minor release is 18.7. pgvector 0.8.7 (1 Oct 2026) does not yet mention PG19 support (0.9.0 is unreleased), so **PG18 is the safe target**.
5. **Encryption at rest:** community Postgres has no built-in TDE. The options are disk/filesystem encryption, pgcrypto for individual columns, or Percona's `pg_tde` (which needs Percona's distribution). On SQLite, the free route is **SQLite3 Multiple Ciphers** (MIT, NuGet `SQLite3MC.PCLRaw.bundle`). That matters because SQLitePCLRaw 3.0 deprecated the free `bundle_e_sqlcipher`.

---

## 2. Embedded / SQLite-family options

### 2.1 sqlite-vec

| Item | Finding | Source |
|---|---|---|
| Latest stable | v0.1.9 (2026-03-31). Fixes `DELETE` on `vec0` tables with long (>12 char) text metadata. | sqlite-vec releases |
| Revival | v0.1.7 (17 Mar 2026), "sqlite-vec is back", sponsored by Mozilla. Adds proper `DELETE` support: space is reclaimed only once about a whole chunk (~1024 vectors) is deleted. Adds `<`/`>` constraints on distance for pagination. | v0.1.7 notes |
| ANN (pre-release) | v0.1.10-alpha.1 (31 Mar 2026) adds `rescore`, `ivf` (experimental, not enabled by default) and DiskANN. alpha.4 (18 May 2026) fixes DiskANN statement cleanup and `ALTER TABLE RENAME`. | releases; PRs #276/#277/#278 |
| Syntax | `float[1024] distance_metric=cosine indexed by rescore(quantizer=bit, oversample=8)` / `indexed by ivf(nlist=…)` / `indexed by diskann(neighbor_quantizer=binary, n_neighbors=48, …)` | PRs #276–278 |
| Erasure note | alpha.2: "Fix data leaking via un-deleted compressed neighbor vectors in DiskANN. This causes DELETEs to be quite expensive." | releases |
| Metadata filtering | Metadata columns (TEXT/INTEGER/FLOAT/BOOLEAN, up to 16) can be filtered inside the KNN query with `= != > >= < <=`. Partition-key columns shard the index ("should be +100's of vectors per unique partition key value"). Auxiliary `+col` columns cannot appear in KNN `WHERE`. | vec0 docs |
| Status | README still says it is pre-v1: "expect breaking changes". | README |
| License | Dual MIT / Apache-2.0 (LICENSE-MIT and LICENSE-APACHE in the repo; I re-fetched LICENSE-APACHE). | repo |

**Author benchmark: 1M NYT headlines, mxbai-embed-large 1024-dim, MacBook Pro M4**

| Config | Insert | Size | Query | Recall@10 |
|---|---|---|---|---|
| Flat (brute force) | 27.4 s | 3.85 GB | 590 ms | 1.0 |
| rescore int8, os=2 | 27.3 s | 5.28 GB | 225 ms | 1.0 |
| rescore bit, os=8 | 28.2 s | 4.55 GB | 101 ms | 0.988 |
| IVF nlist=1024, nprobe=32 (k-means done in Faiss) | 18 m | 3.99 GB | 37 ms | 0.988 |
| DiskANN R=48, L=64 | 55 m 21 s | 11.94 GB | 33.6 ms | 0.980 |
| DiskANN R=40, L=48 | 42 m 17 s | 10.03 GB | 4.6 ms | 0.954 |

The author notes that rescore "is technically still a brute-force search" and that its query time grows roughly linearly. Recall depends heavily on how well the embedding model tolerates quantization, and he has seen it drop to 70–80% (PR #276).

Inference: scaling linearly, brute force at 200k would be about 120 ms on an M4. Expect it to be slower on an x86 Docker VM.

### 2.2 SQLite FTS5 (BM25)

- **Tokenizers:** `unicode61` (default), `ascii`, `porter` (English-only stemming) and `trigram` for substring matching. There is **no built-in Russian or Danish stemmer** (sqlite.org/fts5.html).
- **Third-party stemming:** `abiliojr/fts5-snowball` is a loadable FTS5 tokenizer that uses the Snowball stemmers, for example `tokenize='snowball russian english'`. It has 44 stars, so it is lightly maintained.
- **Erasure:** with the FTS5 `secure-delete` option (SQLite ≥ 3.42.0), "full-text entries are actually removed from the database when existing FTS5 table rows are updated or deleted". The default is 0. Without it, the old index entries "can be used to trivially reconstruct the contents of deleted FTS5 table rows".
- **Whole-file erasure:** combine `PRAGMA secure_delete` (overwrites freed content with zeros; see sqlite.org/pragma.html#pragma_secure_delete), `VACUUM`, and a WAL checkpoint with TRUNCATE.
  - Inference: the WAL can hold old pages until it is checkpointed and truncated.

### 2.3 Encryption for SQLite in .NET

- **SQLitePCLRaw 3.0:** "I will no longer maintain and distribute encryption-enabled SQLite builds without cost… The `.bundle_e_sqlcipher` package is deprecated" (ericsink/SQLitePCL.raw v3.md). Microsoft's encryption doc still shows `bundle_e_sqlcipher` (page updated 2025-07-31).
- **SQLite3 Multiple Ciphers:**
  - NuGet `SQLite3MC.PCLRaw.bundle` 2.3.6, based on SQLite 3.53.3, MIT-licensed.
  - Supports sqleet (ChaCha20-Poly1305, the default) and SQLCipher v1–v4 formats. Encrypts the database and its journal files.
  - Use it with `Microsoft.Data.Sqlite.Core`.
- **Loading extensions:** `SqliteConnection.LoadExtension()` (Microsoft.Data.Sqlite ≥ 3.0). SQLite uses the platform loader directly, so `PATH`/`LD_LIBRARY_PATH` may need adjusting (MS Learn "Extensions").
- **Unverified:** whether sqlite-vec loads cleanly into a SQLite3MC or SQLCipher build. It should work if the build enables `sqlite3_load_extension`. The alternative is to compile the sqlite-vec amalgamation statically into the SQLite3MC build. Test this.

### 2.4 libSQL / Turso

| Item | Finding |
|---|---|
| libSQL (C fork) vector search | Native `F32_BLOB`, `F16`, `FB16`, `F8`, `F1BIT` types. DiskANN index via `CREATE INDEX … (libsql_vector_idx(col, 'metric=cosine','compress_neighbors=float8', …))`, queried with `vector_top_k(idx, q, k)`. Partial vector indexes (`WHERE year >= 2000`) are supported. Other filters are applied with a JOIN after the top-k. (docs.turso.tech/features/ai-and-embeddings) |
| libSQL status | "libSQL is actively maintained, but new features are being developed in Turso." MIT. (libsql README) |
| Turso (Rust rewrite) | Latest 0.8.4 (2026-11-20); 0.9.0-pre.6 is out. 0.7 (2026-07-13) "drop[s] the beta warning", adds an experimental .NET provider (`Turso.Data.Sqlite`, `Turso.Raw`) and EF Core (`Turso.EntityFrameworkCore.Sqlite`). 0.5 (2026-03-04) adds experimental Tantivy full-text search (`fts_match`/`fts_score`) stored inside the database file. |
| Turso vectors | The vector-search reference documents `vector32/64/8/1bit`, sparse vectors and distance functions, with examples using `ORDER BY distance LIMIT`, so brute force. **No ANN index is documented.** |
| Turso encryption | Native page-level AEGIS-256 / AES-GCM encryption, enabled with an `--experimental-encryption` flag. (docs.turso.tech/tursodb/encryption) |

### 2.5 DuckDB 2.0 (LTS, 21 Oct 2026)

- **VSS (HNSW):**
  - The extension is "experimental". Persistence needs `hnsw_enable_experimental_persistence`, because "WAL recovery is not yet properly implemented for custom indexes… data loss or corruption of the index".
  - The index "is not buffer managed and must be able to fit into RAM", and it does not count toward `memory_limit`.
  - Deletes are only marked; `PRAGMA hnsw_compact_index` cleans them up.
  - Only FLOAT32 vectors are supported.
- **FTS:**
  - Uses `match_bm25` with Snowball stemmers, including `russian` and `danish`.
  - Has an `incremental` option that maintains the index with triggers.
  - **The default `ignore` regex `(\\.|[^a-z])+` removes non-Latin characters, so Cyrillic text needs a custom setting.**
- **Verdict:** not suitable as the primary transactional memory store.

---

## 3. PostgreSQL options

### 3.1 Versions

- **PostgreSQL 19.0** was released 2026-11-19 (supported to 2031). It adds `REPACK [CONCURRENTLY]` to rewrite tables and reclaim space without blocking reads or writes, plus parallel autovacuum of indexes.
- **PostgreSQL 18:** current minor 18.7 (supported to 2030). (postgresql.org/support/versioning)
- **Extensions on PG19:** pg_textsearch supports PG 17/18/19, but pgvector's changelog has no PG19 entry up to 0.8.7. **Use PG18 until pgvector confirms PG19.**

### 3.2 Vector extensions

| Extension | Version / date | Key features | License | Status notes |
|---|---|---|---|---|
| **pgvector** | 0.8.7 (2026-10-01); 0.9.0 unreleased (async I/O for indexes, drops PG13/14) | HNSW and IVFFlat. `vector` indexable to 2,000 dims, `halfvec` to 4,000, `bit` to 64,000. `sparsevec`. `binary_quantize` + rerank. **Iterative index scans** (0.8.0) via `hnsw.iterative_scan = strict_order/relaxed_order` and `hnsw.max_scan_tuples` (default 20,000) for filtered search. Partial indexes and partitioning for filters. | PostgreSQL | 0.8.3 (2026-06-17) "Fixed possible index corruption with HNSW vacuuming"; 0.8.2/0.8.7 fixed buffer overflows. Track minor releases. |
| **pgvectorscale** | 0.9.1 (2026-09-04) | StreamingDiskANN, SBQ, label filtering via `smallint[]` + `&&`. Other WHERE clauses are post-filters. | PostgreSQL | Tiger Data, 2026-10-13: pgvectorscale "will enter maintenance mode". |
| **pg_vectorsearch** | alpha (announced 2026-10-13) | PRISM partition/compression index. Vendor claim at 1M vectors: 280 MB index; 9 ms p50 at 99% recall on a 4 GB box and 15 ms on a 2 GB box, against HNSW at 2.05 GB, 392 ms and a 21.7 h build. | PostgreSQL | "currently in alpha"; not production-ready. |
| **VectorChord (vchord)** | 1.1.1 (2026-02-28); 1.1.0 added `rabitq8`/`rabitq4` types | RaBitQ IVF (`vchordrq`), graph index, prefilter, pgvector-compatible types and syntax. Vendor: "RaBitQ8 maintains high precision with <1% recall loss". | **AGPLv3 or ELv2** | Repo now redirects to `supervc-stack/VectorChord`; the docs site was unreachable when I tried. |
| **pgvecto.rs** | — | — | Apache-2.0 | README says to migrate to VectorChord. |

### 3.3 BM25 / full-text extensions

| Option | Version | BM25? | RU/DA | License | Notes |
|---|---|---|---|---|---|
| Built-in `tsvector` + `ts_rank`/`ts_rank_cd` | core | No (ranking is not BM25) | Yes: `russian` and `danish` Snowball configs ship with Postgres | PostgreSQL | pgvector's README pairs it with RRF for hybrid search. |
| **pg_textsearch** | v1.5.1 (2026-10-02); 1.5.0 (2026-10-01) | Yes: BM25 with `k1`/`b`, Block-Max WAND | Uses any PG `text_config`; README shows per-language partial indexes | PostgreSQL | `ORDER BY content <@> 'q' LIMIT k`. `tsquery` boolean filtering. Pre-filter through B-tree indexes; post-filter scans grow until `LIMIT` is filled. Needs `shared_preload_libraries`. No term positions, so no phrase scoring (phrase checks are rechecked against the row). Windows and PG19 support added in 1.5.0. |
| **ParadeDB pg_search** | v0.27.0 (2026-11-19) | Yes (Tantivy) | Snowball stemmers including `danish` and `russian` | **AGPL-3.0** (commercial Enterprise edition available) | Now one `USING paradedb` index for text + vector (beta since 0.25; 0.27+ recommended) + filters. RRF recipe in the docs. EF Core integration repo exists. |
| **VectorChord-bm25** | 0.3.0 (2025-12-15) | Yes (Block-WeakAnd) | Through `pg_tokenizer` analyzers or models | **AGPLv3 or ELv2** | Needs the separate `pg_tokenizer` extension. Release activity is slow. |

### 3.4 Hybrid search in SQL (RRF)

ParadeDB's documented pattern works the same with pgvector + pg_textsearch. Each branch is an `ORDER BY … LIMIT N` with `RANK() OVER(...)`, and the results are fused with `sum(weight/(60+rank))`:

```sql
WITH text AS (
  SELECT id, RANK() OVER (ORDER BY content <@> to_bm25query($q,'mem_en_idx')) r
  FROM chunks WHERE lang='en' AND channel=$ch AND ts >= $from
  ORDER BY content <@> to_bm25query($q,'mem_en_idx') LIMIT 50),
vec AS (
  SELECT id, RANK() OVER (ORDER BY emb <=> $v) r
  FROM chunks WHERE channel=$ch AND ts >= $from
  ORDER BY emb <=> $v LIMIT 50)
SELECT id, sum(w) score FROM (
  SELECT id, 1.0/(60+r) w FROM text UNION ALL SELECT id, 1.0/(60+r) FROM vec) u
GROUP BY id ORDER BY score DESC LIMIT 10;
```

This shape follows paradedb.com/docs/reference/hybrid/rrf and pg_textsearch's partial-index usage. Set `hnsw.iterative_scan` so filtered HNSW queries still return enough rows. pgvector-dotnet ships a C# hybrid (RRF) example (`examples/Hybrid`).

### 3.5 Memory footprint

- `shared_buffers` defaults to 128 MB; docs suggest 25% of RAM on a dedicated server.
- pgvector's README recommends `halfvec` and binary quantization "for a smaller working set".
- Inference, to measure locally:
  - 200k × 1024 `halfvec` is about 0.4 GB of heap plus a similar-sized HNSW index.
  - A tuned container (`shared_buffers` 256–512 MB, modest `work_mem`/`maintenance_work_mem`) should fit in 1–1.5 GB.
  - 1M × 1024 under HNSW will press against a 2 GB budget. Tiger Data's vendor test shows HNSW struggling at 1M on 4 GB.
  - A Docker `mem_limit` plus a matching `shared_buffers` is essential.

### 3.6 Erasure

- **Standard VACUUM** "removes dead row versions… marks the space available for future reuse", but it does not return space to the OS. `VACUUM FULL`, `CLUSTER` and `REPACK` write a complete new table file (PG docs §24.1.2).
- Inference: plain VACUUM does not zero out freed bytes. Old row images also stay in WAL segments until recycled, and in any base backups or dumps. For guaranteed physical removal you need:
  1. VACUUM FULL or REPACK, plus REINDEX of the HNSW and BM25 indexes (pgvector recommends reindexing before vacuuming HNSW anyway);
  2. a CHECKPOINT and WAL recycling;
  3. rotation of backups.
- pg_textsearch compaction rewrites segments with ≥50% dead documents. Tombstones exist before that point.

### 3.7 Encryption at rest

- **Community Postgres:** pgcrypto for individual columns, or file-system/block encryption. There is no built-in TDE (PG docs §18.8, current = 19).
- **Percona pg_tde 2.2.0** (2026-05-20): AES-256, `pg_tde_upgrade`. It needs Percona Distribution for PostgreSQL ≥ 17.10 / 18.4.
- **Practical route here:** encrypt the Docker VM disk or host volume (BitLocker on the Windows host's VHDX).

### 3.8 Backup and operations

- `pg_dump` / `pg_restore` is enough at this size.
- pgBackRest v2.60.1 (2026-11-16) is active but too heavy for this setup.
- Major-version upgrades need dump/restore or `pg_upgrade`, and every extension must be rebuilt for the new major version.
- Inference: pgvector publishes Docker images (`pgvector/pgvector:pg18`); pg_textsearch publishes prebuilt Linux amd64/arm64 binaries. A small custom Dockerfile layering the two is the low-effort path.

### 3.9 .NET support

- **Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0** (net10.0); supports PG18 features.
- **Pgvector NuGet 0.3.2** (MIT): `dataSourceBuilder.UseVector()`, `Vector`/`HalfVector`/`SparseVector`, plus Dapper and EF Core packages.
- pg_textsearch has no .NET package; it is plain SQL operators (`<@>`, `to_bm25query`), so raw SQL through Npgsql works.

---

## 4. Licensing

| Component | License | Concern for a BSD-3 public repo (Cortex) |
|---|---|---|
| SQLite | Public domain | None |
| sqlite-vec | MIT / Apache-2.0 | None |
| SQLite3MC | MIT | None |
| libSQL, Turso | MIT | None |
| DuckDB | MIT (not re-verified this session) | None |
| PostgreSQL, pgvector, pgvectorscale, pg_textsearch, pg_vectorsearch | PostgreSQL license (OSI, permissive) | None |
| ParadeDB Community | AGPL-3.0 | Using the unmodified image as a separate service is generally aggregation, but modifying it and exposing it over a network triggers source-sharing. Lawyers differ on edge cases. **Not legal advice.** |
| VectorChord, VectorChord-bm25 | AGPLv3 **or** ELv2 | ELv2 restricts offering it as a managed service; AGPL caveats as above. |
| Percona pg_tde | Open source (Percona distribution) | Requires the Percona server build. |

---

## 5. Benchmarks (100k–1M scale where available)

| Benchmark | Scale | Result | Independence |
|---|---|---|---|
| sqlite-vec PRs #276–278 | 1M × 1024, M4 | Flat 590 ms; rescore-bit 92–101 ms (recall 0.962–0.988); IVF 37–61 ms; DiskANN 4.6–33.6 ms (DB 10–12 GB, 42–55 min inserts) | Author's own |
| Tiger Data, pg_vectorsearch launch (2026-10-13) | 1M | PRISM 280 MB index, 9–15 ms p50 at 99% recall on 2–4 GB; HNSW (`halfvec`) 2.05 GB, 392 ms at 99% recall, 21.7 h build on 4 GB | Vendor |
| pg_textsearch vs ParadeDB 0.21.6 (2026-03-10) | MS MARCO 8.8M passages | p50 0.70–26 ms vs 18.8–44.9 ms; index 1,215 MB vs 1,503 MB (pg_textsearch stores no positions); build 234 s vs 140 s | Vendor (Timescale); single run, ±10% |
| VectorChord vs pgvector | (docs) | "QPS could be twice that of pgvector"; index build 16× faster using external k-means | Vendor |
| pgvectorscale vs Pinecone | 50M × 768 | 28× lower p95 latency at 99% recall | Vendor (older) |

**Gap:** I found no independent benchmark of filtered + hybrid search (BM25 + vector + time/channel filters) at 100k–1M across these engines. You will need to measure it on your own data (see §7).

---

## 6. Ranking for Cortex's constraints

| Rank | Option | RAM | Ops (≈2 h/week) | Hybrid / multilingual | Erasure | Encryption | Migration effort | Main risks |
|---|---|---|---|---|---|---|---|---|
| **1 (now)** | **Stay on SQLite:** FTS5 (`secure-delete=1`, unicode61 + optional fts5-snowball or trigram) + sqlite-vec 0.1.9 (metadata `channel`/`kind`; partition key on e.g. a month bucket) + SQLite3MC; RRF in C# | Tens of MB | Lowest: file copy or `VACUUM INTO` backups | BM25 yes; RU/DA stemming needs an extra extension | Strong: `secure_delete` + FTS5 secure-delete + VACUUM + WAL truncate | SQLite3MC (free, MIT) | Lowest (incremental) | Brute force ≈0.6 s at 1M on M4; ANN still alpha; sqlite-vec pre-v1; extension loading under SQLite3MC unverified |
| **2 (scale path)** | **PG18 + pgvector 0.8.7 (`halfvec` HNSW, iterative scans) + pg_textsearch 1.5.1** | ~0.5–1.5 GB tuned (estimate) | Medium: custom image, autovacuum, minor/extension updates, major upgrades | Best: true BM25, `russian`/`danish` configs, SQL filters, RRF | Needs VACUUM FULL/REPACK + REINDEX + WAL/backup hygiene | Disk-level only (or Percona pg_tde) | Medium: data move + query rewrite; Npgsql/EF Core mature | Postgres ops overhead; HNSW memory at 1M; pg_textsearch is young (1.x, corruption fixes in 1.5.1) |
| 3 | PG18 + VectorChord (+ VectorChord-bm25 / pg_tokenizer) | Lowest RAM per vector among Postgres options (RaBitQ) | Medium-high: extra extensions, prebuilt images | Good; BM25 tokenizer configuration is more work | As Postgres | As Postgres | Medium | AGPL/ELv2; org move; bm25 releases slow |
| 4 | ParadeDB 0.27 (one index for text + vector + filters) | Higher (Tantivy + Postgres) | Medium | Very good BM25; vector search is beta | As Postgres, plus Tantivy segment merges | As Postgres | Medium | AGPL; vectors beta; heavier image |
| 5 | libSQL with DiskANN, or Turso 0.8 | Low | Low-medium | libSQL: no BM25 beyond FTS5. Turso: Tantivy FTS experimental, no ANN index | Unverified for DiskANN shadow data | Turso native (experimental); libSQL unclear | Medium: different .NET stack | libSQL has no new features; Turso still pre-1.0 |
| 6 | DuckDB 2.0 VSS + FTS | Index must fit in RAM | Low | FTS BM25 + Snowball (must fix the Cyrillic regex) | Deletes only marked; compaction needed | None built in (not checked) | Medium | Experimental HNSW persistence (corruption risk); OLAP engine |
| — | pgvectorscale / pg_vectorsearch / pgvecto.rs | — | — | — | — | — | — | Maintenance mode / alpha / superseded |

**When to switch from SQLite to Postgres (inference):**
- p95 hybrid query latency goes above about 150–200 ms at the current corpus size;
- the corpus passes about 300k chunks;
- RU/DA keyword recall measurably hurts results; or
- you want concurrent writers from several services.

Keep an `IMemoryStore` abstraction so the backend swap is contained.

---

## 7. Suggested next steps

1. Benchmark on your own data with the real embedding model, the Docker VM, and filters on time and channel:
   - sqlite-vec 0.1.9 flat at 10k, 200k and 1M (synthetic);
   - pgvector 0.8.7 `halfvec` HNSW with `iterative_scan`;
   - pg_textsearch with per-language partial indexes.
2. Check that sqlite-vec loads under SQLite3MC through `Microsoft.Data.Sqlite.Core` + `SQLite3MC.PCLRaw.bundle` + `LoadExtension`.
3. Check exactly what happens on erasure: after a `vec0` row is deleted (and `PRAGMA secure_delete=ON`), do the vector bytes still exist in the chunk blob? Run a byte-grep test on the database file after delete + VACUUM. Release notes suggest vectors are reclaimed only per chunk.
4. Re-check pgvector PG19 support (0.9.0) before choosing PG19.

---

## 8. Sources

| Title | Org | Date | URL |
|---|---|---|---|
| sqlite-vec releases | asg017 | 2026-03-17 → 2026-05-18 | https://github.com/asg017/sqlite-vec/releases |
| PR #276 rescore / #277 IVF / #278 DiskANN | asg017 | 2026-03-31 | https://github.com/asg017/sqlite-vec/pull/276 (277, 278) |
| vec0 metadata / partition / aux docs | Alex Garcia | n/d | https://alexgarcia.xyz/sqlite-vec/features/vec0.html |
| SQLite FTS5 (secure-delete, tokenizers) | sqlite.org | crawled 2026-11 | https://sqlite.org/fts5.html |
| PRAGMA secure_delete | sqlite.org | — | https://www.sqlite.org/pragma.html#pragma_secure_delete |
| fts5-snowball | abiliojr | — | https://github.com/abiliojr/fts5-snowball |
| SQLitePCLRaw v3 notes | ericsink | — | https://github.com/ericsink/SQLitePCL.raw/blob/main/v3.md |
| SQLite3MC.PCLRaw.bundle 2.3.6 | utelle / NuGet | 2026 | https://www.nuget.org/packages/SQLite3MC.PCLRaw.bundle/2.3.6 |
| Microsoft.Data.Sqlite Encryption / Extensions | Microsoft Learn | 2025-07-31 / 2026-03-30 | https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/encryption ; …/extensions |
| AI & Embeddings (libSQL vectors) | Turso | crawled 2026-11 | https://docs.turso.tech/features/ai-and-embeddings |
| Turso v0.5.0 / v0.7.0 | Turso | 2026-03-04 / 2026-07-13 | https://turso.tech/blog/turso-0.5.0 ; https://turso.tech/blog/turso-0.7.0 |
| Turso releases (0.8.4) | Turso | 2026-11-20 | https://github.com/tursodatabase/turso/releases |
| Turso vector-search reference | Turso agent-skills | — | https://github.com/tursodatabase/agent-skills/blob/main/skills/turso-db/references/vector-search.md |
| Turso encryption | Turso | 2026-11-18 | https://docs.turso.tech/tursodb/encryption |
| libSQL README | Turso | — | https://github.com/tursodatabase/libsql |
| DuckDB VSS / FTS docs (2.0) | DuckDB | 2026 | https://duckdb.org/docs/2.0/core_extensions/vss ; …/full_text_search |
| Announcing DuckDB 2.0.0 LTS | DuckDB | 2026-10-21 | https://duckdb.org/2026/10/21/announcing-duckdb-200 |
| pgvector CHANGELOG / README | pgvector | 0.8.7 = 2026-10-01 | https://github.com/pgvector/pgvector |
| pgvector-dotnet / Pgvector NuGet 0.3.2 | pgvector | — | https://github.com/pgvector/pgvector-dotnet ; https://www.nuget.org/packages/Pgvector |
| pgvectorscale README / releases | Tiger Data | 0.9.1 = 2026-09-04 | https://github.com/timescale/pgvectorscale |
| Announcing pg_vectorsearch | Tiger Data | 2026-10-13 | https://www.tigerdata.com/blog/announcing-pg_vectorsearch-scalable-open-source-vector-search-postgresql |
| pg_textsearch README / releases | Tiger Data | v1.5.1 = 2026-10-02 | https://github.com/timescale/pg_textsearch |
| pg_textsearch vs ParadeDB | Tiger Data | 2026-03-10 | https://timescale.github.io/pg_textsearch/benchmarks/comparison.html |
| VectorChord README / LICENSE / releases | TensorChord (supervc-stack) | 1.1.1 = 2026-02-28 | https://github.com/supervc-stack/VectorChord |
| VectorChord-bm25 | TensorChord | 0.3.0 = 2025-12-15 | https://github.com/supervc-stack/VectorChord-bm25 |
| pgvecto.rs README | TensorChord | — | https://github.com/tensorchord/pgvecto.rs |
| VectorChord vs pgvector benchmark | TensorChord | — | https://docs.vectorchord.ai/vectorchord/benchmark/pgvector.html |
| ParadeDB README / LICENSE / releases | ParadeDB | v0.27.0 = 2026-11-19 | https://github.com/paradedb/paradedb |
| ParadeDB vector, RRF, stemmer docs | ParadeDB | 2026 | https://www.paradedb.com/docs/reference/hybrid/rrf.md ; …/token-filters/stemming.md |
| PostgreSQL versioning policy | PGDG | 2026-11 | https://www.postgresql.org/support/versioning/ |
| PostgreSQL 19 press kit / release notes | PGDG | 2026-11-19 | https://www.postgresql.org/about/press/presskit19/en/ |
| REPACK; Routine Vacuuming; Encryption Options; Resource Consumption | PGDG (docs 19) | 2026-11 | https://www.postgresql.org/docs/current/sql-repack.html (etc.) |
| Text search psql (`russian` config) | PGDG | — | https://www.postgresql.org/docs/current/textsearch-psql.html |
| pg_tde 2.2.0 release notes | Percona | 2026-05-20 | https://docs.percona.com/pg-tde/release-notes/release-notes-v2.2.0.html |
| pgBackRest releases | pgBackRest | 2.60.1 = 2026-11-16 | https://github.com/pgbackrest/pgbackrest/releases |
| Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0 | Npgsql | 2025-11 | https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/10.0.0 |

---

## 9. Uncertainty notes

- **Vendor benchmarks:** everything except sqlite-vec's author numbers comes from the vendor and was run on very different hardware and datasets. None covers filtered + hybrid search at our scale.
- **Release dates without a year on GitHub:** I took the year from the asset timestamps (for example sqlite-vec v0.1.9 assets dated 2026-03-31). For pg_textsearch 1.5.x, VectorChord 1.1.x and pgvectorscale 0.9.1, I inferred 2026 from context; it was not confirmed by asset timestamps.
- **Not verified:**
  - whether sqlite-vec works under SQLite3MC or SQLCipher;
  - whether a `vec0` DELETE physically wipes vector bytes;
  - the DuckDB license (assumed MIT);
  - a .NET package for libSQL embedded;
  - Postgres RAM figures for our workload (estimates only).
- **VectorChord:** the docs site was unreachable during this session, and the move to `supervc-stack` is unexplained. Its long-term governance is uncertain.
- **Licensing:** the AGPL and ELv2 interpretations in §4 are general and not legal advice.
