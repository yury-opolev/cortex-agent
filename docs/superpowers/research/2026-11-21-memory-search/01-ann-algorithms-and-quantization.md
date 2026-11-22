# Hybrid retrieval for Cortex memory at 10k to 1M+ chunks: index, quantization, filtering and fusion

## 0. Summary

Exact search (brute force, every vector scored) is the right default for Cortex today, and it stays viable up to about 1M chunks if you store a quantized copy and rescore the top candidates. I measured this on this laptop (i9‑13900H, .NET 10, `System.Numerics.Tensors`, 1024‑dim vectors):

| Vectors | float32, 1 thread | float32, all threads | int8 (AVX2) | binary Hamming |
|---|---|---|---|---|
| 100k | 18 ms | 7.5 ms | 2.2 ms | 0.9 ms |
| 1M | 166 ms | 67 ms | 17 ms | 4.4 ms |

Exact search also removes the filtered‑ANN (approximate nearest neighbor) problem. A restrictive filter such as "one channel, one week" shrinks the set you scan, so it gets faster and stays at 100% recall. ANN graphs (HNSW and similar) are the opposite: restrictive filters and deletions are where they fail. Qdrant's own benchmark shows plain HNSW recall dropping to 0.1% at a 1% filter. Graph indexes only clearly pay off past roughly 1–5M vectors, or under concurrent load. At that point, use a system with a filter‑aware planner (Qdrant, or pgvector ≥ 0.8 with iterative scans plus partitioning) rather than a hand‑rolled HNSW.

Two findings in the current code matter for your requirements:
1. **Post‑filtering in search.** `SqliteVecMemoryStore.SearchAsync` fetches `limit*10` nearest neighbours, then filters by tags and score (`lib/memory-mcp/src/MemoryMcp.Core/Storage/SqliteVecMemoryStore.cs:289-302`). With a selective filter, recall collapses. In my synthetic test, post‑filtering the top 100 at 1% selectivity gave recall@10 = 0.106.
2. **Deleted vectors are not erased.** The pinned sqlite‑vec `0.1.7-alpha.2.1` (`lib/memory-mcp/src/MemoryMcp.Core/MemoryMcp.Core.csproj:25`) predates the DELETE fix. In that version a delete only flips a validity bit, so the vector bytes stay in the file. The fix landed in sqlite‑vec v0.1.7 on 2026‑03‑17 (commit `cb147c8`). SQLite's `secure_delete` and FTS5's `secure-delete` are also off by default, so deleted text and index entries remain in the file too.

---

## 1. Exact (brute‑force) KNN on CPU: cost model and measurements

### 1.1 Memory per vector (1024 dims; computed, not estimated)

| Encoding | Bytes per vector | 10k | 100k | 1M | 5M |
|---|---|---|---|---|---|
| float32 | 4096 | 41 MB | 410 MB | 4.1 GB | 20.5 GB |
| float16 / bfloat16 | 2048 | 20 MB | 205 MB | 2.05 GB | 10.2 GB |
| int8 (scalar) | 1024 (+ about 4–8 B for scale/offset) | 10 MB | 102 MB | 1.02 GB | 5.1 GB |
| MRL‑512 + int8 | 512 | 5 MB | 51 MB | 512 MB | 2.6 GB |
| RaBitQ 1‑bit (Faiss formula d/8+8) | 136 | 1.4 MB | 14 MB | 136 MB | 680 MB |
| binary (sign bit) | 128 | 1.3 MB | 13 MB | 128 MB | 640 MB |
| + HNSW graph overhead, M=16 (Faiss: M·2·4 B) | +128 | +1.3 MB | +13 MB | +128 MB | +640 MB |

The HNSW and RaBitQ formulas come from the [Faiss "Guidelines to choose an index" wiki](https://github.com/facebookresearch/faiss/wiki/Guidelines-to-choose-an-index): HNSW memory is "(d·4 + M·2·4) bytes per vector", and RaBitQ is "(d/8 + 8) bytes per vector".

With about 7.8 GB of the VM's 15.6 GB already used, float32 in RAM is fine up to about 500k vectors. At 1M it costs 4.1 GB, which is uncomfortable. At 5M you need binary or 1‑bit RaBitQ in RAM, with higher‑precision copies on disk for rescoring.

### 1.2 Measured latency: one query, top‑10 (top‑400 for binary)

**Setup.** Windows host, not inside Docker. i9‑13900H (14 cores / 20 threads, AVX2 and AVX‑VNNI, no AVX‑512), DDR5 laptop memory. Random unit vectors. Median of 15 repetitions. Run on 2026‑11‑21. Docker/WSL2 with other services competing will be slower; treat these as lower bounds.

**.NET 10 (`TensorPrimitives.Dot` for float32; hand‑written AVX2 `vpmaddwd` loop for int8; `PopCount` for binary; `Parallel.For` over 20 partitions; tiered JIT off for steady state):**

| n | f32, 1 thread | f32, parallel | int8, 1 thread | int8, parallel | binary, 1 thread | binary, parallel |
|---|---|---|---|---|---|---|
| 10k | 1.5 ms | 0.5 ms | 0.3 ms | 0.13 ms | 0.22 ms | 0.19 ms |
| 100k | 18 ms | 7.5 ms (p95 21 ms) | 6.5 ms | 2.2 ms | 1.3 ms | 0.9 ms |
| 1M | 166 ms | 67 ms (p95 74 ms) | 67 ms | 17 ms | 14 ms | 4.4 ms |

**Cross‑checks with native libraries (same machine):**

| n | Faiss `IndexFlatIP`, 1 thread | Faiss flat, 20 threads | NumPy f32 matvec (OpenBLAS, 1 thread / multi‑threaded) | USearch exact f32 / i8, 20 threads | Faiss `IndexBinaryFlat` (k=400) |
|---|---|---|---|---|---|
| 100k | 24 ms | 13 ms | 14 / 7.2 ms | 9.2 / 5.3 ms | 1.1 ms |
| 1M | 236 ms | 134 ms | 149 / 75 ms | 71 / 47 ms | 12 ms |

**Pitfalls observed:**
- **float16 is a trap unless the kernel is SIMD‑native.**
  - `TensorPrimitives.Dot<Half>` took 1.58 s at 100k, about 85× slower than float32.
  - Converting each row to float32 and then calling `Dot` took 39 ms, about 2× slower than float32.
  - Faiss `SQfp16` and USearch f16 were also slower than float32 on this CPU.
  - Store f16 only for size (for example the rescoring copy), and widen it when you read it.
- **Faiss scalar‑quantizer flat indexes with a single query** (`SQ8` 80 ms at 100k) do not parallelize over the database, so don't use that as an int8 baseline.
- **The float32 scan is memory‑bandwidth bound.** 4.1 GB in about 67 ms is roughly 60 GB/s, which is about the laptop's DRAM bandwidth. Single‑thread numbers are compute/latency bound. Each 4× cut in bytes (int8) gives about 4× speed at 1M, and binary gives a further 4×.

### 1.3 When brute force is the right answer
- **Up to about 100k:** always. Float32 runs in under 20 ms on one thread, recall is 1.0, there is no index to maintain, and deletes and filters are trivial. The Faiss wiki says the same: if you need exact results, `Flat` is "the only index that can guarantee exact results"; and if you only run a few searches, "direct computation is the most efficient option" (wiki, sections "Will you perform few searches?" and "Do you need exact results?").
- **100k–1M:** brute force on int8 or binary, with exact rescoring of the top few hundred, stays at 2–20 ms. Recall is set by the rescoring depth (§3).
- **Above roughly 1–5M, or under concurrency:** a full scan costs O(n) per query, and the latency multiplies with concurrent queries on a shared VM. This is where HNSW, IVF or DiskANN start to win. Even then, keep the exact path for selective filters (§4).

---

## 2. ANN index families

| Family | Representative | Memory (1024 d) | Build | Recall | Updates / deletes | On disk? |
|---|---|---|---|---|---|---|
| Graph, in memory | HNSW (Malkov & Yashunin, arXiv 1603.09320, 2016/2018) | vectors + about 128 B/vector at M=16 (Faiss formula) | Measured: 100k × 1024, M=16, efC=200, 20 threads = 8.8 s (synthetic clustered data) | Very high at ef 64–128 on easy data. Real 1024‑d text embeddings are harder; measure. | Inserts are cheap. Deletes are tombstones; vectors stay until rebuild. Faiss HNSW "does not support removing vectors" (Faiss wiki). | Not natively (Qdrant/pgvector page it) |
| Graph, SSD | DiskANN/Vamana (NeurIPS 2019) | Compressed (PQ) vectors in RAM; graph and full vectors on SSD | Heavy (billion scale) | ">95% 1‑recall@1 in < 3 ms mean" on SIFT1B with 64 GB RAM ([NeurIPS'19](https://papers.nips.cc/paper/9527-diskann-fast-accurate-billion-point-nearest-neighbor-search-on-a-single-node)) | FreshDiskANN: real‑time inserts and deletes, ">95% 5‑recall@5", "5–10× reduction" in freshness cost vs periodic rebuild ([arXiv 2105.09613](https://arxiv.org/abs/2105.09613), May 2021) | Yes |
| Partition (IVF) | IVF‑Flat; Faiss suggests K = 4√N…16√N lists | Same as flat, plus centroids | Needs k‑means training (30·K to 256·K samples) | Tuned by `nprobe`; exact within the probed lists | Add/remove per list is easy. Centroids drift as data grows, so retrain occasionally. | Possible |
| Partition + compression | IVF‑PQ / OPQ (Jégou et al. TPAMI 2011; Ge et al. CVPR 2013) | M bytes/vector (PQ) or M/2 (PQ‑4bit fast‑scan) | Training needed | Low without rerank: "poor recall (e.g., <80%)" at ≥32× compression ([Extended RaBitQ, arXiv 2409.09913](https://arxiv.org/abs/2409.09913)) | Same as IVF | Codes in RAM, rerank vectors on disk |
| Anisotropic VQ | ScaNN ([Guo et al., arXiv 1908.10396](https://arxiv.org/abs/1908.10396), ICML 2020) | Compressed | Training | Strong on ann‑benchmarks for maximum inner product search | Mostly batch | – |
| Memory‑disk hybrid | SPANN ([arXiv 2111.08566](https://arxiv.org/abs/2111.08566), NeurIPS 2021) | Centroids in RAM, posting lists on SSD | Hierarchical balanced clustering | "2× faster than DiskANN to reach 90% recall with same memory"; about 1 ms at 90% recall@10 with 32 GB, at billion scale | Posting‑list updates (SPFresh) | Yes |

### Deletes and erasure in practice
- **hnswlib:** `mark_deleted(label)` "marks the element as deleted, so it will be omitted from search results"; `unmark_deleted` restores it. So the data is still in memory, and `replace_deleted` reuses the slots ([hnswlib README](https://github.com/nmslib/hnswlib)).
  - In my synthetic test, recall after marking 30% of points deleted stayed at 0.999 (ef=64), so tombstones are fine for recall at that ratio.
  - But tombstones are not erasure.
  - Long delete/update churn causes "unreachable points" and degradation ([Xiao et al., arXiv 2407.07871](https://arxiv.org/abs/2407.07871), Jul 2024).
- **Qdrant:** deleted points are "only marked as deleted". The vacuum optimizer rebuilds a segment once the deleted fraction exceeds `deleted_threshold` (example value 0.2) and the segment holds at least `vacuum_min_vector_number` (1000) points. "Every rebuild removes deleted points" ([Qdrant Optimizer docs](https://qdrant.tech/documentation/ops-optimization/optimizer/)).
  - For erasure, you must force optimization or lower the threshold.
- **pgvector:** deleted rows become dead tuples until VACUUM. "Vacuuming can take a while for HNSW indexes. Speed it up by reindexing first" ([pgvector README](https://github.com/pgvector/pgvector), §Vacuuming).
- **sqlite‑vec** (what Cortex uses):
  - Issue #54, "Currently only validity bit is set" (opened 2024‑07‑31).
  - Fixed by `cb147c8`, "Complete vec0 DELETE: zero data, reclaim empty chunks" (2026‑03‑17), released in v0.1.7.
  - v0.1.10‑alpha adds experimental ANN (rescore, IVF, DiskANN). Its alpha.2 notes say "Fix data leaking via un‑deleted compressed neighbor vectors in DiskANN".
  - Sources: [issue #54](https://github.com/asg017/sqlite-vec/issues/54), GitHub releases.
- **SQLite and FTS5 erasure settings:**
  - SQLite `secure_delete` is "normally off". Apps that want to avoid forensic traces "should enable the secure_delete pragma … or else run VACUUM" ([SQLite pragma docs](https://www.sqlite.org/pragma.html)).
  - FTS5: without `'secure-delete'`, "old full‑text index entries remain in the database file … Anyone with access … can use these entries to trivially reconstruct the contents of deleted FTS5 table rows" ([SQLite FTS5 §6.13](https://www.sqlite.org/fts5.html)).
  - The WAL file and backups also need handling.

---

## 3. Quantization and compression

| Technique | Compression | Measured quality (source) | Speed | Notes |
|---|---|---|---|---|
| float16 / bfloat16 | 2× | Essentially lossless for cosine (no specific source fetched) | Only fast with native f16 SIMD; slow in .NET/USearch here (§1.2) | Good format for the on‑disk rescoring copy |
| Scalar int8 | 4× | mxbai‑embed‑large (1024 d): 97% of float32 NDCG@10 on MTEB retrieval without rescore. Cohere v3: 100%. e5‑base: 94.7%. With rescore multiplier 4–5: "99%" retention ([HF blog, 2024‑03‑22](https://huggingface.co/blog/embedding-quantization)). Qdrant: error "usually less than 1%" ([Qdrant quantization docs](https://qdrant.tech/documentation/manage-data/quantization/)) | HF: 3.66× mean speedup (exact, USearch). Here: 4× at 1M (.NET AVX2) | Needs calibration ranges per dimension |
| Binary (sign) + rescore | 32× | mxbai 1024 d: 92.53% → 96.45% NDCG with rescoring. Cohere v3 binary: 94.6%. e5‑base: 74.8% (fails) (HF blog). Qdrant: ada‑002 0.98 recall@100 with 4× oversampling; 3‑small/large 0.98–0.997 with 3× oversampling and rescoring; "only efficient for high‑dimensional vectors and requires a centered distribution" | HF: 24.8× mean speedup. Here: 15× vs f32 at 1M (.NET, parallel) | Depends on the model. Qwen3‑0.6B has not been measured; you must validate. |
| RaBitQ / Better Binary Quantization (BBQ) | about 30× (1 bit + correction terms) | RaBitQ: theoretical error bound, beats PQ ([Gao & Long, SIGMOD 2024, arXiv 2405.12497](https://arxiv.org/abs/2405.12497)). Elastic BBQ (RaBitQ‑derived): CohereV3 1M × 1024 needs 208 MB vs 4162 MB raw; "above 90% recall with only 3× oversampling" (HNSW, recall@100) ([Elastic, 2024‑11‑11](https://www.elastic.co/search-labs/blog/better-binary-quantization-lucene-elasticsearch)) | Bitwise ops; asymmetric int4 query in BBQ | Faiss: `RaBitQ`, `RaBitQN` (2–9 bits), `IVF…,RaBitQfs` |
| Extended RaBitQ (multi‑bit) | 3–10× | "10 bits per dimension suffice to produce nearly perfect recall"; at ≥32× compression no method "can stably produce over 90% recall without re‑ranking" ([arXiv 2409.09913](https://arxiv.org/abs/2409.09913), Sep 2024) | SIMD | Official library: [VectorDB‑NTU/RaBitQ‑Library](https://github.com/VectorDB-NTU/RaBitQ-Library) |
| TurboQuant (random rotation + optimal scalar quantization) | 4–32× | Within about 1.45× of optimal distortion at 1 bit ([arXiv 2504.19874](https://arxiv.org/abs/2504.19874), Apr 2025). Qdrant ≥ 1.18: 4‑bit gives "similar recall and speed" to SQ8 at 2× its compression | – | Rescoring is on by default for ≤2 bits in Qdrant |
| PQ / OPQ | 16–64× | Lossy; needs rerank (above). Faiss: "for larger codes SQ is usually as accurate and faster" | Table lookups | Not worth it below about 5M vectors at this RAM budget |
| Matryoshka (MRL) truncation | 2–32× by dimension | MRL paper: "up to 14× real‑world speed‑ups for large‑scale retrieval" (ImageNet) ([arXiv 2205.13147](https://arxiv.org/abs/2205.13147)). HF blog: at 8.3% of dimensions the MRL model kept 98.37% vs 96.46% for a standard model ([HF matryoshka blog](https://huggingface.co/blog/matryoshka)) | Linear in dimensions | Qwen3‑Embedding‑0.6B: MRL "Yes", output dimensions 32–1024 ([model card](https://huggingface.co/Qwen/Qwen3-Embedding-0.6B)). No official per‑dimension retrieval curve found. |

**Oversample and rescore pattern** (HF, Qdrant, Elastic and pgvector all document a variant; pgvector's version is `binary_quantize(...)` with `LIMIT 20` and then reorders by full distance, README lines 594–607):
1. Scan a cheap representation (binary, int8, or MRL‑256) and keep k·m candidates. Typical m is 3–10.
2. Rescore those with int8 or float vectors, from RAM or memory‑mapped disk.
3. Return the top k.

The rescoring cost is negligible: about 400 × 1024 multiply‑adds, well under 0.1 ms if in RAM. That is an estimate; with random SSD reads of 4 KB per candidate, allow a few ms.

**Uncertainty:** HF's numbers are NDCG retention, not recall of the exact top‑k. Qdrant and Elastic numbers are recall against float32 ground truth on other models. Qwen3‑0.6B's suitability for binary quantization (centered distribution, collapsed dimensions) is unknown. The e5‑base case shows binary can fail badly (74.8%).

---

## 4. Filtered search

### Why HNSW breaks
- **Post‑filtering:** "If a condition matches 10% of rows, with HNSW and the default `hnsw.ef_search` of 40, only 4 rows will match on average" ([pgvector README §Filtering](https://github.com/pgvector/pgvector)).
- **In‑graph filtering:** filtering out points cuts edges. At Qdrant's m=16 (about 21 links per node on layer 0), "Filter out 96% of the points and fewer than one link per node survives" ([Qdrant, "Filtered Vector Search: What ACORN Fixes…", 2026‑08‑07](https://qdrant.tech/articles/filtered-vector-search-acorn/)).

**Qdrant benchmark** (1M × 96‑d deep‑image vectors, recall@10 at `hnsw_ef`=64, Qdrant v1.18.2, laptop):

| Filter | Plain HNSW | HNSW + ACORN‑1 | Filterable HNSW (extra payload edges) | Planner + ACORN |
|---|---|---|---|---|
| 1 keyword, 10% | 20.6% | 98.1% | 99.0% | 99.9–100% |
| 1 keyword, 1% | 0.1% | 67.7% | 99.8% | 100% (served from the payload index) |
| 2 keywords, 1% | ≈0% | 72.7% | 70.8% | 100% |
| 2 keywords, 0.012% | – | 0.6% | 1.8% | 100% (payload index, 1.3 ms) |

Filterable‑HNSW edges cost "4.4× to 5.6×" more build time (116 s vs 507–652 s). The ACORN paper's ACORN‑γ builds at "8.8× to 33.1×" plain HNSW (as quoted by Qdrant).

**My synthetic check** (hnswlib, 100k × 1024, clustered synthetic data, so absolute recall is optimistic):
- Post‑filtering the top 100 gave recall@10 of 0.858 at 10% selectivity and **0.106 at 1%**.
- Filtering inside the traversal gave 0.999–1.0, but took 12.7–37.8 ms at 1% (inflated by Python callbacks).
- An exact pre‑filter scan over the 1% subset took **1.2 ms**.

### Solutions

| Approach | Mechanism | Fits Cortex? |
|---|---|---|
| Pre‑filter + exact scan | Metadata index (B‑tree on time/channel/kind) → candidate IDs → SIMD scan | **Best for selective filters.** Cost is proportional to matching rows; recall 1.0. Qdrant's planner does this below `full_scan_threshold` (10 MB of vectors by default, "about … 2,500 vectors of 1024 dimensions"). |
| Filter inside the scan (bitmap) | Brute‑force scan that skips rows failing the predicate | Exact; cost ≤ unfiltered scan |
| ACORN ([Patel et al., SIGMOD 2024, arXiv 2403.04871](https://arxiv.org/abs/2403.04871)) | Predicate‑subgraph traversal (2‑hop); "2–1,000× higher throughput at fixed recall" (ACORN‑γ) | Via Qdrant ≥ 1.16 (ACORN‑1, opt‑in per query, `max_selectivity` 0.4) or Weaviate ≥ 1.27 ([release, 2024‑10‑29](https://weaviate.io/blog/weaviate-1-27-release)) |
| Filtered‑DiskANN ([Gollapudi et al., WWW 2023](https://dl.acm.org/doi/fullHtml/10.1145/3543507.3583552)) | Label‑aware graph; "order of magnitude or more" faster | Label equality only. Time ranges are not natively covered. |
| Qdrant filterable HNSW | Extra edges per indexed payload value; planner chooses graph, ACORN, or payload index | Good single‑field coverage. Weak for AND of 2+ strict filters without ACORN. |
| pgvector 0.8 iterative scans ([released 2024‑10‑30, announced 2024‑11‑11](https://www.postgresql.org/about/news/pgvector-080-released-2952/)) | Keeps scanning until enough rows (`hnsw.max_scan_tuples` default 20,000); `strict_order` / `relaxed_order` | Plus partial indexes ("few distinct values") and partitioning ("many different values") |
| Partitioning by time/channel | Separate segments; queries touch only relevant partitions | Natural for Cortex: monthly segments make time filters and erasure compaction cheap |
| sqlite‑vec ≥ 0.1.6 metadata / partition keys ([blog, 2024‑11‑20](https://alexgarcia.xyz/blog/2024/sqlite-vec-metadata-release/index.html)) | `WHERE` on metadata columns inside the KNN query; `partition key` shards the scan | Directly fixes Cortex's post‑filter problem |

**Benchmarks that include filters:** VectorDBBench has 1% and 99% filter cases (for example `Performance768D1M1P` and `…99P`), a `LabelFilterPerformanceCase`, and a 1024‑d 1M Cohere case ([zilliztech/VectorDBBench](https://github.com/zilliztech/VectorDBBench)). The NeurIPS'23 Big‑ANN filtered track restricted filters to 1–2 words; the winner was more than 11× faster than the baseline ([arXiv 2409.17424](https://arxiv.org/abs/2409.17424)). ann‑benchmarks ([Aumüller et al., arXiv 1807.05614](https://arxiv.org/abs/1807.05614)) does not test filters.

**Recommendation for highly selective filters** (one channel and one week is likely ≪ 10k chunks): always use pre‑filter plus exact scan. Use a cost‑based switch: if the estimated match count is below about 20–50k, run an exact float or int8 scan of the subset. That switch point is an estimate; at measured rates it is ≤ 1–2 ms. Otherwise run the full quantized scan with the filter bitmap applied inline.

---

## 5. Hybrid fusion, sparse and late interaction

### Fusion methods
- **RRF** ([Cormack, Clarke & Büttcher, SIGIR 2009](https://dl.acm.org/doi/10.1145/1571941.1572114)) is rank‑only and needs no calibration. On TREC it beat Condorcet and CombMNZ, and the best single system, "by 4% to 5% on average". The original paper uses k=60; that comes from my knowledge of the paper, not the fetched snippet.
- **Convex combination (CC)** ([Bruch, Gai & Ingber, ACM TOIS 2023, arXiv 2210.11934](https://arxiv.org/abs/2210.11934)): "RRF [is] sensitive to its parameters"; "CC outperforms RRF in in‑domain and out‑of‑domain settings"; CC "is sample efficient, requiring only a small set of training examples to tune its only parameter"; and it is "generally agnostic to the choice of score normalization".
- **Practical approach:** start with RRF (k=60). Log queries and click/use signals, build a small labelled set (about 50–200 queries; estimate), then switch to CC with a tuned α. A learned or cross‑encoder reranker (for example Qwen3‑Reranker‑0.6B) on the top 30–50 is the bigger quality lever. Its CPU cost is unmeasured, and the GPU is busy.

### Sparse and lexical
- **BM25 in SQLite FTS5:** FTS5 has a built‑in `bm25()` ranking function (§5.1.1). Its porter stemmer is "designed for use with English language terms only". `unicode61` tokenizes Cyrillic and Danish but does not stem them. A `trigram` tokenizer exists for substring and morphology‑tolerant matching. For RU/DA stemming you need a custom tokenizer, for example Snowball (not verified here).
- **SPLADE** ([Formal et al., SIGIR 2021, arXiv 2107.05720](https://arxiv.org/abs/2107.05720)) is BERT‑vocabulary (English) in its original form.
- **BGE‑M3** ([arXiv 2402.03216](https://arxiv.org/abs/2402.03216)), MIRACL dev nDCG@10 averages:

  | Method | nDCG@10 |
  |---|---|
  | BM25 (paper's baseline) | 31.9 |
  | M3 sparse | 53.9 |
  | M3 dense | 69.2 |
  | M3 multi‑vector | 70.5 |
  | dense + sparse | 70.4 |
  | all three | 71.5 |

  For Russian specifically: dense 70.1, dense + sparse 69.8. Learned sparse adds about 1 point over dense on average and nothing for Russian. It also needs a second 568M‑parameter model.
- **Qdrant BM42** ([2024‑07‑01](https://qdrant.tech/articles/bm42/)) carries Qdrant's own correction: "BM42 does not outperform BM25 implementation of other vendors … consider BM42 as an experimental approach." Not recommended.
- **Conclusion:** keep BM25 as the lexical leg. Its value in a personal memory is exact matches on names, IDs, code and rare tokens, not semantics. Fix the tokenization for RU/DA.

### Late interaction
- **ColBERTv2** ([arXiv 2112.01488](https://arxiv.org/abs/2112.01488), NAACL 2022) compresses token vectors to "20 or 36 bytes per vector" (128‑d, 1–2 bits). The MS MARCO index is 16–25 GiB vs 154 GiB for ColBERT, a 6–10× reduction.
- **PLAID** ([arXiv 2205.09707](https://arxiv.org/abs/2205.09707)) is up to 45× faster on CPU than vanilla ColBERTv2, with "tens or just few hundreds of milliseconds on a CPU" at 140M passages.
- **BGE‑M3 multi‑vector** uses 1024‑d per token. My estimate: about 300 tokens per chunk × 2 KB (fp16) ≈ 600 KB per chunk, or about 600 GB at 1M chunks. Not viable as an index. It is viable only as an on‑the‑fly reranker, where it competes with a cross‑encoder.
- **Verdict for Cortex:** skip late interaction. Use dense + BM25 + RRF/CC + an optional cross‑encoder rerank.

---

## 6. Recommendation matrix

Latencies are vector‑search time only, from my laptop measurements scaled linearly where marked "est." Expect a 1.5–3× slowdown in a busy Docker VM; that factor is also an estimate.

| Scale | Dense index | Quantization | Filter strategy | Lexical + fusion | RAM (vectors) | Expected p95 (vector part) | Recall vs exact float |
|---|---|---|---|---|---|---|---|
| **≤ 50k** (now) | Brute force, in process (`TensorPrimitives`) or sqlite‑vec ≥ 0.1.7 with metadata/partition columns | float32 (or f16 on disk, widened) | SQL pre‑filter → exact scan of the subset; no post‑filtering | FTS5 `bm25()` (secure‑delete on) + RRF (k=60) → CC later | ≤ 205 MB f32 | ≤ about 10 ms on 1 thread (est. from 18 ms/100k); under 1 ms filtered | **1.0** |
| **50k–500k** | Brute force over a contiguous int8 matrix, segmented by month | int8 in RAM + f32/f16 on disk (mmap); rescore top k·4–10. Optional MRL‑256/512 first stage. | Pre‑filter exact when matches < about 50k (est.); otherwise inline bitmap during the int8 scan | Same; add a cross‑encoder rerank of the top 30–50 if quality needs it | int8: 51–512 MB; f32 disk 0.2–2 GB | int8 parallel: about 1–9 ms (est., between measured 2.2 ms/100k and 17 ms/1M); rescore < 1–3 ms | about 0.99 with rescore (est.; HF reports 99% retention at ×4–5 for int8) → **measure** |
| **500k–5M** | ≤ about 1–2M: binary or 1‑bit RaBitQ scan in RAM → int8 rescore → f32 final. Above that, or under concurrency: **Qdrant** (HNSW + int8/TurboQuant‑4bit/BQ, originals on disk, payload indexes on time/channel/kind, planner + ACORN) or **pgvector ≥ 0.8** (HNSW on `halfvec`/`binary_quantize`, iterative scans, time partitioning) | binary 128–640 MB in RAM; int8 1–5 GB on disk/mmap; f32 on disk | Exact path for selective filters is mandatory. Use ANN only for broad or unfiltered queries. Partition by month. | BM25 in the same engine (Qdrant sparse with IDF, or SQLite FTS5 alongside), RRF/CC | 0.13–0.7 GB (binary) + graph about 0.13–0.64 GB if HNSW | Binary scan 4.4 ms/1M measured → about 22 ms/5M (est.) + rescore; HNSW typically single‑digit ms | Binary + rescore: 0.9–0.99 depending on the model (Qdrant/Elastic/HF figures) → **must validate on Qwen3‑0.6B** |

### Implementation guidance

1. **Fix filtering now.** Push tag, time, channel and kind into the KNN query: sqlite‑vec metadata or partition‑key columns, or your own pre‑filter → scan. Remove the `limit*10` post‑filter.
2. **Fix erasure now.**
   - Upgrade sqlite‑vec to ≥ 0.1.7 (preferably 0.1.9).
   - Enable `PRAGMA secure_delete=ON` and FTS5 `secure-delete`.
   - Run periodic `VACUUM`, and checkpoint the WAL.
   - For any ANN engine, schedule forced vacuum or optimization after erasure requests.
   - Monthly segments let you rewrite one segment instead of the whole store.
3. **Build an eval harness** before choosing quantization:
   - about 200–500 real queries;
   - ground truth from exact float32;
   - report recall@10 for int8, binary and MRL‑256/512 at rescore factors 1/4/10;
   - include filtered variants.

   This closes the biggest uncertainty: how well Qwen3‑0.6B tolerates binary quantization and MRL truncation.
4. **Defer graph indexes** until measured p95 under real concurrency exceeds the 100–200 ms budget. My extrapolation puts that around 1–5M chunks. When you get there, prefer a managed engine with a filter‑aware planner over embedding hnswlib yourself, because of tombstones, unreachable points and filter collapse.

---

## 7. Uncertainty and gaps
- **My benchmarks:** Windows host, not Docker/WSL2. Random or synthetic vectors (latency is data‑independent for brute force; the HNSW recall numbers are optimistic). Single query, no concurrency. A hand‑written AVX2 int8 kernel; VNNI is available but unused, so int8 could be faster.
- **Qwen3‑Embedding‑0.6B:** no published per‑dimension (MRL) or quantized‑retrieval curves found. A third‑party paper summary (AGI Hunt, 2026‑10) claims prefix truncation without MRL matches MRL up to about 75% dimension removal. It is not a primary source, so I did not rely on it.
- **Not fetched this session; cited from knowledge:** RRF k=60; PQ (Jégou TPAMI 2011) and OPQ (Ge CVPR 2013) details; FTS5 `bm25()` semantics beyond the table of contents; RU/DA Snowball availability for .NET.
- **Qdrant ACORN article:** 96‑d vectors; absolute ms will differ at 1024‑d. It cites a RACORN‑1 paper (arXiv 2607.00768) that I did not verify.
- **sqlite‑vec version mapping:** the NuGet package version `0.1.7-alpha.2.1` is assumed to correspond to upstream `v0.1.7-alpha.2`, before the fix. Verify which native binary the NuGet package actually ships.
- **ann‑benchmarks** has no 1024‑d text‑embedding dataset or filter workloads. VectorDBBench's 1024‑d Cohere case is the closest public proxy; I did not extract specific VectorDBBench numbers.

---

## 8. Sources

| # | Title | Authors / org | Date | URL |
|---|---|---|---|---|
| 1 | Efficient and robust ANN search using HNSW graphs | Malkov, Yashunin | 2016 (rev. 2018) | https://arxiv.org/abs/1603.09320 |
| 2 | DiskANN: Fast Accurate Billion‑point NN Search on a Single Node | Jayaram Subramanya et al. (MSR) | NeurIPS 2019 | https://papers.nips.cc/paper/9527-diskann-fast-accurate-billion-point-nearest-neighbor-search-on-a-single-node |
| 3 | FreshDiskANN | Singh, Subramanya, Krishnaswamy, Simhadri | 2021‑05‑20 | https://arxiv.org/abs/2105.09613 |
| 4 | Filtered‑DiskANN | Gollapudi et al. | WWW 2023 | https://dl.acm.org/doi/fullHtml/10.1145/3543507.3583552 |
| 5 | SPANN | Chen et al. (Microsoft) | NeurIPS 2021 | https://arxiv.org/abs/2111.08566 |
| 6 | Accelerating Large‑Scale Inference with Anisotropic VQ (ScaNN) | Guo et al. (Google) | 2019 / ICML 2020 | https://arxiv.org/abs/1908.10396 |
| 7 | RaBitQ | Gao, Long | SIGMOD 2024 (2024‑05‑21) | https://arxiv.org/abs/2405.12497 |
| 8 | Extended RaBitQ | Gao, Gou, Xu, Yang, Long, Wong | 2024‑09‑16 | https://arxiv.org/abs/2409.09913 |
| 9 | TurboQuant | Zandieh et al. (Google) | 2025‑04‑28 | https://arxiv.org/abs/2504.19874 |
| 10 | ACORN | Patel, Kraft, Guestrin, Zaharia | SIGMOD 2024 (2024‑03‑07) | https://arxiv.org/abs/2403.04871 |
| 11 | Enhancing HNSW for Real‑Time Updates (MN‑RU) | Xiao et al. | 2024‑07 | https://arxiv.org/abs/2407.07871 |
| 12 | Matryoshka Representation Learning | Kusupati et al. | NeurIPS 2022 | https://arxiv.org/abs/2205.13147 |
| 13 | Qwen3 Embedding | Zhang et al. (Alibaba) | 2025‑06‑05 | https://arxiv.org/abs/2506.05176 ; https://huggingface.co/Qwen/Qwen3-Embedding-0.6B |
| 14 | Binary and Scalar Embedding Quantization | Shakir, Aarsen, Lee (HF / mixedbread) | 2024‑03‑22 | https://huggingface.co/blog/embedding-quantization |
| 15 | Matryoshka Embedding Models | Aarsen et al. (HF) | 2024‑02 | https://huggingface.co/blog/matryoshka |
| 16 | Better Binary Quantization in Lucene and Elasticsearch | B. Trent (Elastic) | 2024‑11‑11 | https://www.elastic.co/search-labs/blog/better-binary-quantization-lucene-elasticsearch |
| 17 | Qdrant Quantization docs | Qdrant | accessed 2026‑11‑21 | https://qdrant.tech/documentation/manage-data/quantization/ |
| 18 | Filtering in Vector Search: When to Use What | Qdrant | accessed 2026‑11‑21 | https://qdrant.tech/documentation/search-design/vector-search-filtering/ |
| 19 | Filtered Vector Search: What ACORN Fixes… | Couzon, Ghafouri (Qdrant) | 2026‑08‑07 | https://qdrant.tech/articles/filtered-vector-search-acorn/ |
| 20 | Qdrant Optimizer (vacuum) docs | Qdrant | accessed 2026‑11‑21 | https://qdrant.tech/documentation/ops-optimization/optimizer/ |
| 21 | BM42 | A. Vasnetsov (Qdrant) | 2024‑07‑01 (with later correction) | https://qdrant.tech/articles/bm42/ |
| 22 | pgvector README; 0.8.0 release | pgvector / A. Kane | 2024‑10‑30 / 2024‑11‑11 | https://github.com/pgvector/pgvector ; https://www.postgresql.org/about/news/pgvector-080-released-2952/ |
| 23 | Weaviate 1.27 release (ACORN) | Weaviate | 2024‑10‑29 | https://weaviate.io/blog/weaviate-1-27-release |
| 24 | Faiss: Guidelines to choose an index | Douze et al. (Meta) | wiki rev. 2026‑10‑01 | https://github.com/facebookresearch/faiss/wiki/Guidelines-to-choose-an-index |
| 25 | hnswlib README | nmslib | accessed 2026‑11‑21 | https://github.com/nmslib/hnswlib |
| 26 | sqlite‑vec metadata/partition release; issue #54; releases | A. Garcia | 2024‑11‑20; 2024‑07‑31 → fixed 2026‑03‑17 | https://alexgarcia.xyz/blog/2024/sqlite-vec-metadata-release/index.html ; https://github.com/asg017/sqlite-vec/issues/54 |
| 27 | SQLite FTS5; PRAGMA secure_delete | SQLite | accessed 2026‑11‑21 | https://www.sqlite.org/fts5.html ; https://www.sqlite.org/pragma.html |
| 28 | Reciprocal Rank Fusion… | Cormack, Clarke, Büttcher | SIGIR 2009 | https://dl.acm.org/doi/10.1145/1571941.1572114 |
| 29 | An Analysis of Fusion Functions for Hybrid Retrieval | Bruch, Gai, Ingber | 2022 / TOIS 2023 | https://arxiv.org/abs/2210.11934 |
| 30 | SPLADE | Formal, Piwowarski, Clinchant | SIGIR 2021 | https://arxiv.org/abs/2107.05720 |
| 31 | M3‑Embedding (BGE‑M3) | Chen et al. (BAAI) | 2024‑02 (v5 2025‑12) | https://arxiv.org/abs/2402.03216 |
| 32 | ColBERTv2 | Santhanam, Khattab et al. | NAACL 2022 | https://arxiv.org/abs/2112.01488 |
| 33 | PLAID | Santhanam, Khattab, Potts, Zaharia | 2022‑05 | https://arxiv.org/abs/2205.09707 |
| 34 | ANN‑Benchmarks | Aumüller, Bernhardsson, Faithfull | 2018 | https://arxiv.org/abs/1807.05614 |
| 35 | VectorDBBench | Zilliz | accessed 2026‑11‑21 | https://github.com/zilliztech/VectorDBBench |
| 36 | Results of the Big ANN: NeurIPS'23 competition | Simhadri et al. | 2024‑09 | https://arxiv.org/abs/2409.17424 |
| 37 | Local measurements (.NET 10 TensorPrimitives, Faiss 1.16.0, USearch 2.26.6, hnswlib 0.8.0, NumPy 2.3.3) | this session | 2026‑11‑21 | i9‑13900H host; scripts not persisted |
| 38 | Cortex code: `SqliteVecMemoryStore.cs:101-107, 289-302`; `MemoryMcp.Core.csproj:25` | yury‑opolev/cortex‑agent | current working tree | local repo |
