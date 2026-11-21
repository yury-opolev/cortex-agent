# Qdrant 1.20.x as the retrieval engine for a single-user AI-assistant memory: technical study (November 2026)

**Version and scope.** Latest release is **v1.20.2**, released 2026-11-16 at commit `23065a0`. All source citations below are at tag `v1.20.2` and written as `qdrant/qdrant:<path>:<lines>`. Docs were read on 2026-11-21; they are living pages and their dates are the access date. Anything marked **[EST]** is my own estimate, not a measured or published number.

**Summary.**
- **BM25 is now built in.** Qdrant 1.20 scores BM25 directly on a full-text payload index. Corpus statistics (N, df, avgdl) are summed across all segments of a shard at query time, so the score does not depend on which segment holds a document. They are still per-shard, and they include deleted documents until the segment is rebuilt.
- **Language handling is weak for mixed text.** The `multilingual` tokenizer (charabia/vaporetto) splits most scripts correctly. But stemming is one Snowball language per index (19 languages, including Danish and Russian). Mixed EN/RU/DA text in one field cannot be stemmed per language.
- **Filtered search is solid.** Qdrant plans each segment separately: it estimates filter cardinality, then picks an exact scan over payload-index candidates, a filter-aware HNSW traversal, or ACORN. Very selective filters (0.1–1%) work well, especially if `full_scan_threshold` is raised.
- **RAM fit by scale (1–4 GB budget):**
  - **1M chunks:** fits comfortably with quantized vectors pinned and the originals on disk.
  - **10M chunks:** feasible but disk-bound and tight.
  - **50M chunks:** cannot keep even 1-bit quantized vectors (6.4 GB) plus the HNSW graph (~7.7 GB) in RAM. Interactive latency there depends on time-partitioning so that queries touch a small active slice.
- **Erasure is not immediate.** Deletes are soft: Gridstore payload blocks are only marked free, vectors are flagged, and full-text postings stay. Data is physically dropped only when a segment is rebuilt (vacuum runs at >20% deleted by default). Copies can also linger in the WAL (32 MB segments, one closed segment retained) and in any snapshot.

---

## 1. Architecture of search

### Storage layout
- **Hierarchy.** A collection has shards; each shard has segments. Each segment holds its own vector storage, payload storage, vector index (HNSW), payload indexes, and an ID tracker (external ID → internal offset and version). Segments are **appendable** (accept inserts) or **non-appendable** (read and delete only). At least one appendable segment always exists. Search deduplicates points that appear in more than one segment. (https://qdrant.tech/documentation/manage-data/storage/)
- **Memory tiers (since v1.19).** Every component takes a `memory` setting:
  - `pinned`: loaded onto the heap, never evicted.
  - `cached`: memory-mapped and pre-warmed into the page cache at startup.
  - `cold`: memory-mapped and read on demand.
  - `cached` and `cold` are evicted by the OS on the same terms; `cached` only gets warmed first.
  - `pinned` is rejected for dense vectors and payloads.
  - Defaults: dense vectors `cached`; HNSW `cached`; quantized vectors `pinned` if the originals are cached, `cold` if they are cold; sparse index `pinned`; payloads `cold`; payload indexes `pinned`; ID tracker `pinned` (made configurable in 1.20).
  - (https://qdrant.tech/documentation/ops-configuration/memory-tiers/)
- **Payload storage.** Since v1.13 payloads use **Gridstore**, a custom store that replaced RocksDB. It writes 128-byte blocks, keeps a usage bitmask, and tracks free gaps per region. A delete soft-deletes the pointer and marks the blocks free in the bitmask; nothing is compacted on delete. Values of indexed fields are also held by their payload index, so filtering does not touch payload storage. (https://qdrant.tech/articles/gridstore-key-value-storage/; https://qdrant.tech/documentation/manage-data/storage/#payload-storage)
- **Segment sizing defaults.**
  - Target segment count = `clamp(num_cpus/2, 2, 8)`. A 20-thread laptop therefore targets 8 segments. (`qdrant/qdrant:lib/shard/src/optimizers/config.rs:194-200`)
  - Default max segment size = `num_indexing_threads × 256,000 KB`, counted in vector bytes. (`qdrant/qdrant:lib/shard/src/optimizers/config.rs:17-20,224-231`)
  - Indexing threads auto-select between 8 and 16. (`qdrant/qdrant:lib/segment/src/types.rs:867-872`)
  - **[EST]** With 16 indexing threads a segment is about 4 GB of vectors: ~1M float32×1024 vectors, or ~4M if the originals are `turbo8`. So 10M float32 chunks means ≥10 segments and 50M means ≥50.
- **Optimizers.** Four optimizers rebuild segments: indexing, vacuum, merge, and config-mismatch. Segments stay readable during a rebuild through proxy and copy-on-write segments. "Every rebuild removes deleted points and applies the current collection configuration." (https://qdrant.tech/documentation/ops-optimization/optimizer/)

### Write path
1. The operation is appended to the per-shard WAL and given a sequence number. `SerdeWal::write` explicitly "does [not] guarantee durability". (`qdrant/qdrant:lib/shard/src/wal.rs:101-102`)
2. It goes onto the update queue. The default queue length was cut from 1M to 200 in v1.19 (#9448).
3. An update worker applies it to an appendable segment.
4. Every `flush_interval_sec` (default 5 s; `config/config.yaml:224-225`), the flush worker flushes the WAL, flushes segments, persists clock maps, and then acknowledges (truncates) the WAL up to the confirmed version. (`qdrant/qdrant:lib/collection/src/update_workers/flush_workers.rs:62-128`)
5. With `wait=true`, the client gets the response only after the operation is applied and visible. With `wait=false` (the REST default), the response is just "acknowledged". (https://qdrant.tech/documentation/manage-data/points/#awaiting-result)

### Query path (end to end)
1. **Collection layer.** Resolves the request and fans out to shards.
2. **Shard layer.** Plans the query tree of prefetches. A top-level `filter` is merged into every leaf prefetch ("Filters are propagated into the leaves"). (`qdrant/qdrant:lib/shard/src/query/planned_query.rs:345-365`)
3. **Statistics pre-pass for text queries.** For BM25 or IDF-modified sparse queries, each segment reports document frequencies and token totals, and these are summed per shard before any segment scores. (`qdrant/qdrant:lib/segment/src/data_types/query_context.rs:52-69,102-123,268-290`)
4. **Per segment, in parallel.**
   - The planner chooses plain search, filtered HNSW, or ACORN (§3).
   - Candidates are scored on quantized vectors if they exist.
   - Rescoring reads the originals for `limit × oversampling` candidates.
   - The segment returns its top-k.
5. **Merge and fuse.** Per-segment results are merged and deduplicated by point id and version. Fusion and formula scoring run at the top level; fusion is global only when it is the main query (§5).
6. **Payload fetch.** `with_payload` reads from Gridstore. On Linux, `io_uring: auto` makes reads from `cold` structures asynchronous. (https://qdrant.tech/documentation/ops-configuration/memory-tiers/#async-io; `config/config.yaml:173-181`)

---

## 2. Dense vector search internals

### HNSW parameters and implementation
| Parameter | Default | Mechanism / source |
|---|---|---|
| `m` | 16 | Level 0 uses `m0 = 2m` = 32 links. `m=0` disables the main graph; payload links can still exist. (`lib/segment/src/index/hnsw_index/config.rs:37-56`; `types.rs:849-854`) |
| `ef_construct` | 100 | `DEFAULT_HNSW_EF_CONSTRUCT` (`types.rs:1703`). Minimum 4. |
| `hnsw_ef` (query) | = `ef_construct` | "initialized to `ef_construct`" (`hnsw_index/config.rs:19`) |
| `full_scan_threshold` | 10,000 KB | Converted to a **vector count** per segment: `threshold_kb × 1024 / avg_vector_bytes` (`hnsw.rs:186-213`). For float32×1024 (4,096 B) that is **2,500 vectors**; for `turbo8` (1,024 B) it is 10,000. |
| `payload_m` | null (= m) | Extra links per indexed payload value (§3). |
| `inline_storage` | false | Stores quantized and original vectors inside the graph nodes. Since 1.20 the originals are stored only there, with no duplicate copy. Requires quantization; no multivectors. (`types.rs:887-892`; 1.20 blog) |
| `max_indexing_threads` | 0 (auto, 8–16) | `types.rs:867-872` |
| entry points | `max(1, N/full_scan_threshold × 10)` | `hnsw/build.rs:95-101` |

- **v1.20 graph changes.**
  - Upper layers use `floor()` level assignment, which gives 6.25% of points on level 1 at m=16 instead of 25% (#11166).
  - Payload-link building and graph healing got faster (#11009, #11083).
  - Cold graph search does half the reads (#10743, v1.20.1).
  - On-disk vectors are prefetched in batches with `madvise(WILLNEED)` (#10906).
  - (https://github.com/qdrant/qdrant/releases)
- **PiPNN graph builder.** v1.20.1 switched large segments to the PiPNN builder (milestone 56). v1.20.2 made it **opt-in again** because PiPNN ignores `ef_construct`; it only kicks in on segments of 30k+ points. (PR #11553, merged 2026-11-15: https://github.com/qdrant/qdrant/pull/11553)
- **GPU indexing.** Uses the Vulkan API (NVIDIA, AMD, Intel) through dedicated Docker images or a build with the `gpu` feature; enable with `gpu.indexing: true`. Published timings for 1M×1536 vectors: Nvidia L4 12.4 s, T4 19.1 s, 8 CPU cores 97.5 s, 4 cores 221.9 s. (https://qdrant.tech/documentation/ops-configuration/running-with-gpu/; https://qdrant.tech/blog/qdrant-1.13.x/, 2025-01-23)

### Quantization (a compressed copy kept next to the originals)
| Method | Bytes/dim | Compression | Rescore by default | Notes |
|---|---|---|---|---|
| TurboQuant `bits8` (v1.20) | 1 | 4× | No | Recommended replacement for scalar quantization at 4×. |
| TurboQuant `bits4` (default) | 0.5 | 8× | No | Recall similar to scalar quantization at twice the compression. |
| TurboQuant `bits2` / `bits1_5` / `bits1` | 0.25 / ~0.19 / 0.125 | 16× / ~21× / 32× | Yes | Better recall than binary quantization at the same size, but slower. |
| Scalar int8 | 1 | 4× | No | `quantile` tuning; AVX-VNNI/AVX512-VNNI kernels since 1.20 (#10975). |
| Binary 1 / 1.5 / 2-bit | 0.125–0.25 | 32× / ~21× / 16× | Yes | Asymmetric mode (binary stored vectors, scalar query) since 1.15. |
| Product quantization | up to 64× | — | No | 256 centroids per chunk; not SIMD-friendly. |

(https://qdrant.tech/documentation/manage-data/quantization/)

- **TurboQuant mechanics.** A fast random (Hadamard) rotation, then a single global quantization grid. Queries are kept at full precision (asymmetric).
- **`oversampling`.** The quantized pass pre-selects `limit × oversampling` candidates; rescoring then reads the originals for those. Example from the docs: 2.4 × 100 = 240. `ignore` and `rescore` can be set per query.
- **TurboQuant datatypes (a different thing).** `turbo16`, `turbo8` and `turbo4` replace the original vectors; no full-precision copy is kept. `turbo16` matched float32 within 0.001 recall@10 on 12 datasets. `turbo8` came close to float16 at half its size:

  | Dataset | turbo4 | turbo8 | float16 |
  |---|---|---|---|
  | DBpedia OpenAI 1536d | 0.920 | 0.965 | 0.963 |
  | arXiv 384d | 0.928 | 0.972 | 0.973 |

  (https://qdrant.tech/blog/qdrant-1.20.x/, 2026-10-22; https://qdrant.tech/documentation/manage-data/vectors/#datatypes)
- **Published recall trade-offs.**
  - 1.18 blog, HNSW m=16 / ef_construct=128. On the multilingual dataset gte-multilingual-ads-1M: float32 0.9298, SQ 0.9187, TQ4 0.9169. 1-bit: BQ1 0.676 vs TQ1 0.772. (https://qdrant.tech/blog/qdrant-1.18.x/, 2026-05-11)
  - Qdrant laptop test: 4.6M × 384d DBpedia-entity on v1.19.0, recall@10 against exact search:

    | Configuration | Recall@10 |
    |---|---|
    | float32 graph | 0.957 |
    | `bits4`, rescore off | 0.918 |
    | `bits4`, oversampling 4 | 0.993 |
    | `bits1`, rescore off | 0.605 |
    | `bits1`, oversampling 1 / 2 / 4 | 0.951 / 0.977 / 0.988 |

    nDCG@10 barely moved. (https://qdrant.tech/documentation/search-tuning/when-your-collection-outgrows-ram/)
- **Named vectors and multivectors.** Each named vector has its own HNSW, datatype and tier. Multivectors are supported (late interaction / MaxSim). The documented multi-stage pattern: a small Matryoshka (MRL) prefix or quantized vector as the first stage, rescored with the full vector; set `m=0` on rescoring-only vectors to save graph RAM. (https://qdrant.tech/documentation/search/hybrid-queries/#multi-stage-queries)
- **Relevance to our model.** Qwen3-Embedding allows "flexible vector definitions across all dimensions" (MRL), so a 256/512-d prefix vector plus 1024-d rescoring is an option. (https://huggingface.co/Qwen/Qwen3-Embedding-0.6B)

---

## 3. Filtered search internals

### Payload indexes
- **Types.** keyword, integer (with `lookup`/`range` options), float, bool, datetime, uuid, geo, text.
- **Keyword prefix match** since 1.19.
- **Default tier is `pinned`.** The index can be moved to `cached` or `cold`.
- **Special flags.**
  - `is_tenant` (keyword/uuid): co-locates a tenant's data on disk.
  - `is_principal` (integer/float/datetime): optimizes storage for, e.g., time filters.
  - `nested` (v1.20): per-element indexes for arrays of objects.
  - `enable_hnsw: false`: skips extra graph links for that field.
  - (https://qdrant.tech/documentation/manage-data/indexing/)
- **Strict mode** can reject filters on unindexed fields (`unindexed_filtering_retrieve`) and can require a tenant filter (`require_tenant_filter`). (`config/config.yaml:314-342`)

### Planner, per segment (`qdrant/qdrant:lib/segment/src/index/hnsw_index/hnsw/read_view/dispatch.rs:62-203`)
1. **No filter.** Plain search if `exact`, if HNSW is disabled, or if available vectors < `full_scan_threshold`; otherwise HNSW (lines 63-94).
2. **With a filter.** `estimate_cardinality(filter)` returns `{min, exp, max}`, which is then adjusted for deleted vectors (lines 101-112; `query_estimator.rs:26-76`).
   - `max < threshold` → plain search: iterate the payload-index candidates and score them exactly (quantized scoring plus rescoring) (lines 134-146).
   - `min > threshold` → filter-aware HNSW (lines 148-158).
   - Otherwise → **sampling.** Up to 1,000 random ids are checked against the filter, with an Agresti–Coull 95% interval (z=2), until the decision is confident (lines 160-200; `sample_estimation.rs:5-52`).
3. **Combining conditions.**
   - `must`: assumes independence, so `exp = total × ∏(exp_i/total)`, `max = min(max_i)` (`query_estimator.rs:199-232`).
   - `should`: complement rule `1 − ∏(1−p_i)` (lines 123-166).
   - Unindexed conditions give no primary clause, so the estimate falls back to the whole segment.
4. **ACORN.** Used only if the query enables it **and** estimated selectivity `exp/available ≤ max_selectivity` (default **0.4**) (`dispatch.rs:262-294`; `types.rs:654`).
   - ACORN-1 explores 2-hop neighbours when 1-hop neighbours are filtered out; docs say it is "approximately 2-10x" slower.
   - 1.16 benchmark (multiple filters): ef=64 + ACORN gave 97.2% accuracy at 13.86 ms, against 53.3% at 1.25 ms without it (ef=512 without ACORN: 71.1%, 3.89 ms). (https://qdrant.tech/blog/qdrant-1.16.x/, 2025-11-19)
   - Since 1.20 ACORN also works on `m=0` graphs through the payload links (#10907).

### Extra links per payload value (`payload_m`)
- During the build, each indexed field (with HNSW enabled) contributes **payload blocks**: one per value or range whose cardinality is ≥ `full_scan_threshold`. Each block gets an additional subgraph with `payload_m` links (`hnsw/build.rs:94,129,189-225`; `build/payload_blocks.rs:122-135`).
- Tenant fields' blocks are always built. Later fields skip blocks that the graph built so far already connects well (`build.rs:207-212`).
- Links exist **per single-field value, not per combination**. Combinations such as `channel ∧ week` can still fragment the graph; that is the documented reason ACORN exists.
- Payload indexes must exist **before** ingestion, or the HNSW must be rebuilt (bump `ef_construct` by 1). (https://qdrant.tech/documentation/manage-data/indexing/#rebuild-the-hnsw-index)

### Behaviour at the extremes
- **Very selective filters.** The exact-scan path costs roughly one quantized score per candidate, and recall is exact, apart from quantization error that rescoring fixes.
- **Very broad filters.** Normal HNSW with an in-traversal filter check, with little overhead.
- **The middle band.** The weak spot, and where `payload_m` links and ACORN apply.
- **Applied to "one channel + one week" (0.1–1%) [EST].**
  - 10M chunks in ~10 segments → 1k–10k matches per segment against a threshold of 2,500 (float32). Some segments take the exact path, others the filtered graph.
  - Practical tuning: raise `hnsw_config.full_scan_threshold` (e.g., 40,000–100,000 KB → 10k–25k vectors per segment) so these queries always take the exact path. Scoring ≤25k vectors at 128–512 B each is sub-millisecond to a few ms of CPU while the quantized copy is pinned.
  - Make `channel` a keyword index (optionally `is_tenant`) and `timestamp` a datetime index with `is_principal`.

### Tenancy and partitioning
- **Partition by payload** with `is_tenant`.
- **User-defined shard keys**, which can be used for time-based sharding (e.g., one shard per month).
- **Tiered multitenancy**: a tenant can be promoted to a dedicated shard (1.16).
- **Per-tenant IDF corpus** for BM25 and sparse queries (`params.idf.corpus`, a filter) (1.19).
- (https://qdrant.tech/documentation/manage-data/multitenancy/; https://qdrant.tech/documentation/scaling/distributed_deployment/#time-based-sharding)

---

## 4. Keyword / BM25 internals (v1.20)

### Index structure
- A `text` payload index with `scoring: true` (shorthand for `{"type":"bm25"}`). `k` (default 1.2) and `b` (default 0.75) are set **per query**, so changing them needs no rebuild. (https://qdrant.tech/documentation/search/text-search/full-text-search/)
- **Mutable index** (appendable segments): stores each document as ordered token ids.
- **Immutable / on-disk index**: `postings.dat`, `vocab.dat`, `point_to_tokens_count.dat`, `point_to_doc_len.dat`, `deleted_mask.bin`, plus `term_frequencies.dat` when scoring is on. All are written once at build time. Runtime deletions live only in an in-memory bitvec rebuilt from the ID tracker. (`full_text_index/inverted_index/on_disk_inverted_index/mod.rs:75-121`)
- **Term frequencies** are bit-packed in BitPacker4x blocks aligned with posting chunks and stored as tf−1, so blocks of all-ones take zero bytes. (`inverted_index/term_frequencies.rs:1-24`)
- **Document lengths** are a 16-bit minifloat: exact up to 8,192 tokens, saturating at 2²⁷. (`inverted_index/doc_len.rs:1-15`)

### Scoring
- **Algorithm.** Document-at-a-time with **MaxScore** pruning; a term's upper bound is `idf·(k1+1)`. A dense term-at-a-time pass is used when postings per document ≥ 0.5. (`bm25/top_k.rs:33-39,53-61`)
- **Per-term score.** `idf·tf·(k1+1)/(tf + k1·(1−b+b·len/avgdl))`. Repeated query terms count once. (`bm25/mod.rs:276-293,234-236`)
- **IDF.** `ln((N−df+0.5)/(df+0.5)+1)`, clamped at ≥0. (`query_context.rs:95-100,491-496`)
- **Filters.** A filter is checked per block of candidates ("accept"). It does not change how posting lists are walked. **[Inference from code]** A very selective filter does not reduce posting traversal for BM25, unlike the dense planner. (`bm25/mod.rs:46-146`; `full_text_index_read.rs:141-151`)

### Statistics: scope and consistency
- **Scope.** "IDF and the average document length are gathered across the shard before any segment scores, so the same document scores the same wherever it lives." (`bm25/mod.rs:9-11`) They are **per shard**: in a multi-shard collection, each shard uses its own statistics. (docs, "Corpus Statistics")
- **Deletes skew the statistics.** An immutable index "counts its build, deleted documents included, so a deletion never changes its statistics." (`inverted_index/mod.rs:402-409`) Because `df` can then exceed `N`, IDF is clamped (`query_context.rs:491-494`). Docs: scores "can shift slightly until optimization. Deleted points never appear in the results."
- **Per-tenant or partial corpus.** `params.idf.corpus` takes a filter that narrows the population for N, df and avgdl. If the corpus matches nothing, every term gets a constant weight and length normalization is turned off. It never reaches across shards. (https://qdrant.tech/documentation/manage-data/multitenancy/#per-tenant-scoring-statistics)

### Tokenizers and language processing
- **Tokenizers.** `word` (default), `whitespace`, `prefix` (indexes every prefix), `multilingual`, and `code` (v1.20; splits camelCase and snake_case). (https://qdrant.tech/documentation/manage-data/indexing/#tokenizers)
- **`multilingual`.**
  - Latin script goes straight to charabia's segmenter and normalizer.
  - Text detected as Japanese goes to vaporetto.
  - Everything else goes to charabia.
  - Tokens with no alphabetic characters are dropped.
  - The language-detection allow-list covers Eng, Rus, Por, Ita, Deu, Ara, Dan, Swe, Fin, Tur, Nld, Hun, Ell, Tam, Ron, Cmn, Jpn.
  - (`full_text_index/tokenizers/multilingual.rs:21-58,83-103`)
- **Snowball stemmer: one language per index.** 19 languages: Arabic, Armenian, Danish, Dutch, English, Finnish, French, German, Greek, Hungarian, Italian, Norwegian, Portuguese, Romanian, Russian, Spanish, Swedish, Tamil, Turkish. (`lib/segment/src/data_types/index.rs:495`, enum `SnowballLanguage`)
- **Stopwords: a list of languages plus custom words.** 30 languages, including Danish, Russian, Chinese, Japanese, Kazakh, Hebrew and Bengali. (`data_types/index.rs:652`)
- **Other options.** `lowercase` (default on), `ascii_folding` (since 1.16), `min_token_len`/`max_token_len`, `phrase_matching` (stores positions; used by the phrase filter condition, not by ranking).

**What one stemmer per field means for EN/RU/DA text.**
- **[Inference]** A Russian Snowball stemmer leaves Latin-script words mostly untouched, and an English stemmer leaves Cyrillic words mostly untouched. So a single EN or RU stemmer on mixed EN+RU text is partly workable. EN and DA share the Latin script and their stemmers conflict.
- Clean options:
  - Separate text fields per language (e.g., `text_en`, `text_ru`, `text_da`, filled from client-side language detection, each with its own stemmer and stopwords), run as separate BM25 prefetches and fused.
  - Or no stemming at all (`multilingual` + lowercase + ascii_folding) and let the dense model carry the morphology.
- No BM25F or multi-field BM25 natively. (https://qdrant.tech/documentation/search/text-search/sparse-vector-search/)

### Alternatives
- **Sparse vectors with `modifier: idf`.** Same IDF formula, per shard; exact inverted index; dot product only. (https://qdrant.tech/documentation/manage-data/indexing/#idf-modifier)
- **Server-side `qdrant/bm25` sparse inference.** It needs a fixed `avg_len` (default 256) and defaults to English stemming and stopwords. Qdrant now recommends full-text BM25 instead. (https://qdrant.tech/documentation/inference/inference-bm25/)
- **SPLADE++ and miniCOIL** via FastEmbed. **[Not verified]** BGE-M3 sparse would need client-side generation, then storage as sparse vectors with IDF off.
- **Storage trade-off.** Full-text BM25 requires keeping the raw text in the payload; the index is rebuilt from the payload during optimization. Sparse vectors do not need the text.

---

## 5. Hybrid execution

- **Query API.** Prefetches nest arbitrarily (dense, sparse, text/BM25, quantized-first then full vector, multivector MaxSim). Each query runs on the results of its prefetches. The top-level filter is propagated to the leaf prefetches (code cited in §1).
- **RRF.** `score = Σ 1/(k + (r_d+1)/w_r − 1)` with zero-based rank and `k` = 2 by default (configurable since 1.16). Per-prefetch weights `w_r` are supported since 1.17.
- **DBSF.** Normalizes each retriever's scores as `(s − (μ−3σ))/(6σ)` over that query's returned set, then sums. No clipping; outputs 0.5 when σ=0.
- **Formula queries (1.14+).** Combine `$score`, payload fields and decay functions (exp/gauss/lin) — e.g., a recency boost on a datetime field. Note RRF scores are small, so the decay term needs a calibrated weight.
- **Ordering.** `order_by` as the main query sorts only the prefetched candidates and needs a range-capable index. A formula on the field gives the same ordering without an index.
- **Distributed caveat.** Fusion is global only when it is the **main** query; inside a prefetch it is computed per shard. A formula over fused results therefore needs a single shard. (https://qdrant.tech/documentation/search/hybrid-queries/)
- **Reranking hooks.**
  - Self-hosted core: no server-side cross-encoder. Options are multi-stage rescoring with other vectors or multivectors, formula queries, MMR, and relevance-feedback queries (1.17).
  - Server-side embedding: only `qdrant/bm25` self-hosted; Cloud Inference on managed Qdrant. (https://qdrant.tech/documentation/inference.md)
- **Pagination.** `offset`, or the stable-ordering patterns in the docs (exclude seen IDs). Ordering by datetime: `order_by` on a datetime or integer index (scroll or main query).

---

## 6. Performance at scale (1024-d cosine)

### Sizing formulas (official)
From https://qdrant.tech/documentation/capacity-planning/:
- `dense = N × d × bytes_per_dim`, where bytes_per_dim is 4 (f32), 2 (f16/turbo16), 1 (uint8/turbo8) or 0.5 (turbo4).
- `quantized = N × d × quant_bytes`.
- `hnsw = N × m × 2 × 4 B × 1.2`, which is 153.6 B per point at m=16.
- `id_tracker = N × 52 B`.
- `payload_disk = N × avg_payload × 1.5`, and ×3 more if `cached`.
- `payload_index ≈ 2 × indexed field bytes`.
- Add ~20% headroom.

### Footprints for 1024-d
Payload and text-index rows are **[EST]**: they assume ~700-word chunks, 4–12 KB of raw JSON (Cyrillic is about 2× the bytes of English), and ~350 distinct terms per chunk at ~1.2–2 B per bit-packed posting.

| Component | per point | 1M | 10M | 50M |
|---|---|---|---|---|
| Originals float32 | 4,096 B | 4.1 GB | 41 GB | 205 GB |
| Originals turbo8 / turbo4 | 1,024 / 512 B | 1.0 / 0.5 GB | 10.2 / 5.1 GB | 51 / 25.6 GB |
| Quantized SQ or `bits8` | 1,024 B | 1.0 GB | 10.2 GB | 51 GB |
| Quantized `bits4` | 512 B | 0.51 GB | 5.1 GB | 25.6 GB |
| Quantized `bits2` / `bits1` | 256 / 128 B | 0.26 / 0.13 GB | 2.6 / 1.3 GB | 12.8 / 6.4 GB |
| HNSW m=16 (m=8) | 154 (77) B | 0.15 GB | 1.5 GB | 7.7 (3.8) GB |
| ID tracker | 52 B | 0.05 GB | 0.52 GB | 2.6 GB |
| Payload on disk [EST] | 6–18 KB | 6–18 GB | 60–180 GB | 300–900 GB |
| BM25 text index [EST] | 0.4–0.8 KB | 0.4–0.8 GB | 4–8 GB | 20–40 GB |
| Filter payload indexes [EST] | 0.1–0.2 KB | 0.1–0.2 GB | 1–2 GB | 5–10 GB |

### Configurations that fit 1–4 GB of RAM [EST]
- **1M.**
  - Originals float32 `cold` (4.1 GB on NVMe); `bits4` or `bits8` `pinned`; HNSW `cached`; payload `cold`; payload indexes and text index `pinned`.
  - Resident set ≈ **1.3–2.2 GB**. ✅
  - Simpler variant: `turbo8` originals `cached` (1 GB) with no extra quantization.
- **10M.**
  - `bits1` or `bits2` pinned (1.3–2.6 GB); HNSW cached (1.5 GB, evictable); ID tracker 0.5 GB; text index `cold`; filter indexes pinned or cached.
  - Pinned plus hot set ≈ 3.3–5 GB. Over 4 GB, it relies on the page cache, so latency becomes disk-bound. ⚠️
  - Alternative: HNSW `cold` with `inline_storage`. Its disk cost is about 3–6× the float32 vectors (docs), roughly 120–250 GB.
- **50M.**
  - `bits1` alone is 6.4 GB and HNSW is 7.7 GB, so the 1–4 GB budget forces an all-cold setup.
  - Viable only if queries are confined to a small active slice: time-based shard keys or `is_principal` locality. The docs' "subgroup-oriented" sizing budgets RAM for *active* points only. ❌ for global search, ⚠️ for time-scoped search.

### Expected latency for single-user queries on 20 threads + NVMe
All rows are **[EST]**; they extrapolate from the anchors listed after the table.

| Scale / config | Unfiltered dense | Selective filter (0.1–1%) | BM25 | Hybrid (dense+BM25+RRF+payload) |
|---|---|---|---|---|
| 1M, warm | p50 2–5 ms, p95 5–10 ms | 1–5 ms (exact path) | 2–15 ms | 5–25 ms |
| 10M, 4 GB cap | p50 10–50 ms, p95 50–150 ms | 5–30 ms | 20–200 ms (cold index) | 30–250 ms |
| 50M, 4 GB cap, global | p50 50–300 ms, p95 0.3–1 s+ | 10–100 ms | 0.1–1 s | 0.2–1.5 s |
| 50M, time-sharded (only recent shard queried) | like the 1–10M rows | like the 1–10M rows | per-shard IDF | like the 1–10M rows |

**Benchmark anchors and their caveats.**
- **Qdrant's own benchmark** (raw data 2024-06-15, Qdrant version of that time; server 8 vCPU / 32 GB Azure D8s v3; vendor-run).
  - dbpedia-openai 1M×1536 with SQ, m=64, ef=512, precision 0.9915: 1 client → 319 RPS, mean 2.56 ms, p95 3.13 ms; 100 clients → 1,238 RPS, p95 4.96 ms.
  - Upload 211 s; upload plus indexing 1,466 s.
  - (https://qdrant.tech/benchmarks/; https://qdrant.tech/benchmarks/results-1-100-thread-2024-06-15.json)
- **Tiger Data / Timescale** (a competitor; 2025-04-29; Qdrant 1.13.4 with binary quantization; 50M×**768** Cohere vectors; r6id.4xlarge, 16 vCPU, 128 GB RAM, local NVMe; no filters; 2 shards × 2 segments; m=32, ef_construct=64).
  - 99% recall (hnsw_ef=768): p50 30.75 / p95 36.73 / p99 38.71 ms at **41.5 QPS**; pgvectorscale reached 471.6 QPS.
  - 90% recall: p50 4.74 / p95 5.50 ms.
  - The authors admit they struggled to tune Qdrant. (https://www.tigerdata.com/blog/pgvector-vs-qdrant)
- **Qdrant laptop test** (v1.19.0, 4.6M×384, Docker). With a 12 GiB limit, p50 is 3.8–4.1 ms. With a **4 GiB cap and rescoring**, p50 is **43 ms**, because rescoring re-reads 2.98 GB instead of 0.30 GB. (https://qdrant.tech/documentation/search-tuning/when-your-collection-outgrows-ram/)
- **Inline storage** (1.16 blog; 1M × 512d LAION, 2-bit quantization, float16, 430 MiB RAM limit): 211 QPS at 86.9% accuracy, against 20 QPS without inline storage.
- **VectorDBBench** (Zilliz, a competitor; leaderboard dated 2026-03-31; Qdrant Cloud 16c64g).
  - p99 6.4 ms, 1,242 QPS, recall 0.947.
  - Cohere-10M under streaming ingestion: 446.9 QPS static; 393.8 QPS at 500 rows/s ingestion; 347.6 QPS at 1,000 rows/s.
  - (https://zilliz.com/vdbbench-leaderboard)
- **No published BM25 latency numbers.** Only code comments mention BEIR/Quora tuning (`bm25/top_k.rs:33-39`).

### Build time and ingest [EST]
- **Index build.**
  - Anchor: 1M×1536 took 97.5 s on 8 cores. Extrapolated to 1024-d on ~16 threads: ~1–2 min per 1M vectors.
  - 10M: ~15–40 min. 50M: ~2–5 h, including merges, quantization, payload-link and text-index builds.
  - GPU indexing gives roughly 5–8× on cheap GPUs (1.13 table).
- **Continuous small inserts.** Go to the WAL and the appendable segment. Once an appendable segment exceeds `indexing_threshold` (10,000 KB ≈ 2,500 float32×1024 vectors), it is indexed in the background at low thread priority (1.20 #10977).
- **Bulk re-embeds.**
  - Write into a new named vector or a new collection.
  - Set `indexing_threshold: 0` or `m: 0` during the load, then re-enable.
  - Consider `prevent_unoptimized` so queries never scan huge unindexed segments. Use `wait=false` with it.
  - (https://qdrant.tech/documentation/ops-optimization/optimizer/)

### Updates, deletes and cold start
- **Deletes are tombstones.** The HNSW graph keeps the nodes, filter estimates count tombstones (`query_estimator.rs:234-239`), and BM25 statistics include deleted documents.
- **Vacuum** triggers at >20% deleted and ≥1,000 vectors per segment.
- **Config changes rebuild everything.** Changing HNSW, quantization or tier settings sends **every** segment through the config-mismatch optimizer: a long background job.
- **Cold start.** `cached` pre-reads whole files into the page cache at startup; `cold` faults pages in lazily. `low_memory_mode: no_resident | no_populate` avoids startup out-of-memory crash loops. (https://qdrant.tech/documentation/ops-configuration/administration/#low-memory-mode)

---

## 7. Scaling path

- **Sharding.**
  - Shard count defaults to the number of nodes at creation. Docs suggest 12 shards for flexibility.
  - **Resharding is Qdrant Cloud-only.** Self-hosted collections must be recreated to change shard count.
  - Replication (`replication_factor`, `write_consistency_factor`); shard transfer methods `stream_records`, `snapshot`, `wal_delta`.
  - (https://qdrant.tech/documentation/capacity-planning/; https://qdrant.tech/documentation/scaling/distributed_deployment/)
- **Distributed query costs.**
  - Fan-out to one replica per shard; partial fan-out and delayed fan-out (1.17).
  - BM25 and IDF statistics are per shard, and fusion is global only as the main query.
  - User-defined shard keys allow time-based shards. Old shard keys can be dropped, which removes whole shards; this suits retention and erasure.
- **Single-node ceiling.** The docs' rule of thumb is about 100M vectors per node. Our constraint is RAM (1–4 GB), not that ceiling.
- **Staying single-node.** Use tiers (cold originals, pinned quantized copy, `inline_storage` for a cold graph, `io_uring` on Linux) plus partitioning by time.
- **Qdrant Edge (beta).** An embedded, in-process `EdgeShard` library for Rust and Python with on-device BM25 and a WAL-less mode (1.20). It is a plausible fit for a single-user assistant. (https://qdrant.tech/documentation/edge.md)

---

## 8. Consistency, durability and erasure

### Write acknowledgement and consistency
- `wait=false` returns after the WAL append and enqueue. `wait=true` returns after the operation is applied and visible.
- Cluster settings: `write_consistency_factor`, read `consistency` (`all`/`majority`/`quorum`/N), write `ordering`, and the `X-Qdrant-Route-Affinity` header (1.19). (https://qdrant.tech/documentation/scaling/consistency-guarantees/)

### Durability
- The docs say a change in the WAL "will not be lost even if a power loss occurs". (https://qdrant.tech/documentation/manage-data/storage/#versioning)
- **[Inference from code]** WAL writes are not fsynced per operation. The flush worker flushes the WAL roughly every `flush_interval_sec` (5 s). Writes acknowledged in that window survive a process crash but may not survive a sudden power loss or OS crash. (`lib/shard/src/wal.rs:101-102,254-261`; `flush_workers.rs:62-87`)
- **Windows.** Bind-mounting Windows folders into Docker on WSL is a documented "incompatible file system" that can zero vectors after restart; use Docker volumes. A native Windows binary is shipped (`qdrant-x86_64-pc-windows-msvc.zip`). `io_uring` is Linux-only. (https://qdrant.tech/documentation/common-errors.md)

### Where deleted data remains on disk
1. **Vector storage and HNSW nodes.** Soft-deleted in segments; removed only when the segment is rebuilt (any optimizer: vacuum, merge, indexing, config-mismatch).
2. **Gridstore payload.** Blocks are only marked free; bytes stay until overwritten or the segment is rebuilt.
3. **Immutable full-text index.** Postings, vocabulary, document lengths and term frequencies of deleted documents stay until rebuild. Runtime deletions are not even persisted in the index (`on_disk_inverted_index/mod.rs:84-88`).
4. **WAL.** Upserts carrying payload and vector live until acknowledged and their 32 MB WAL segment is truncated; `wal_retain_closed: 1` (`config/config.yaml:122-131`). **[EST]** At assistant-level write rates (~10 KB per record), the old record can persist for days to weeks.
5. **Snapshots.** A shard snapshot archives all segments (after a forced flush) and optionally the WAL (`local_shard/snapshot.rs:50-57,229-237,349-356`). Data stays in snapshots until they are deleted, and snapshot retention is manual.
6. **Old segment directories.** Deleted as files after a rebuild. **[Inference]** There is no secure overwrite, so SSD data remanence applies.

### Erasure procedure [EST]
- After a deletion batch, temporarily `PATCH optimizers_config` with very low `deleted_threshold` and `vacuum_min_vector_number` to force vacuum, and watch `/collections/{name}/optimizations`.
- Small appendable segments below `vacuum_min_vector_number` will not vacuum by themselves.
- Delete or rotate snapshots.
- Rely on WAL truncation, or keep low WAL limits.
- Partitioning by time (shard keys) allows whole-shard deletion.
- Full-disk encryption covers the remanence risk.

---

## 9. Operations

- **Main tuning knobs.**
  - Per-component `memory` tier; quantization (`bits`, `rescore`, `oversampling`); datatypes.
  - HNSW `m`, `ef_construct`, `hnsw_ef`, `full_scan_threshold`, `payload_m`, `inline_storage`.
  - Optimizer `indexing_threshold`, `max_segment_size`, `default_segment_number`, `deleted_threshold`, `prevent_unoptimized`.
  - `optimizer_cpu_budget`, `max_search_threads`, `io_uring: auto`.
- **Monitoring.**
  - `/metrics` (Prometheus): `collection_points`, `collection_running_optimizations`, `collection_indexed_only_excluded_points`, `memory_resident_bytes`, `process_major_page_faults_total`, REST/gRPC latency histograms.
  - `/telemetry`, which includes ACORN statistics (1.19.2) and per-segment search timers by plan type.
  - The optimizations API, the slow-request log, and memory-usage reporting (1.18).
  - (https://qdrant.tech/documentation/ops-monitoring/monitoring/)
- **Resource limits.**
  - Strict mode limits: query limit, timeout, `hnsw_ef`, oversampling, unindexed filtering, batch sizes, collection size, points count, rate limits.
  - `max_resident_memory_percent` in strict mode is **deprecated (1.19) in favour of `storage.quotas`**, which has memory and disk percentages plus a release margin. Reads are never blocked by quotas. (https://qdrant.tech/documentation/ops-configuration/quotas/)
- **Upgrades.** Go through the latest patch of every intermediate minor version; a single node cannot skip versions. Keep the SDK within one minor version. 1.20.2 can read configs written by 1.21 (downgrade compatibility). (https://qdrant.tech/documentation/upgrades.md)
- **Backup.** Collection snapshots or full-storage snapshots (single node only), stored locally or on S3/GCS/Azure. Recovery from a snapshot needs about 2× disk.
- **Common failure modes.**
  - Out-of-memory crash loops at startup (fix: low-memory mode).
  - "Too many open files" (OS error 24).
  - Non-POSIX file systems.
  - Large unindexed segments slowing search after a bulk load.
  - Update-queue backpressure.
  - Long rebuilds after config changes.
  - Graph fragmentation under combined filters (fix: ACORN or a higher full-scan threshold).
  - BM25 scores shifting after deletes.
  - Recent correctness fixes in the changelog: Gridstore torn writes (1.19.2), `must_not` ignored (1.20.0), Greek stemmer panic (1.20.1). **Pin to the latest patch.**

---

## 10. Strengths, weaknesses and verdict

**Strengths**
- Mature filtered ANN: a cardinality-based planner, per-value payload links, and ACORN.
- Very rich quantization (TurboQuant 1–8 bit, binary, scalar, PQ) and lossy original datatypes, with measured recall data.
- Native multi-stage and hybrid queries (RRF, weighted RRF, DBSF, formula) in one request.
- BM25 with correct shard-wide avgdl and per-query `k`/`b`.
- Fine-grained memory tiers and NVMe-oriented features (inline storage, io_uring).

**Weaknesses**
- One Snowball stemmer per field, with weak handling of mixed-language text.
- BM25 traversal is not reduced by selective filters; statistics are per shard and include deleted documents until rebuild; no BM25F.
- Erasure is lazy: tombstones, Gridstore free blocks, WAL and snapshots all keep data until rebuild or rotation.
- WAL fsync is periodic (inferred).
- No self-hosted resharding; no in-engine cross-encoder.
- The 50M scale far exceeds a 1–4 GB RAM budget.

**Verdict by scale band**
- **1M — strong fit.** Use `bits4`/`bits8` pinned with float32 or `turbo8` originals, a pinned BM25 index, and raise `full_scan_threshold`. Expect hybrid queries in 5–25 ms. [EST]
- **10M — workable with care.** Use `bits1`/`bits2` pinned with rescoring and `cold` originals, a `cold` text index, and `io_uring` on Linux. Plan for p95 of 100–250 ms on hybrid queries under a 4 GB cap, or raise the budget to 6–10 GB for consistently <50 ms. [EST]
- **50M — poor fit for a 1–4 GB budget unless queries are time-scoped.** Use per-period shard keys or partition collections so that typical queries touch ≤5M points. Otherwise expect sub-second rather than tens-of-ms latency. [EST]

---

## Sources
| Title | Org | Date | URL |
|---|---|---|---|
| Releases v1.19.0–v1.20.2 changelogs | Qdrant | 2026-08-05 → 2026-11-16 | https://github.com/qdrant/qdrant/releases |
| Source tree at tag v1.20.2 (files cited inline) | Qdrant | 2026-11-16 | https://github.com/qdrant/qdrant/tree/v1.20.2 |
| PR #11553 "Disable PiPNN graph building by default" | Qdrant | 2026-11-15 | https://github.com/qdrant/qdrant/pull/11553 |
| Qdrant 1.20 – Full-Text Search Improvements | Qdrant | 2026-10-22 | https://qdrant.tech/blog/qdrant-1.20.x/ |
| Qdrant 1.19 – TurboQuant Datatype & Memory Tiers | Qdrant | 2026-08-05 | https://qdrant.tech/blog/qdrant-1.19.x/ |
| Qdrant 1.18 – TurboQuant | Qdrant | 2026-05-11 | https://qdrant.tech/blog/qdrant-1.18.x/ |
| TurboQuant in Qdrant | Qdrant | 2026-05-13 | https://qdrant.tech/articles/turboquant-quantization/ |
| Qdrant 1.17 – Relevance Feedback & Latency | Qdrant | 2026-02-20 | https://qdrant.tech/blog/qdrant-1.17.x/ |
| Qdrant 1.16 – Tiered Multitenancy & Disk-Efficient Search | Qdrant | 2025-11-19 | https://qdrant.tech/blog/qdrant-1.16.x/ |
| Qdrant 1.13 – GPU Indexing, Strict Mode | Qdrant | 2025-01-23 | https://qdrant.tech/blog/qdrant-1.13.x/ |
| Docs: Full-Text Search (BM25) | Qdrant | accessed 2026-11-21 | https://qdrant.tech/documentation/search/text-search/full-text-search/ |
| Docs: Indexing | Qdrant | accessed 2026-11-21 | https://qdrant.tech/documentation/manage-data/indexing/ |
| Docs: Memory Tiers | Qdrant | accessed 2026-11-21 | https://qdrant.tech/documentation/ops-configuration/memory-tiers/ |
| Docs: Quantization | Qdrant | accessed 2026-11-21 | https://qdrant.tech/documentation/manage-data/quantization/ |
| Docs: Vectors / Datatypes | Qdrant | accessed 2026-11-21 | https://qdrant.tech/documentation/manage-data/vectors/ |
| Docs: Capacity Planning | Qdrant | accessed 2026-11-21 | https://qdrant.tech/documentation/capacity-planning/ |
| Docs: Optimizer | Qdrant | accessed 2026-11-21 | https://qdrant.tech/documentation/ops-optimization/optimizer/ |
| Docs: Storage | Qdrant | accessed 2026-11-21 | https://qdrant.tech/documentation/manage-data/storage/ |
| Docs: Search (Query Planning, ACORN) | Qdrant | accessed 2026-11-21 | https://qdrant.tech/documentation/search/search/ |
| Docs: Hybrid and Multi-Stage Queries | Qdrant | accessed 2026-11-21 | https://qdrant.tech/documentation/search/hybrid-queries/ |
| Docs: Multitenancy (per-tenant IDF) | Qdrant | accessed 2026-11-21 | https://qdrant.tech/documentation/manage-data/multitenancy/ |
| Docs: Sparse Vector Search / BM25 inference | Qdrant | accessed 2026-11-21 | https://qdrant.tech/documentation/search/text-search/sparse-vector-search/ |
| Docs: When Your Collection Outgrows RAM | Qdrant | accessed 2026-11-21 | https://qdrant.tech/documentation/search-tuning/when-your-collection-outgrows-ram/ |
| Docs: Consistency, Distributed, Snapshots, Quotas, Administration, Monitoring, Upgrades, Common Errors, GPU, Edge | Qdrant | accessed 2026-11-21 | https://qdrant.tech/documentation/ (respective pages) |
| Gridstore article | Qdrant | n/d (Gridstore since v1.13) | https://qdrant.tech/articles/gridstore-key-value-storage/ |
| Vector Search Benchmarks (+ raw JSON) | Qdrant | data 2024-06-15 | https://qdrant.tech/benchmarks/ |
| Pgvector vs. Qdrant (50M×768) | Tiger Data (Timescale) | 2025-04-29 | https://www.tigerdata.com/blog/pgvector-vs-qdrant |
| VectorDBBench leaderboard | Zilliz | 2026-03-31 | https://zilliz.com/vdbbench-leaderboard |
| Qwen3-Embedding-0.6B model card | Qwen | n/d | https://huggingface.co/Qwen/Qwen3-Embedding-0.6B |

## Uncertainties and gaps
- **No independent benchmark matches our shape:** 1024-d, a 1–4 GB cap, hybrid BM25, selective filters, 10M/50M points. All latency figures in §6 for our setup are extrapolations [EST]. Tiger Data and Zilliz are competitors and used older Qdrant versions (1.13.4, unknown) without 1.16–1.20 features (inline storage, TurboQuant, io_uring, PiPNN).
- **BM25 index size and latency:** no published figures. My bytes-per-posting and latency ranges are estimates. Whether full-text indexes accept `memory: cold` exactly like other payload indexes is inferred from the general "payload indexes" tier docs plus the on-disk inverted-index code; not separately documented for text indexes.
- **WAL fsync semantics** are inferred from code (`write` without a durability guarantee plus the periodic flush worker). I did not trace every write path (e.g., whether `wait=true` forces a flush), so treat this as a hypothesis to test with a power-cut test.
- **User-defined shard keys on a single node:** not confirmed by the docs. Running cluster mode with one peer is the safe assumption.
- **`is_tenant` and `is_principal` together** on one collection: not verified. Validation ranges for setting `deleted_threshold` and `vacuum_min_vector_number` to extreme values: not verified.
- **Stemmer behaviour on mixed scripts** (English stemmer on Cyrillic, Russian stemmer on Latin) is my reasoning about Snowball algorithms, not tested in Qdrant.
- **Release years:** the GitHub releases page shows day and month only. 2026 is inferred from the matching blog dates (1.20 blog dated 2026-10-22).
- **Suggested follow-ups:**
  1. Benchmark 1M/10M samples of the real corpus on the target laptop under a cgroup memory cap with the §6 configurations.
  2. Run a power-cut durability test with `wait=true`.
  3. Run an erasure audit: grep segment, WAL and snapshot files for a deleted canary string after forced vacuum.
