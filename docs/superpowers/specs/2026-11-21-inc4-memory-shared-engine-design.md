# Increment 4: Curated memory on the shared engine — design

Date: 2026-11-21
Status: Draft for implementation.
Parent: [`2026-11-21-episodic-history-memory-design.md`](2026-11-21-episodic-history-memory-design.md) (Part I: D1–D10, §4 roadmap row 4, §5).
Builds on: increment 0 (MemoryMcp.Core correctness: exact search, dense-only `FindSimilarAsync`, physical deletion), increment 1 (D10 quarantine, contentless FTS5 conventions) and increment 3 ([hybrid history search](2026-11-21-inc3-hybrid-history-search-design.md): fusion, exact intent, vector math, folding, stemming, evaluation protocol) as implemented on `feat/episodic-history-memory`.
Plan: [`../plans/2026-11-21-inc4-memory-shared-engine.md`](../plans/2026-11-21-inc4-memory-shared-engine.md).

---

## 1. Goal

Roadmap row 4: *"Extract the proven engine into MemoryMcp.Core; `memory_search` hybrid mode. Gate: ≥ 250 memory
queries; Gate H for memory."*

1. The engine-agnostic parts of hybrid history search — fusion, exact-intent detection, vector math, folding,
   tokenizing, stemming, ranking metrics and paired statistics — live once, in `MemoryMcp.Core`, and history
   search consumes them **without any behaviour change** (the existing history tests pass unmodified).
2. Curated memories get a keyword (FTS5 BM25) index, and `memory_search` gets `mode=auto|hybrid|keyword|dense`
   with filters (tags, D10 quarantine) applied before scoring in both lists, physical erasure of index rows,
   and a backfill for existing memories.
3. The owner can measure it: a memory query dataset schema, generator support for ≥ 250 queries, the shared
   metrics/statistics/ablation reports and a `memory-eval` CLI. The default stays today's semantic search until
   the owner's Gate H decision.

## 2. Scope

**In scope**
- `MemoryMcp.Core.Retrieval` and `MemoryMcp.Core.Evaluation` (shared engine), consumed by `Agent.Host/History`.
- `memories_fts` keyword index in `SqliteVecMemoryStore`: schema, write path, delete/edit erasure, fingerprinted
  backfill, capability probe, BM25 search with filters before ranking.
- Dense search with excluded ids applied before scoring (`MemorySearchFilter`).
- `IHybridMemorySearch` (dense, keyword, hybrid; fallbacks) and `MemoryMcp:Search` options.
- `memory_search` modes, §5.3 description wording, byte-identical default output; D10 via exclusion before scoring.
- Memory evaluation tooling and the `memory-eval` CLI; documentation; default configuration.

**Out of scope** (§14): running the evaluation and the Gate H decision (owner), changing the default mode,
memory-output fencing, encryption of `memory.db`, the Bridge memory UI search, the standalone MemoryMcp
`search_memory` tool, a reranker (increment 5), ANN indexes.

## 3. Decisions

| ID | Decision |
|---|---|
| I4-1 | **The shared engine lives in `MemoryMcp.Core`** (roadmap wording; Agent.Host already references it, so no new project or package): `MemoryMcp.Core.Retrieval` = `RankFusion` (generic key, `FusionMethod`, `FusionSettings`, `RankedItem<T>`, `FusedItem<T>`), `QueryIntent`, `RecencyPrior`, `VectorMath`, `TextFolder`, `KeywordTokenizer`, `SnowballStemmer`, `KeywordQuery`; `MemoryMcp.Core.Evaluation` = `RetrievalMetrics` (generic ids), `CalibrationScore`, `EvalStatistics`, `EvalSplits`, `ParaphraseGuard`, `AblationScore`/`ConfigSummary`/`PairedComparison`/`AblationReport` + `AblationReporting`, `Preregistration`. Code moves verbatim (same arithmetic, same tie-breaks). |
| I4-2 | **History keeps its names and its tests.** Engine-neutral types move under the same names and Agent.Host (and its test project) import the two namespaces with csproj `<Using>` items. History-named types stay as thin adapters: `HistoryTokenizer` → `KeywordTokenizer`, `HistoryStemmer` (and its `Version`) → `SnowballStemmer`, `HybridFusion`/`RankedCandidate`/`FusedCandidate` → `RankFusion` with `long` segment ids (`HistoryRankingOptions.ToFusionSettings()`), `RankingMetrics` → `RetrievalMetrics` plus the history-only `RankedMessages`/locator cost; `AblationRunner` and `EvalWorkspace` delegate reporting and registration. **No file under `tests/…/History` or `tests/…/Integrity` changes** — that unchanged suite is the no-behaviour-change proof. |
| I4-3 | **`libstemmer.net` 2.2.3 moves to `MemoryMcp.Core`** (explicit `Version`: `lib/` has no central package management) and leaves `Agent.Host.csproj` and `Directory.Packages.props`. |
| I4-4 | **One contentless FTS5 row per memory** (BM25 document = memory, not chunk): `body` = `TextFolder.Fold(title + "\n" + content)`, `stem` = Snowball stems of `body`, `tokenize='unicode61 remove_diacritics 2'` (as history). The stem column is always populated, so `KeywordVariant` (Plain/Stemmed) is a **query-time** choice and the stemming ablation needs no re-index. No prefix index (memories are few; `word*` still works). |
| I4-5 | **Contentless (`content=''`, `contentless_delete=1`), not external-content.** Memory text lives in `.memory.data` files, not in `memory.db`; an external-content table would need a second plaintext copy in the database. A map table `memories_fts_map(FtsRowId INTEGER PRIMARY KEY, MemoryId TEXT UNIQUE)` gives stable integer rowids (the implicit rowid of `chunks` is not stable across `VACUUM`). |
| I4-6 | **Erasure = delete in the write transaction, then `optimize`.** Found while building this increment: a `contentless_delete` table records deletions as tombstones and keeps the deleted terms in the segment even with the FTS5 `secure-delete` option, until the segment is rewritten (byte test). So every delete and every replacement (title edit) runs `INSERT INTO memories_fts(memories_fts) VALUES('optimize')` after the commit, under `PRAGMA secure_delete=ON` (freed pages zeroed), followed by the existing `wal_checkpoint(TRUNCATE)`. At memory scale (thousands of short documents) a full rewrite is milliseconds. Content edits are delete + store (as before); title edits re-index the row in the metadata transaction. |
| I4-7 | **Backfill and versioning by fingerprint.** `memory_meta('keyword_index')` stores `schema=1;fold=<TextFolder.Version>;stem=<SnowballStemmer.Version>;tokenize=unicode61-rd2`. Missing or different → `delete-all`, re-index every memory from its content file (decrypting when MemoryMcp encryption is on) in one transaction, `optimize`, log the count; missing content files are skipped and counted. |
| I4-8 | **Capability probe, graceful absence.** The index needs SQLite ≥ 3.43 (`contentless_delete`) — true for the agent's SQLite3 Multiple Ciphers 3.51, false for the SQLCipher bundle that standalone MemoryMcp and `MemoryMcp.Core.Tests` load. `InitializeAsync` probes on a temp table; on failure `KeywordIndexUnavailableReason` is set, every keyword operation is a no-op and dense search works unchanged. Therefore the index's behaviour tests run in `Agent.Host.Tests` (SQLite3MC) and the core tests assert the unavailable path. |
| I4-9 | **Filters before scoring in both lists** (`MemorySearchFilter { Tags, ExcludeIds }`). Keyword: SQL `WHERE` on the joined chunk-0 row (`memory_tags_match`) and `NOT IN json_each(@excluded)` before `ORDER BY bm25 LIMIT`. Dense (`IMemoryStore.SearchDenseAsync`): the vec0 KNN path (k = chunk count ≤ 4096) skips excluded ids before counting results — exact; above the KNN cap any filter takes the exact-join path with the same SQL predicates. The old `SearchAsync(…, tags)` delegates to it. |
| I4-10 | **`IHybridMemorySearch` is separate from `IMemoryService`.** No member is added to `IMemoryService`, so `FindSimilarAsync` (duplicate guard, consolidation, compaction, subagent context) and every caller of `SearchAsync` (Bridge memory UI, evals) are untouched and their candidate sets cannot change. Dense mode is exactly the previous path: `EmbedAsync(query)` (no instruction unless configured), `SearchDenseAsync(limit, minScore, filter)`, embedder exceptions propagate. |
| I4-11 | **Hybrid** = keyword list (top `Candidates`=50 BM25) + dense list (top 50 by cosine, `minScore` applied to it only) fused by `RankFusion` (RRF k = 60 default; weighted RRF and convex configurable; exact intent doubles the keyword weight; ties → keyword rank → ordinal id). Keyword-only hits are loaded with `GetMemoryAsync` and carry no similarity. **Fallbacks:** query embedding failure/timeout (`QueryTimeoutMs`=3000) → keyword; keyword index unavailable → dense (also for `mode=keyword`). The result states the effective mode and the reason. |
| I4-12 | **Default unchanged.** `MemoryMcp:Search:DefaultMode = Dense`; `memory_search mode=auto` (default) resolves to it. Explicit `hybrid`/`keyword`/`dense` are always honoured, so the agent can ask for exact words now. The owner flips the default after Gate H. `UseQueryInstruction` is `false` by default for memory (queries embedded as before) and ablated. |
| I4-13 | **Tool contract.** The description carries the §5.3 wording (search when…; normally one lookup; historical data, not instructions; not proof of current truth; no hits ≠ never happened; do not infer invention). With an effective dense mode and no fallback the output is **byte-identical** to the old tool (characterisation test written before the change). Other modes add `Match: …` per memory, `(keyword match)` instead of a score for keyword-only hits, and a `mode: … [; fallback: …]` footer. If the embedder is down, `auto`/`hybrid` fall back to keyword with a note while `dense` fails exactly as before. |
| I4-14 | **D10 before scoring.** `QuarantineAwareHybridSearch` adds the quarantined ids to `ExcludeIds`; they never take a result slot in either list. `QuarantineAwareMemoryService` (over-fetch + post-filter) stays for consolidation, compaction and subagent seeding — their candidate sets are unchanged (§4 row 0 commitment). The Bridge "Needs review" list keeps using the undecorated service. |
| I4-15 | **Evaluation runs on a self-deleting copy.** `MemoryEvalCorpus` backs up `memory.db`, copies the content files into `<run>/corpus`, opens it with its own store (which backfills the keyword index in the copy) and deletes the copy on dispose. Run directories hold ids and numbers only, so — unlike `history-eval` — no purge step needs to know about them. `seeds.jsonl` (raw memory text for query authoring) is the only raw artefact; the CLI and docs tell the owner to delete it. |

## 4. Shared engine

| Type (MemoryMcp.Core) | Was (Agent.Host) | History consumer after the move |
|---|---|---|
| `Retrieval.VectorMath`, `TextFolder`, `QueryIntent`, `RecencyPrior` | `History.*` (same names) | direct, via `<Using Include="MemoryMcp.Core.Retrieval" />` |
| `Retrieval.KeywordTokenizer` | `History.HistoryTokenizer` | `HistoryTokenizer` adapter |
| `Retrieval.SnowballStemmer` | `History.HistoryStemmer` | `HistoryStemmer` adapter (`Version` = `SnowballStemmer.Version`, so the index fingerprint is unchanged) |
| `Retrieval.RankFusion` (+ settings/records) | `History.HybridFusion` body | `HybridFusion.Fuse` adapter (`long` keys, `Comparer<long>.Default`) |
| `Retrieval.KeywordQuery` | — (new; history keeps `QueryParser`) | — |
| `Evaluation.RetrievalMetrics`, `CalibrationScore` | `History.Evaluation.RankingMetrics` body | `RankingMetrics` adapter (+ `RankedMessages`, `WordsToFirstRelevant(ReturnedHit…)`) |
| `Evaluation.EvalStatistics`, `EvalSplits`, `ParaphraseGuard` | `History.Evaluation.*` (same names) | direct, via `<Using Include="MemoryMcp.Core.Evaluation" />`; `ScenarioEvals/GateReport.cs` imports it |
| `Evaluation.AblationScore`, `ConfigSummary`, `PairedComparison`, `AblationReport`, `AblationReporting` | `History.Evaluation.AblationRunner` | `AblationRunner.RunAsync`/`Markdown` delegate |
| `Evaluation.Preregistration` | `History.Evaluation.EvalWorkspace` hashing | `EvalWorkspace.Register`/`VerifyRegistration` delegate (same file format and messages) |

`NearDuplicateCollapse` stays in History (it encodes the worker-author rule). The history index, recall,
projector and renderer code is not touched.

## 5. Keyword index (`memory.db`)

```sql
CREATE VIRTUAL TABLE memories_fts USING fts5(body, stem, content='', contentless_delete=1,
  tokenize='unicode61 remove_diacritics 2');
INSERT INTO memories_fts(memories_fts, rank) VALUES('secure-delete', 1);
CREATE TABLE memories_fts_map (FtsRowId INTEGER PRIMARY KEY, MemoryId TEXT NOT NULL UNIQUE);
CREATE TABLE memory_meta (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);   -- keyword_index fingerprint
```

| Operation | Keyword index |
|---|---|
| `StoreMemoryAsync` | Map row + FTS row in the chunk/vector transaction. |
| `DeleteMemoryAsync` | FTS row + map row deleted in the transaction; then `optimize`, `wal_checkpoint(TRUNCATE)`. |
| `UpdateAsync` (content) | Delete + store (as before) → erased and re-indexed. |
| `UpdateMetadataAsync` (title) | Re-index in the metadata transaction, then `optimize`. Tags are not indexed (they are filters). |
| `InitializeAsync` | Probe → create → fingerprint check → backfill if needed. |
| `KeywordSearchAsync(match, limit, filter)` | `bm25()` over `MATCH`, joined to chunk 0 for tags, excluded ids removed, `ORDER BY rank, MemoryId LIMIT`. Score = −bm25. |

Query building (`KeywordQuery.Build`): fold, tokenize; quoted phrases → `"a b"`; `word*` → `"word"*`; other
tokens OR-ed; Stemmed adds `OR stem : (stems)`. Every term is quoted letters/digits, so user text cannot inject
FTS5 syntax; a query without a searchable token yields an empty keyword list.

## 6. Search contract (`IHybridMemorySearch`)

`HybridSearchRequest { Query, Mode, Limit=5, MinScore, Filter, Settings? }` →
`HybridSearchResult { Hits (Memory, Match, FusedScore, KeywordRank, DenseRank, Similarity), EffectiveMode, Fallback, TopSimilarity }`.
`Settings` overrides `MemoryMcp:Search` per call (evaluation ablations). `TopSimilarity` (best dense cosine) is the
abstention confidence for calibration.

## 7. Configuration (`MemoryMcp:Search`)

`DefaultMode` (Dense), `KeywordVariant` (Plain), `Fusion` (Rrf), `RrfK` (60), `KeywordWeight`/`DenseWeight` (1),
`ExactIntentKeywordBoost` (2), `Candidates` (50), `UseQueryInstruction` (false), `QueryInstruction`,
`QueryTimeoutMs` (3000). `MemorySearchOptions.Validate()` runs at agent startup; invalid values stop it.

## 8. Evaluation tooling (Gate H for memory)

**Dataset line** (`/app/state/eval/memory/dataset.jsonl`, never in the repository):

```json
{"id":"m001","text":"which editor theme does PersonA like","category":"paraphrase","stratum":"synthetic",
 "judgments":[{"memoryId":"3f0c…","grade":2},{"memoryId":"9a1d…","grade":0}],
 "tags":null,"modelQuery":{"text":"PersonA editor theme","tags":null},"sourceMemoryId":"3f0c…",
 "queryModelFamily":"family-a","judgeModelFamily":"family-b","expectAbstain":false}
```

Categories: `lookup`, `paraphrase`, `cross_language`, `identifier`, `tag_filtered` (needs oracle `tags`), `multi`
(≥ 2 relevant; set F1), `abstention`. Validation mirrors history (unique ids, grades 0–2, abstention rules, model
families differ) and `validate`/`register` require **≥ 250 queries**.

| Need | Tooling |
|---|---|
| Generator support for ≥ 250 queries | `MemoryQuerySeeds` + `memory-eval sample --count 300`: deterministic sample stratified by first tag, a fitting category per seed (identifier only for identifier-like content, tag_filtered only for tagged memories), ~10% abstention seeds; `query-generation.md` guide |
| Judging pools | `memory-eval pool --depth 10`: unjudged top-10 ids of every grid configuration per query |
| Metrics | shared `RetrievalMetrics` on memory ids: nDCG@10, recall@10, MRR, judged@10, words to the first relevant memory (shown content, `SearchMaxContentLength`), set F1 (`multi`), calibration on `TopSimilarity` |
| Statistics | shared `AblationReporting`: paired bootstrap 95% CI + randomisation test vs the **dense** baseline; per-category CIs only |
| Splits, guard, κ | shared `EvalSplits`, `ParaphraseGuard` (memory document frequency), `EvalStatistics.CohensKappa` (`memoryId,llm,human`) |
| Grid | `MemoryAblationGrid.Default()`: dense, dense+instruction, keyword plain/stemmed, RRF k ∈ {5,10,20,60}, weighted RRF ×2, convex ×3, hybrid stemmed, hybrid+instruction, model-written queries (hybrid, dense) |
| Pre-registration | shared `Preregistration` over dataset, analysis plan, ablation plan; `holdout` refuses on any change |

**Workflow** (owner, inside the agent container): `init` → `sample` → write dataset → `pool` → judge → `validate` →
`guard` → `kappa` → `ablate --split dev` → choose holdout → `register` → `holdout` → Gate H decision → optionally
`MemoryMcp:Search:DefaultMode=Hybrid`.

## 9. Owner tasks (not part of the implementation)

1. Build and judge the dataset (≥ 250 queries, real-request stratum where available, κ sample).
2. Dev ablation; choose the configuration; finish and register the analysis plan; run the holdout.
3. Gate H for memory; if it passes, set `MemoryMcp:Search:DefaultMode=Hybrid` and the chosen values.

## 10. Rollout

1. Deploy: the first start backfills `memories_fts` (log line with the count). `memory_search` output is unchanged.
2. Optional: try `memory_search mode=keyword|hybrid` by hand.
3. Evaluation (§8); default switch only after Gate H.

## 11. Risks and accepted gaps

- **Tokens in plaintext `memory.db`.** The FTS index holds tokens of memory text in `memory.db`, which (like the
  content files and vectors) is not encrypted by `Storage:EncryptAtRest`. Same exposure class as the existing
  files; erasure is byte-tested. Encrypting the memory store is a separate change.
- **`optimize` per delete** rewrites the whole index; fine for thousands of memories, revisit (merge-based
  cleanup) if the corpus grows by orders of magnitude.
- **Memory output is not fenced** with a per-response boundary token (§5.3 fencing): kept out so the default
  output stays byte-identical; the description wording is in place. Follow-up when the default switches.
- **Bridge memory UI search** stays dense (owner review tool); it gains hybrid only if the owner asks.
- **Standalone MemoryMcp** (SQLCipher) reports the keyword index unavailable; its `search_memory` stays dense.
- **Generator support is semi-manual**: seeds + guide + pooling; the queries and grades come from LLMs/the owner.

## 12. Deliberately left out

Changing the default mode, reranking, ANN, chunk-level BM25, prefix indexes, tag text in the keyword document,
memory-output fencing, `memory.db` encryption, MemoryMcp server tool changes, any evaluation run in CI.

## 13. Roadmap and cross-cutting coverage

| Requirement | Where |
|---|---|
| Extract the proven engine into MemoryMcp.Core | I4-1, I4-2, I4-3, §4 |
| No behaviour change for history search | I4-2 (history tests unchanged), I4-1 (verbatim code) |
| `memory_search` hybrid mode | I4-11, I4-12, I4-13, §6 |
| FTS5 BM25 over curated memories | I4-4, I4-5, §5 |
| D10 quarantine and tag filters before scoring in both lists | I4-9, I4-14 |
| Secure erasure of index rows on delete/update | I4-6, §5 |
| Migration/backfill | I4-7 |
| ≥ 250 memory queries; Gate H for memory | §8, §9 |
| §5.3 wording in `memory_search` | I4-13 |
| D8 (SQLite FTS5 + exact vectors, filters before scoring, RRF) | I4-9, I4-11 |
| Consolidation candidate sets unchanged (inc0) | I4-10, I4-14 |
