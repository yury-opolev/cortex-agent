# Hybrid memory search engine options for Cortex (late 2026)

## 1. Summary

- **Qdrant is the best fit for your constraints, and I'm fairly confident about that.** It is written in Rust, licensed Apache-2.0, and the current release is v1.20.2 (2026-11-16). v1.20.0 (2026-10-22) added true BM25 scoring directly on a payload full-text index, so you no longer need sparse-vector workarounds. It already had:
  - Snowball stemmers, including Russian and Danish (since v1.15).
  - A multilingual tokenizer and stopwords.
  - RRF, weighted RRF (v1.17) and DBSF fusion through the Query API.
  - Filtering inside the HNSW index.
  - Per-component memory tiers (pinned/cached/cold, v1.19) and TurboQuant quantization (v1.18–1.20).
  - Collection and full-storage snapshots.
  - An official gRPC .NET client, Qdrant.Client 1.20.0, with C# examples for BM25 and RRF.
- **Qdrant's main caveats:**
  - BM25-on-payload is only one month old.
  - Open-source Qdrant has no encryption at rest.
  - Deletes are soft until the vacuum optimizer runs.
  - The Microsoft.Extensions.VectorData (MEVD) Qdrant connector's `HybridSearchAsync` does **not** use BM25 ranking (details in section 4.11).
- **LanceDB (embedded Rust) is the most memory-efficient runner-up.** It has a Lance-native BM25 index with stemming, RRF hybrid search, and disk-first IVF_RQ/IVF_PQ indexes. But the open-source version is a library, not a server: you would write and run the Rust service yourself. There is no official .NET SDK, only a community P/Invoke wrapper with 18 stars.
- **Everything else is either a poorer fit or too heavy:**
  - **Meilisearch:** ranks with rules, not BM25; no stemming for Russian or Danish (normalization only); LMDB never returns freed disk space.
  - **Weaviate:** keeps HNSW in RAM (its rule of thumb is 2× vector memory); no stemming found in its tokenizers.
  - **Typesense:** in-memory and GPL-3.0.
  - **Milvus standalone:** needs 8 GB RAM minimum, 16 GB recommended.
  - **Chroma:** its hybrid Search API is Cloud-only.
  - **Turso:** vector indexing is still on the roadmap and full-text search is experimental.
  - **Vespa:** needs at least 4 GB RAM for the container.
  - **Elasticsearch/OpenSearch:** JVM-based and heavy.
  - **Microsoft Kernel Memory:** archived.
- **Best in-process .NET alternative:** Lucene.NET (BM25, Russian and Danish analyzers, but still 4.8.0-beta00018) plus USearch's C# binding (v2.26.6, requires .NET 10, Linux x64/macOS arm64 native assets only). You would write the fusion, filtering and erasure logic yourself.

Note: `github/get_me` was not available in this environment, so I skipped it. All facts below come from official docs, GitHub API/README/source, or NuGet as fetched on 2026-11-21.

---

## 2. Main options and where they come from

| Repo / package | Purpose | Latest version (date) | License |
|---|---|---|---|
| `qdrant/qdrant` | Rust vector database | v1.20.2 (2026-11-16) | Apache-2.0 |
| `qdrant/qdrant-dotnet` / NuGet `Qdrant.Client` | Official .NET gRPC client | 1.20.0 (2026-10-22), about 3.49M downloads | Apache-2.0 |
| `lancedb/lancedb`, `lancedb/lance` | Embedded Rust vector DB and columnar format | lancedb v0.41.0 (2026-10-29); lance v14.0.3 (2026-11-15) | Apache-2.0 |
| `lennylxx/lancedb-csharp` / NuGet `LanceDB` | Community C# P/Invoke wrapper over the Rust crate | 2.5.0 (2026-07-05), 18 stars | Apache-2.0 |
| `quickwit-oss/tantivy` | Rust BM25 library | 0.26.1 (2026-05-10) | MIT |
| `meilisearch/meilisearch` | Rust search engine with hybrid search | v1.57.0 (2026-11-16) | MIT core + BUSL-1.1 Enterprise Edition |
| `chroma-core/chroma` | Rust-core vector DB | 1.5.9 (2026-05-05) | Apache-2.0 |
| `tursodatabase/turso` | Rust rewrite of SQLite | v0.8.4 (2026-11-20) | MIT |
| `tursodatabase/libsql` | SQLite fork | last server release v0.24.32 (2025-02-14) | MIT |
| `milvus-io/milvus` | Go/C++ vector DB | v3.0.6 (2026-11-11) | Apache-2.0 |
| `milvus-io/milvus-lite` | Python-only local Milvus | v3.2.2 (2026-10-13) | Apache-2.0 |
| `weaviate/weaviate` | Go vector DB | v1.41.0-rc.1 (2026-11-20) | BSD-3-Clause, except a `wl` directory under the "Weaviate License" |
| `typesense/typesense` | C++ search engine | v30.2 (2026-04-19) | GPL-3.0 |
| `vespa-engine/vespa` | Java/C++ search engine | v8.788.13 (2026-11-16) | Apache-2.0 |
| `elastic/elasticsearch` | JVM search engine | v9.5.6 (2026-11-02) | AGPL-3.0 / SSPL / ELv2 triple license |
| `opensearch-project/OpenSearch` | JVM search engine | 3.10.0 (2026-11-18) | Apache-2.0 |
| `unum-cloud/usearch` / NuGet `Cloud.Unum.USearch` | C++ HNSW with C# binding | v2.26.6 (2026-10-25) | Apache-2.0 |
| `curiosity-ai/hnsw-sharp` / NuGet `HNSW` | Pure C# HNSW (HNSW.Net) | 26.11.7916 (2026-11-10) | MIT |
| `apache/lucenenet` | Lucene.NET | 4.8.0-beta00018 (tag 2026-06-22) | Apache-2.0 |
| `microsoft/kernel-memory` | Kernel Memory | last packages 0.98.250508.3 (2025-05-09); **archived research project** | MIT |
| `CommunityToolkit/AI` | Current MEVD connectors (`CommunityToolkit.VectorData.*`) | 1.0.x | MIT |
| NuGet `Microsoft.Extensions.VectorData.Abstractions` | .NET vector store abstractions | 11.0.1 (2026-11-21) | MIT |
| `zilliztech/VectorDBBench` | Benchmark tool | v2.0.0 (2026-09-10) | MIT |
| `erikbern/ann-benchmarks` | Benchmark tool | no releases; pushed 2026-07-10 | MIT |
| `asg017/sqlite-vec` | SQLite vector extension | v0.1.9 (2026-03-31), "pre-v1" | Apache-2.0 |
| `paradedb/paradedb` | Postgres BM25 extension (pg_search) | v0.27.0 (2026-11-19) | AGPL-3.0 |
| `pgvector/pgvector` | Postgres vector extension | tag v0.8.7 | PostgreSQL License (GitHub API shows NOASSERTION) |

Note: the Semantic Kernel MEVD connectors (`Microsoft.SemanticKernel.Connectors.Qdrant`, PgVector, SqliteVec, InMemory and others) stopped at **1.74.0-preview (2026-03-20)**. Work continues as `CommunityToolkit.VectorData.*` (Qdrant 1.0.1, PgVector 1.0.2, SqliteVec, Weaviate 1.0.1, Redis, SqlServer, Chroma 1.1.0-preview, Milvus 1.0.0-preview) in `github.com/CommunityToolkit/AI`, which Microsoft Learn now points to.

---

## 3. Comparison table

All RAM figures for 1M×1024 are **my estimates** from documented sizing formulas or vendor guidance (see section 6). They exclude OS page cache unless noted.

| Engine | Language | Hybrid / BM25 | Multilingual stemming (RU/DA) | Filtering | Quantization | RAM at 1M×1024 (est.) | Erasure (delete semantics) | Encryption at rest (self-hosted) | .NET client | License | Maturity |
|---|---|---|---|---|---|---|---|---|---|---|---|
| **Qdrant 1.20** | Rust | **True BM25** on full-text payload index (v1.20), or sparse vectors with IDF / local BM25 inference (v1.15.2); RRF, weighted RRF, DBSF; nested prefetch | Snowball (rust-stemmers: Russian, Danish), multilingual tokenizer (charabia), stopwords; stemmer set per field | Filtering inside the HNSW index; payload indexes | Scalar int8, binary 1–2 bit, PQ up to 64×, TurboQuant 4–32×; turbo4/8/16 storage types | **~0.7–1.2 GB** (originals cold on disk, turbo4/SQ pinned 0.5–1.0 GB, HNSW about 0.15 GB, plus text/payload indexes); about 4.3 GB+ if all float32 in RAM | Soft-delete; vacuum rebuilds a segment when more than `deleted_threshold` (e.g. 0.2) of it is deleted | **None in open source** (TLS and API keys only); customer-managed keys are a Cloud Premium feature | **Official** gRPC client 1.20.0; MEVD connector exists but its hybrid search isn't BM25-ranked | Apache-2.0 | High (35.6k stars, frequent releases) |
| **LanceDB 0.41 (embedded)** | Rust | Lance-native BM25 FTS (Tantivy mode removed); hybrid with RRF default plus other rerankers | `language` param with stemming and stopwords; ICU, jieba, lindera tokenizers | Prefilter (default) or postfilter; scalar indexes | IVF_RQ (~1/32), IVF_PQ, IVF_HNSW_SQ, IVF_HNSW_FLAT | **~0.2–0.8 GB** working set (disk-first; IVF_RQ 1-bit ≈ 128 MB of codes) | Rows marked deleted; compaction rewrites; cleanup of old versions (default retention **7 days**) needed for physical removal | Not documented for local open source (unverified) | **No official SDK** (Python, TypeScript, Rust); community C# wrapper (18 stars) | Apache-2.0 | Medium-high library; **no open-source server** (you build it) |
| **Meilisearch 1.57** | Rust | Hybrid via `semanticRatio`; **not BM25** (bucket-sort ranking rules); user-provided/REST/HuggingFace embedders | Charabia normalization; **no stemming** (Cyrillic: decomposition and lowercase only) | Filterable attributes | Binary quantization | ~1–2 GB (LMDB memory-mapped; vendor says RAM:disk around 1/3 is fine) | LMDB marks pages free but **never returns disk space**; no compaction | Not documented for self-hosting | `Meilisearch` 0.20.0 (0.x) | MIT, with BUSL Enterprise Edition (sharding only) | High |
| **Tantivy 0.26** (library) | Rust | BM25 (K1=1.2, B=0.75); **no vectors** | Snowball, including Danish and Russian | Fast fields / query filters | n/a | Low (memory-mapped segments) | Deleted docs removed at segment merge (from my knowledge; not re-verified) | None built in | None (Rust only) | MIT | High |
| **Chroma 1.5.9** | Rust core | Local: `$contains`/`$regex` filters only; **Search API (RRF, sparse/BM25) is Cloud-only** | n/a locally | Metadata `where` | (HNSW) | ~4.5 GB+ (in-RAM HNSW; my estimate) | Delete API; compaction not reviewed | Not reviewed | Community connector (`CommunityToolkit.VectorData.Chroma` preview) | Apache-2.0 | Medium |
| **Turso 0.8** (in-process) | Rust | FTS via Tantivy (**experimental**); exact vector search; **vector indexing on roadmap** | Via Tantivy | SQL | — | n/a at 1M (brute force) | SQL delete | **Experimental** encryption at rest | `Turso.Data.Sqlite.Provider` 0.8.3 (~4k downloads) | MIT | Early |
| **Milvus 3.0** | Go/C++ | Native BM25 function and analyzers; hybrid with RRF/weighted | Stemmer filter including Russian and Danish | Scalar filtering | SQ, PQ, RaBitQ and others | **Standalone min 8 GB / recommended 16 GB** (docs) | Soft-delete plus compaction (not re-verified) | Not reviewed | `Milvus.Client` 3.0.1 (2026-11-18) | Apache-2.0 | High, but heavy; Milvus Lite is Python-only |
| **Weaviate 1.40/1.41** | Go | BM25/BM25F (BlockMaxWAND); hybrid with relativeScoreFusion default (since 1.24) or ranked fusion | word/whitespace/field/trigram/gse/kagome tokenizers; **no stemming found** | Roaring-bitmap filters; range index is opt-in | PQ, BQ, SQ, RQ; HFresh disk index | Rule of thumb **2× vector memory → ~8 GB** uncompressed; with RQ/PQ/HFresh maybe 1.5–3 GB (my estimate) | LSM store; not reviewed | Not reviewed | `Weaviate.Client` 1.2.0 (repo has 7 stars) | BSD-3 plus Weaviate License (`wl` dir) | High |
| **Typesense 30.2** | C++ | Own text-match score (not documented as BM25); hybrid rank fusion `0.7*K + 0.3*S` (adjustable via alpha) | Snowball stemming chosen by `locale` | Yes | — | Entire index in RAM; keyword index needs 2–3× indexed text size, plus vectors about 4 GB+ | — | — | Community `Typesense` 9.2.0 (DAXGRID) | **GPL-3.0** | High |
| **Vespa 8** | Java/C++ | Best-in-class BM25, nativeRank, phased ranking | Linguistics (not reviewed) | Yes | Yes | **Min 4 GB for the container** (docs) | — | — | No official .NET client | Apache-2.0 | High, but heavy ops |
| **Elasticsearch 9.5 / OpenSearch 3.10** | Java | BM25 with full analyzer chain (Russian and Danish analyzers); RRF | Yes | Yes | int8, int4, BBQ (ES) | JVM heap plus off-heap vectors; realistically ≥2–4 GB (my estimate) | Segment merges | Not reviewed | Official `Elastic.Clients.Elasticsearch` 9.5.3; `OpenSearch.Client` 2.3.0 | ES: AGPL/SSPL/ELv2; OpenSearch: Apache-2.0 | Very high, heavy |
| **Postgres + pgvector + pg_search** (alternative) | C/Rust extensions | pg_search BM25 (Tantivy-based) plus pgvector; fuse in SQL (RRF) | Tantivy tokenizers and stemmers (not fully verified) | Full SQL | halfvec, bit (pgvector) | ~2–3 GB (my estimate) | VACUUM | Disk-level only (community Postgres) | Npgsql / EF Core / MEVD PgVector | PostgreSQL License + **AGPL** (pg_search) | High |
| **Lucene.NET + USearch** (in-process) | C# + C++ | BM25Similarity; you implement the fusion (RRF) | Russian and Danish analyzers in `Lucene.Net.Analysis.Common` | Lucene filters; USearch predicate callbacks | USearch BF16/F16/I8/U8/U1x8 and others | ~0.3–1.3 GB (I8 ≈ 1.0 GB, U1x8 ≈ 128 MB, plus graph) | Lucene deletes at merge; USearch is a separate store, so you keep the two consistent | None built in | Native .NET | Apache-2.0 | Lucene.NET is permanently beta; USearch C# API recently redesigned |
| **Kernel Memory** | C# | — | — | — | — | — | — | — | — | MIT | **Archived** ("not production software") |

---

## 4. Detailed findings by engine (with citations)

### 4.1 Qdrant (recommended)

**BM25 on the full-text index (new in v1.20.0, 2026-10-22)**
- Release notes: "#10750 – BM25 scoring directly on the full-text payload index"; TurboQuant `turbo8`/`turbo16` data types; a new `code` tokenizer. Source: GitHub release v1.20.0.
- Docs: "Qdrant computes BM25 scores from a full-text index on a payload field… set `scoring` to `true`." The `k` (default 1.2) and `b` (default 0.75) parameters are set per query.
- Matching is OR over query terms ("A point matches the query if its text contains at least one of the query terms").
- A text query can be used as a prefetch for hybrid search.
- An official C# example exists:
  ```csharp
  await client.QueryAsync("books",
      query: new Query { Text = new TextQuery { Query = "time travel" } },
      usingVector: "title", payloadSelector: true, limit: 10);
  ```
  Source: qdrant.tech/documentation/search/text-search/full-text-search/
- Tokenizer, lowercasing, stemming and stopwords apply to both indexed text and BM25 queries. A full-text index with scoring "uses more memory and disk space than one without". Source: qdrant.tech/documentation/manage-data/indexing/

**Older BM25 path (still available)**
- v1.15.2 (2025-08-11): "Implement BM25 inference in Qdrant locally" (server-side sparse vectors).
- v1.15.3: a BM25 parameter fix.
- v1.19.0: "Per-query IDF corpus for sparse vector search".
- v1.19.2: "Move BM25 sparse embedding to blocking thread".

**Multilingual text support (v1.15.0, 2025-07-18)**
- Added phrase matching, stopwords, a multilingual tokenizer enabled by default, and the Snowball stemmer.
- Stemming is off by default and is configured per index as `"stemmer": {"type":"snowball","language":"english"}`. The supported languages are those of rust-stemmers, which include **Danish and Russian** (CurrySoftware/rust-stemmers README).
- Design implication (my inference): you get one stemmer language per indexed field. For EN/RU/DA, either:
  - index language-specific fields (`text_en`, `text_ru`, `text_da`, filled according to detected language), or
  - use one multilingual, unstemmed field and fuse multiple BM25 prefetches.

**Hybrid search and fusion**
- The Query API has supported `prefetch` with `rrf`/`dbsf` fusion since v1.10.
- RRF is computed as `1/(k + (r_d+1)/w_r − 1)` with k=2 by default.
- Weighted RRF was added in v1.17.0 (2026-02-20).
- Source: qdrant.tech/documentation/search/hybrid-queries/

**Memory and sizing (capacity-planning docs)**
- Dense vectors: `points × dims × bytes_per_dim`. Datatypes: float32 4 B, float16/turbo16 2 B, uint8/turbo8 1 B, turbo4 0.5 B.
- Cold-tier vectors need no up-front RAM budget.
- HNSW size: `points × m × 2 × 4 B × 1.2` (m=16 by default).
- Payload: `ram = payload × 1.5 × 3` if cached, so **keep payload cold**.
- Docs example: TurboQuant 4-bit pinned with originals cold reduces RAM roughly 8×.

**Memory tiers (v1.19.0)**
- Each collection component takes `"memory": "cold" | "cached" | "pinned"`.
- `pinned` is rejected for dense vectors and payloads; `cached` and `cold` are both memory-mapped.
- v1.18.0 added: a memory usage breakdown, a low-memory startup mode, and strict-mode `max_resident_memory_percent`.

**Quantization**
- TurboQuant: 4–32×.
- Scalar: 4×.
- Binary: up to 32×.
- PQ: up to 64×.
- 8-bit TurboQuant "matched or exceeded" scalar quantization recall in Qdrant's own benchmarks (vendor claim).
- v1.19.0 added `turbo4` as a primary storage datatype, so you don't need to keep the originals.

**Deletes and erasure**
- "When you delete a point, Qdrant doesn't remove it right away but marks it as deleted."
- The vacuum optimizer rebuilds a segment once the deleted fraction exceeds `deleted_threshold` (example 0.2) and the segment has at least `vacuum_min_vector_number` points (example 1000).
- Implication (my inference): for GDPR-style erasure you also have to account for old snapshots and the WAL. Small segments may never meet the vacuum threshold.

**Snapshots**
- Collection snapshots and full-storage snapshots (single-node only).
- Snapshots can go to S3; GCS and Azure Blob were added in v1.20.0.
- Several snapshot data-race fixes landed in 1.15.5, 1.18.1 and 1.19.2, so stay on recent patch releases.

**Security and encryption**
- Self-hosted Qdrant offers API keys (read-only and granular), TLS, network binding and audit logging. "Connections are unencrypted by default." No encryption at rest is documented for self-hosting.
- Customer-managed encryption keys are listed under Qdrant Cloud Premium (llms.txt index).

**Embedded option: Qdrant Edge (beta)**
- In-process ("SQLite, but for vector search"), but only Python (`qdrant-edge-py`) and Rust (`qdrant-edge` crate) are available. **No .NET.**

**.NET client and MEVD connector**
- Qdrant.Client 1.20.0 (2026-10-22). The C# examples in the docs cover TextQuery, BM25 scoring parameters and RRF.
- The MEVD connector (`CommunityToolkit.VectorData.Qdrant`) is a different story. Its `HybridSearchAsync` builds the keyword prefetch as a **filter-only** query (a `should` of `Match.Text` conditions) with no ranking query, then applies `Fusion.Rrf`. The keyword leg therefore isn't ranked by BM25. Source: `CommunityToolkit/AI:MEVD/src/Qdrant/QdrantCollection.cs:665-727` at commit f393fbcd (2026-10-12):
  ```csharp
  var keywordQuery = new PrefetchQuery { Filter = keywordFilter, };   // no Query => unranked
  var fusionQuery  = new Query { Fusion = Fusion.Rrf, };
  ```
  **Recommendation:** call Qdrant.Client directly for hybrid search.

### 4.2 LanceDB (embedded)

- **SDKs:** Python, TypeScript and Rust only (docs.lancedb.com/oss/v0-41-0/basics/sdk). The C# wrapper is community-maintained: `lennylxx/lancedb-csharp`, "wraps the official Rust lancedb crate via P/Invoke", v2.5.0, 18 stars. Other packages (`Joust`, `Joust.VectorData`) have fewer than 300 downloads.
- **FTS:**
  - "LanceDB provides performant full-text search based on BM25."
  - "The current FTS implementation is Lance-native. Legacy Tantivy-only options, including `use_tantivy`, are no longer accepted."
  - Parameters: `base_tokenizer` (simple, whitespace, ngram, icu, jieba, lindera), `language` (default English), `stem` (default true), `remove_stop_words`.
  - I didn't verify that Russian and Danish are accepted values for `language`.
- **Hybrid:** RRF is the default reranker; other rerankers are optional.
- **Vector indexes:**
  - `IVF_RQ`: around 1/32 of raw size with `num_bits=1`.
  - `IVF_PQ`: 1/64 to 1/16.
  - `IVF_HNSW_SQ`: a little over 1/4.
  - `IVF_HNSW_FLAT`.
  - The docs advise: "If your vector search frequently includes metadata filters… use IVF_RQ or IVF_PQ", because HNSW-backed indexes show higher latency variance under filters.
  - Brute-force search without an index is described as "fast".
- **Filtering:** prefilter is the default.
- **Deletion:** "Deletion does not imply immediate physical erasure of every stored copy… Retained history can continue to reference data that no longer appears in the latest table version." `cleanup_older_than` defaults to **7 days**, and tags can block cleanup. For erasure you have to run `optimize(cleanup_older_than=…)` with a short window.
- **Ops implication (my inference):** there is no open-source server; "LanceDB Enterprise" is the server product. You would write a Rust (axum/tonic) service plus a .NET client, and own compaction scheduling. Versioning does give you cheap point-in-time backups (copy the directory).

### 4.3 Tantivy

- BM25 with constants `K1 = 1.2` and `B = 0.75` (`quickwit-oss/tantivy:src/query/bm25.rs:8-9`).
- The stemmer `Language` enum includes Danish and Russian (`quickwit-oss/tantivy:src/tokenizer/stemmer.rs:12-31`, mapped to rust-stemmers algorithms at lines 38 and 50).
- It is a library only, with no vector search. Combining it with a vector index means writing a custom Rust service, which has the highest development cost.
- Tantivy is used inside Turso's full-text search (per the Turso README) and in ParadeDB's pg_search (from my knowledge). LanceDB has dropped it.

### 4.4 Meilisearch

**License**
- `MIT AND OpenLDAP AND BUSL-1.1`. Enterprise Edition parts are under BUSL. "The only feature exclusive to the Enterprise Edition is sharding."

**Ranking (not BM25)**
- The docs page is titled "Meilisearch ranking: a multi-criteria system beyond BM25". It describes sequential bucket-sort rules; BM25 is explicitly not the model.

**Languages**
- Charabia tokenizer. For "Cyrillic / Georgian (Russian, Ukrainian…)": default segmentation, decomposition and lowercase only. Danish falls under the Latin pipeline. **No stemming is listed.**

**Storage and RAM**
- LMDB memory-mapped. "A RAM-to-disk ratio around 1/3 does not materially impact performance."
- Vectors are stored in Hannoy, a disk-backed DiskANN-style HNSW on LMDB (v1.29+).
- Binary quantization is available for embeddings.

**Erasure concern**
- "When deleting documents… disk space usage remains the same… LMDB internally marks that space as free… no need for periodic compaction." Disk usage therefore only grows.
- My inference: deleted content may remain in freed pages until they are overwritten. That weakens an erasure guarantee unless you periodically rebuild the index from a dump.

**Embedders**
- Supported: OpenAI, Cohere, Mistral, HuggingFace (local and Inference API), REST, and user-provided vectors. Use `userProvided` or `rest` for Ollama.
- A dedicated "ollama" source is only mentioned in release notes (v1.48.0); I didn't verify it.

**Backup and .NET**
- Snapshots (exact copy) and dumps (for migration).
- .NET SDK `Meilisearch` 0.20.0 (2026-06-23).

### 4.5 Chroma

- Its full-text search is filters only (`$contains`, `$regex`).
- "**Search API is available in Chroma Cloud only.** Future support on single-node Chroma is planned." The Search API is what provides RRF hybrid and sparse/BM25-style search.
- Not suitable for self-hosted hybrid search today.

### 4.6 Turso / libSQL

- Turso README:
  - "Vector support… exact search."
  - Experimental: encryption at rest, and full-text search "powered by tantivy".
  - Roadmap: "Vector indexing for fast approximate vector search, similar to libSQL vector search."
  - .NET binding: `Turso.Data.Sqlite.Provider` 0.8.3.
- "Rewriting SQLite in Rust… replaces libSQL as our intended direction."
- libSQL's last server release was 2025-02-14.
- Not ready for 1M ANN plus BM25 today.

### 4.7 Milvus / Milvus Lite

- Milvus Lite is "a lightweight local backend for the Milvus Python SDK" (v3.2.2), so **no .NET**.
- Standalone RAM: **8 GB minimum, 16 GB recommended**. etcd is embedded in the Docker script deployment; Woodpecker WAL is bundled.
- The stemmer filter supports languages including "danish" and "russian".
- `Milvus.Client` 3.0.1.
- Its features are a good match but it is over the RAM budget.

### 4.8 Weaviate

- Memory: "The HNSW index must be stored in memory… Memory usage = 2 * (the memory footprint of all vectors)." That is about 8 GB for 1M×1024 float32. PQ and the disk-based HFresh index are mitigations.
- BM25 uses BlockMaxWAND. Filters use roaring bitmaps, and the range index is opt-in.
- Hybrid default is relativeScoreFusion since v1.24.
- Tokenization options: word, field, trigram, gse, kagome and others. I found no stemming option.
- C# client `Weaviate.Client` 1.2.0.

### 4.9 Typesense, Vespa, Elasticsearch, OpenSearch

- **Typesense:**
  - "Stores the entire search index in-memory"; keyword search needs "2X-3X" RAM relative to indexed text.
  - Hybrid uses rank fusion `0.7*K + 0.3*S`, adjustable via alpha.
  - Snowball stemming is selected by locale.
  - GPL-3.0.
- **Vespa:** "Minimum 4 GB RAM dedicated to Docker/Podman."
- **Elasticsearch:** default license is a triple AGPL-3.0 / SSPL / ELv2.
- **OpenSearch:** Apache-2.0, version 3.10.0.
- Both Elasticsearch and OpenSearch are mature for BM25 plus Russian and Danish analyzers, but run on the JVM. Exceeding 1–2 GB is likely at 1M×1024 (estimate).

### 4.10 In-process .NET options

- **USearch C#** (`Cloud.Unum.USearch` 2.26.6, ~70k downloads):
  - "It requires .NET 10, and the package carries the native library for Linux x64 and macOS Arm64."
  - Quantization options: BF16, F16, E5M2, E4M3, I8, U8, U1x8 and others.
  - Supports save/load, `ViewRam` (memory-mapped, read-only) and predicates.
  - The API looks recently redesigned (Executors, Arenas). Expect churn.
- **HNSW.Net** (`curiosity-ai/hnsw-sharp`, NuGet `HNSW` 26.11.7916):
  - Pure C# HNSW with graph (de)serialization.
  - The repo now also contains **Hyperspace**, which adds quantization (including TurboQuant), metadata filters and "BM25 hybrid search". But the `Hyperspace` NuGet package has only **109 downloads**, so it's immature.
- **FAISS .NET:** `FaissNet` 1.1.0 (2023-07) and `FaissSharp` 1.0.1 (2019). Both are stale and not recommended.
- **Lucene.NET:**
  - Still 4.8.0-beta00018 (2026-06-22); beta00017 was 2024-10.
  - `BM25Similarity.cs`, `Analysis/Ru/RussianAnalyzer.cs` and `Analysis/Da/DanishAnalyzer.cs` exist in `apache/lucenenet`.
  - It is a port of Lucene 4.8, which predates Lucene's HNSW vectors (Lucene 9). **No ANN support** (from my knowledge).
- **Microsoft.Extensions.VectorData:**
  - Abstractions 11.0.1.
  - Hybrid search through `IKeywordHybridSearchable<T>.HybridSearchAsync(text, keywords, top)` with `[VectorStoreData(IsFullTextIndexed = true)]` (Microsoft Learn).
  - Connectors implementing the hybrid interface in CommunityToolkit/AI: Qdrant, Weaviate, PgVector, SqlServer, Milvus, Chroma, AzureAISearch and CosmosDB (code search).
  - Each connector decides what "keyword" means. For Qdrant it's an unranked text filter (section 4.1).
- **Kernel Memory:** the README says "This is an archived research project. The code serves as a learning resource, not production software." The last NuGet release was 2025-05. **Avoid.**
- **sqlite-vec:** "pre-v1, so expect breaking changes!" (v0.1.9). The repo contains IVF code, but I didn't verify its ANN status. FTS5's `bm25()` gives BM25 in SQLite.

### 4.11 MEVD's Qdrant hybrid search isn't BM25

If Cortex calls the MEVD Qdrant connector's `HybridSearchAsync`, the keyword leg is filter-only: matching documents get no BM25 ranking before RRF fusion. To get BM25-ranked hybrid search, use `Qdrant.Client`'s `QueryAsync` with a `TextQuery` prefetch and an RRF fusion query (code in section 7).

---

## 5. Benchmarks

| Benchmark | Relevance to 100k–1M × 768–1024 with filters | Notes |
|---|---|---|
| VectorDBBench v2.0.0 (2026-09-10) | Has cases `Performance768D1M`, filtered `Performance768D1M1P`/`99P`, `Performance1024D1M`, `LabelFilterPerformanceCase`, plus streaming, full-text and multi-tenant cases | The leaderboard is published by **Zilliz (a vendor)**. 1M-dataset numbers are on large hardware: QdrantCloud-16c64g 1,242 QPS, p99 6.4 ms, recall 0.947; Milvus-16c64g-sq8 5,973 QPS, p99 2.4 ms, recall 0.919; ElasticCloud-8c60g 2,354 QPS. Not representative of a 1–2 GB single-user container, and no memory figures are shown. |
| Qdrant benchmarks (qdrant.tech/benchmarks) | dbpedia-openai-1M-1536 and gist-960-1M; a filtered-search benchmark exists | "Updated: January/June 2024" (stale). Server was 8 vCPU / 32 GiB. "Memory consumption benchmark – Coming soon". |
| ann-benchmarks | Has `dbpedia-openai-{n}k-angular` (1536-d) and gist-960 | Algorithm-level, in-memory, **no filters, no hybrid**. |

**Takeaway:** no independent benchmark covers your actual workload: ≤2 GB RAM, 1024-d, BM25 plus vector with filters, multilingual. At 10k–200k chunks every candidate answers in single-digit milliseconds. The deciding factors are features, RAM, erasure and ops, not QPS. **Run VectorDBBench's custom-dataset mode, or a small recall@k harness, on your own Qwen3 embeddings** before tuning quantization. Qdrant's docs warn: "Some embedding models don't quantize efficiently, so verify recall."

---

## 6. RAM estimates and how they were computed (1M × 1024)

| Component | Formula / source | Size |
|---|---|---|
| float32 originals | 1e6 × 1024 × 4 B (Qdrant formula) | 4.10 GB (keep **cold**/memory-mapped) |
| float16 / turbo16 | × 2 B | 2.05 GB |
| int8 SQ / turbo8 | × 1 B | 1.02 GB |
| turbo4 (TurboQuant 4-bit) | × 0.5 B | 0.51 GB |
| 1-bit BQ / RQ | × 0.125 B | 0.13 GB |
| HNSW graph (m=16) | 1e6 × 16 × 2 × 4 × 1.2 | 0.15 GB |
| Payload (~1.5 KB text/chunk) | ×1.5 on disk; ×4.5 RAM if cached | 2.25 GB disk; **keep cold** |
| BM25 index with scoring | Not documented numerically | Unknown; measure it (v1.18 added memory breakdown reporting) |

At **200k chunks**: float32 is 0.82 GB and int8 is 0.2 GB, so even all-in-RAM fits the budget.

Caveat (my inference): with Docker Desktop and cgroup v2, page cache from memory-mapped files counts toward a container's memory limit but can be reclaimed. Set `mem_limit` with headroom, and use Qdrant's strict-mode `max_resident_memory_percent` (v1.18).

---

## 7. Recommendation ranking for Cortex

**1. Qdrant v1.20.x container (recommended)**
- **Pros:**
  - Rust; one container.
  - True BM25 plus dense vectors plus filters in one Query API call, with RRF, weighted RRF or DBSF.
  - Snowball stemming for Russian and Danish.
  - Fine-grained memory tiers and TurboQuant keep 1M×1024 at about 1 GB.
  - Snapshots to local disk, S3 or Azure Blob.
  - Best .NET client of the Rust options (official, gRPC, ~3.5M downloads).
  - Fits the existing MEVD ecosystem if you need it.
- **Cons:**
  - BM25-on-index is about one month old (v1.20.0). Plan to fall back to sparse vectors with IDF / BM25 inference (available since 1.15.2) if you hit bugs.
  - One stemmer language per field, so use per-language fields.
  - No encryption at rest: use BitLocker/LUKS on the host or the Docker volume, and encrypt snapshots (e.g. restic or age).
  - Soft deletes: tune `deleted_threshold` and `vacuum_min_vector_number` low for erasure, and purge old snapshots.
  - Don't use MEVD `HybridSearchAsync` for BM25.
- **Migration and ops (my estimate):** 2–4 days to build ingest, schema and hybrid query; then about 15–30 min/week (version bumps, snapshot rotation).
- Suggested schema:
  - Named dense vector (1024, cosine, turbo4 or SQ pinned, originals cold).
  - Full-text fields `text_en`/`text_ru`/`text_da` with `scoring: true` and the matching stemmer, plus an unstemmed multilingual `text` field.
  - Keyword/int/datetime payload indexes on `kind`, `channel`, `ts`.
  - Erasure workflow: delete by filter, let the optimizer vacuum, rotate snapshots.

**2. LanceDB embedded inside your own small Rust service**
- **Pros:** lowest RAM and disk (IVF_RQ), BM25 with stemming, RRF, version history doubles as backup, Apache-2.0.
- **Cons:** you build and maintain the server, API and .NET client; erasure requires compaction plus cleanup (default 7-day retention); HNSW-type indexes have filtered-latency variance; no official .NET.
- **Cost (my estimate):** 1–2 weeks of build time, then more ops ownership.

**3. Postgres + pgvector (+ pg_search or built-in full-text search)**
- Good choice if Cortex already runs Postgres: one system for metadata, events and vectors, transactional deletes, `pg_dump` backups, and Npgsql/EF/MEVD PgVector.
- Not Rust. pg_search is AGPL, which is fine for personal use. RAM for 1M×1024 HNSW is about 2–3 GB unless you use halfvec or bit quantization. Not deeply verified here.

**4. In-process: Lucene.NET + USearch (or Hyperspace)**
- **Pros:** no container, no network hop, real BM25 with Russian and Danish analyzers.
- **Cons:** you hand-roll fusion, filtering and cross-store consistency; Lucene.NET is permanently beta; USearch C# API is new (and requires .NET 10, which matches your stack); Hyperspace is too immature.

**5. Meilisearch**
- Only worth it if typo-tolerant, user-facing search matters more than BM25 recall. No stemming, ranking isn't BM25, and erasure on LMDB is weak.

**Not recommended for this use case:**
- Weaviate: RAM-heavy, no stemming.
- Typesense: in-memory, GPL.
- Milvus: 8 GB minimum. Milvus Lite is Python-only.
- Chroma: hybrid search is Cloud-only.
- Turso: no vector index yet.
- Vespa, Elasticsearch, OpenSearch: heavy.
- Kernel Memory: archived.

---

## 8. Source table (all fetched or accessed 2026-11-21)

| Title | Org | Date | URL |
|---|---|---|---|
| Qdrant releases v1.15.0–v1.20.2 | Qdrant | 2025-07-18 … 2026-11-16 | https://github.com/qdrant/qdrant/releases |
| Hybrid and Multi-Stage Queries | Qdrant | live doc | https://qdrant.tech/documentation/search/hybrid-queries/ |
| Full-Text Search (BM25) | Qdrant | live doc (v1.20) | https://qdrant.tech/documentation/search/text-search/full-text-search/ |
| Indexing (tokenizers, stemmer, stopwords) | Qdrant | live doc | https://qdrant.tech/documentation/manage-data/indexing/ |
| Capacity Planning | Qdrant | live doc | https://qdrant.tech/documentation/capacity-planning/ |
| Quantization | Qdrant | live doc | https://qdrant.tech/documentation/manage-data/quantization.md |
| Memory tiers | Qdrant | live doc (v1.19+) | https://qdrant.tech/documentation/ops-configuration/memory-tiers.md |
| Optimizer (vacuum) | Qdrant | live doc | https://qdrant.tech/documentation/ops-optimization/optimizer.md |
| Snapshots | Qdrant | live doc | https://qdrant.tech/documentation/snapshots.md |
| Security | Qdrant | live doc | https://qdrant.tech/documentation/security.md |
| Qdrant Edge | Qdrant | live doc (beta) | https://qdrant.tech/documentation/edge.md |
| llms.txt index (Cloud Premium CMEK) | Qdrant | live | https://qdrant.tech/llms.txt |
| Qdrant benchmarks | Qdrant | Jan/Jun 2024 | https://qdrant.tech/benchmarks/ |
| QdrantCollection.cs (MEVD hybrid) | CommunityToolkit | commit f393fbcd, 2026-10-12 | https://github.com/CommunityToolkit/AI/blob/main/MEVD/src/Qdrant/QdrantCollection.cs |
| AI Community Toolkit README | CommunityToolkit/.NET Foundation | 2026 | https://github.com/CommunityToolkit/AI |
| MEVD overview and hybrid search | Microsoft Learn | live | https://learn.microsoft.com/en-us/dotnet/ai/vector-stores/hybrid-search |
| LanceDB docs 0.41.0 (SDK, FTS index, hybrid, vector index, filtering, delete, cleanup) | LanceDB | v0.41.0 (2026-10-29) | https://docs.lancedb.com/oss/v0-41-0/ |
| lancedb-csharp | lennylxx (community) | v2.5.0, 2026-07-05 | https://github.com/lennylxx/lancedb-csharp |
| tantivy stemmer.rs, bm25.rs | Quickwit | main, 2026-11 | https://github.com/quickwit-oss/tantivy |
| rust-stemmers README | CurrySoftware | — | https://github.com/CurrySoftware/rust-stemmers |
| Meilisearch LICENSE | Meili SAS | 2025 | https://github.com/meilisearch/meilisearch/blob/main/LICENSE |
| Meilisearch EE/CE, ranking, language, storage, Hannoy, embedders | Meilisearch | live | https://www.meilisearch.com/docs/llms.txt |
| Chroma Search API overview / full-text search | Chroma | live | https://docs.trychroma.com/cloud/search-api/overview.md |
| Turso README | Turso | 2026-11 | https://github.com/tursodatabase/turso |
| Milvus Lite / Docker prerequisites / stemmer filter | Zilliz/Milvus | live | https://milvus.io/docs/milvus_lite.md , https://milvus.io/docs/prerequisite-docker.md , https://milvus.io/docs/stemmer-filter.md |
| Weaviate resources / collections / hybrid / llms.txt | Weaviate | live | https://docs.weaviate.io/weaviate/concepts/resources |
| Typesense system requirements / vector search | Typesense | v30.x | https://typesense.org/docs/guide/system-requirements.html |
| Vespa local deployment | Vespa.ai | live | https://docs.vespa.ai/en/deploy-an-application-local.html |
| Elasticsearch LICENSE.txt | Elastic | main | https://github.com/elastic/elasticsearch/blob/main/LICENSE.txt |
| USearch C# README | Unum | v2.26.6 (2026-10-25) | https://github.com/unum-cloud/usearch/tree/main/csharp |
| hnsw-sharp README (Hyperspace) | Curiosity | 2026-11 | https://github.com/curiosity-ai/hnsw-sharp |
| Lucene.NET source and tags | Apache | beta00018, 2026-06-22 | https://github.com/apache/lucenenet |
| Kernel Memory README (archived notice) | Microsoft | 2026-06 | https://github.com/microsoft/kernel-memory |
| VectorDBBench README / leaderboard | Zilliz | v2.0.0, 2026-09-10 | https://github.com/zilliztech/VectorDBBench , https://zilliz.com/vdbbench-leaderboard |
| ann-benchmarks datasets.py | erikbern | 2026-07 | https://github.com/erikbern/ann-benchmarks |
| NuGet registration/search API (all package versions) | NuGet.org | 2026-11-21 | https://api.nuget.org/v3/ |

---

## 9. Uncertainties and gaps

1. **All RAM figures at 1M×1024 are estimates** from Qdrant's sizing formulas, Weaviate's 2× rule, Typesense's 2–3× rule and Milvus's stated minimum. No vendor publishes memory for this exact workload, and Qdrant's "memory consumption benchmark" is still "Coming soon". The size of Qdrant's BM25-scoring text index is undocumented; measure it with the v1.18 memory breakdown.
2. **Qdrant BM25-on-index is new (v1.20.0, 2026-10-22).** No independent quality or performance reports were found. I didn't verify whether one field can hold multiple stemmers, or whether a stemmer can be chosen per query.
3. **No engine documents a guaranteed physical erasure SLA.** Qdrant (vacuum thresholds plus snapshots/WAL), LanceDB (cleanup retention) and Meilisearch (LMDB free pages) all keep residual data for some time. Exact WAL retention in Qdrant was not checked.
4. **Encryption at rest:** verified absent from Qdrant's self-hosted security docs and present in Qdrant Cloud Premium. Not verified for LanceDB local, Chroma, Weaviate or Milvus. Turso's is labeled experimental.
5. **Not verified:**
   - LanceDB `language` values for Russian and Danish.
   - A Meilisearch dedicated "ollama" embedder source (only seen in release-note text).
   - sqlite-vec ANN status (IVF code exists in the repo).
   - pg_search stemmer language list.
   - Chroma local RAM.
   - Elasticsearch/OpenSearch minimum footprint.
6. **Benchmarks:** the VectorDBBench leaderboard is vendor-run (Zilliz) on 16c/64 GB-class hardware. Qdrant's own benchmarks are from 2024. Neither reflects a ≤2 GB single-user container, so run your own recall@k with Qwen3-0.6B embeddings before enabling aggressive quantization.
7. **Tool limitation:** the `github/get_me` and `get_file_contents` MCP tools weren't used. Data came from the `gh` API, raw GitHub, official docs (.md / llms.txt) and the NuGet API.
