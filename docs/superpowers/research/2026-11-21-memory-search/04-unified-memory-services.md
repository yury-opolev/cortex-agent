# Unified memory service options for Cortex: platforms, design patterns, build vs. buy, and failure reports (as of 21 Nov 2026)

## Summary

- **Nothing off the shelf fits Cortex's constraints.** Cortex needs .NET integration, one user, a 1–2 GB RAM budget, about 2 hours a week of operations, local-first privacy, English/Russian/Danish text, and reliable erasure. Every serious open-source memory platform is written in Python or TypeScript and depends on an LLM to ingest data. Most need a graph database or Postgres. None ships a .NET SDK except Microsoft Kernel Memory, which is now an archived research project.
- **The major platforms changed direction in 2025–26:**
  - **Letta** retired its Python/Postgres server (moved to an `archive` branch). The current product is the TypeScript `letta-code`, which keeps memory in git-tracked files ("MemFS").
  - **Mem0** removed graph memory from its open-source version. It also dropped LLM-driven UPDATE/DELETE consolidation in favour of ADD-only extraction plus hybrid retrieval (semantic + BM25 + entity matching).
  - **Zep** stopped maintaining Zep Community Edition and now focuses its open-source work on Graphiti.
  - **Kuzu** (an embedded graph database) was archived in October 2025 and forked as LadybugDB.
- **Erasure bugs in leading systems are documented in code and issues:**
  - Mem0's `delete()` writes the deleted text into its SQLite history table (verified at commit 753496c, 2026-10-30).
  - Mem0's `delete_all()` leaves raw messages behind, which get re-sent to the LLM on the next `add()` (issue #7452).
  - Graphiti's `remove_episode` can delete facts that other episodes still support, and leaves removed content in entity summaries (issues #2024/#2034, Oct 2026).
- **The pieces for a custom service are mature:**
  - **Qdrant 1.20** (Oct 2026) adds BM25 scoring directly on full-text indexes, with Snowball stemmers for Russian and Danish. It has an official .NET client.
  - **SQLite** has FTS5 and `secure_delete`. **sqlite-vec** is still pre-1.0.
  - **Postgres** has built-in Russian and Danish text-search configurations, plus pgvector 0.8.7.
- **Recommendation:** build a thin .NET memory service that owns an append-only, provenance-aware event log in SQLite, with derived search projections. Start with everything in one SQLite file (FTS5 + sqlite-vec), so erasure is a single transaction. Add Qdrant later as a rebuildable index only if multilingual BM25 or vector scale requires it. A custom Rust service is only worth it as a later optimisation.

---

## 1. Platform comparison

Legend: ✅ supported · ◐ partial or with caveats · ❌ not supported · ? not verified. Stars and versions come from the GitHub/PyPI APIs on 2026-11-21.

| Platform | Memory kinds | Storage backends | Hybrid retrieval | Temporal model | Erasure | Footprint / deps | Runtime | License | Maturity (Nov 2026) | .NET usability |
|---|---|---|---|---|---|---|---|---|---|---|
| **Letta (legacy server)** | Core memory blocks, archival (vector), message recall | Postgres + pgvector (production) or SQLite + sqlite-vec (dev) | ? | Message timestamps only | API deletes; no erasure guarantees found | Third-party guide: ~500 MB Python + ~300 MB Postgres | Python | Apache-2.0 | **Retired.** Moved to `archive` branch; last release 0.16.8 (2026-05-14) | REST only |
| **Letta Code (current)** | Memory blocks as files, skills, message search, "dreaming" | Git-tracked files ("MemFS"); local backend in `~/.letta/lc-local-backend`; cloud is the default | ? | Git history of memory files | Delete from repo/history ◐ | Node CLI/App Server | TypeScript | Apache-2.0 | Active; npm 0.34.68 | CLI or REST only |
| **Mem0 OSS** | Extracted facts scoped by user/agent/run, plus entity links | Pluggable vector stores (Qdrant, Chroma, PGVector, Valkey, …) + SQLite history; external graph support **removed** in v3 | ✅ semantic + BM25 + entity matching (BM25 on Qdrant needs `fastembed`, otherwise silently off) | ◐ "time-aware retrieval"; no validity intervals; ADD-only, so contradictions accumulate | ❌ deleted text kept in history table; `delete_all` leaves raw messages | Library or Docker server; telemetry on by default (PostHog) | Python / TypeScript | Apache-2.0 | Very active; 69.3k★; PyPI 2.2.1 (2026-09-25) | REST server only |
| **Graphiti (Zep OSS)** | Episodes (raw provenance), entities, facts as edges, communities, custom types | Neo4j 5.26, FalkorDB 1.1.2, Amazon Neptune (+ OpenSearch) | ✅ semantic + BM25 + graph traversal | ✅ **bi-temporal** edges; superseded facts invalidated, not deleted | ◐ `remove_episode` exists but has open correctness bugs | Graph DB (Neo4j minimum 2 GB RAM) + LLM with structured output | Python ≥3.10 | Apache-2.0 | Active; 33k★; v0.31.2 (2026-10-20); reference servers have no auth | REST/MCP reference servers only |
| **Zep Community Edition** | Sessions, facts, temporal graph | Postgres + graph | — | — | — | — | Go/Python | Apache-2.0 | **Unmaintained since Apr 2025** | — |
| **Cognee** | Documents, chunks, knowledge graph, code graph, session lessons | Defaults: SQLite + LanceDB + Ladybug (embedded Kuzu fork). Alternatives: Postgres/pgvector, Neo4j, Qdrant, Turso, Neptune; Postgres-only mode is a demo | ✅ graph, vector, or code routing | ◐ ? | ✅ `forget` per item or dataset; `prune` | Python, or Docker image with local GLiNER extraction | Python (+ Cognee-RS in Rust) | Apache-2.0 | Very active; 33.4k★; v1.6.10 (2026-11-18) | REST only |
| **MemOS** | Text, multimodal, tool traces, personas; "memory cubes"; L1 traces / L2 policies / L3 world models / skills | Self-hosted: Neo4j + Qdrant. Local plugin: SQLite + FTS5 + vector | ✅ FTS5 + vector in the local plugin | ? | API includes delete | Self-hosted needs Neo4j + Qdrant | Python service; TypeScript local plugin | Apache-2.0 | Active; v2.0.36 (2026-11-09) | REST only |
| **Memobase** | User profile + event timeline | FastAPI + Postgres + Redis | ◐ event search | ✅ event timeline | ? | 3 services | Python | Apache-2.0 | **Stagnant**: last PyPI release Nov 2025; team promoting a new project (Acontext) | REST; Python/Node/Go SDKs |
| **LangMem + LangGraph Store** | Semantic, episodic, procedural (prompt optimisation) | `BaseStore`: Postgres, MongoDB, Redis, Upstash; optional embedding index | ◐ vector + filters | ❌ (`updated_at` only) | Store delete | Postgres | Python | MIT | LangMem has had no release since 0.0.30 (Oct 2025); LangGraph is active (1.2.24) | None |
| **Microsoft Kernel Memory** | Documents, chunks, tags, RAG with citations | Pluggable (Azure AI Search, Qdrant, Postgres, …) | ✅ (pipeline) | ❌ | Document delete | Web service or Docker | **C#** | MIT | **Archived research project, unsupported**; last package May 2025 | Native, but don't depend on it |
| **LongMemory (formerly CaviraOSS OpenMemory)** | Episodic, semantic, procedural, skills, project memory | SQLite (durable) | ✅ semantic + lexical + entity + graph | ✅ separate recorded and valid time; supersession | ? (content described as immutable) | Single Node service / Docker | TypeScript | Apache-2.0 | Active pushes; renamed | HTTP / MCP |
| **Mem0 "OpenMemory"** | — | — | — | — | — | — | — | — | **Deprecated**, replaced by the Mem0 self-hosted server; `mem0-mcp` archived 2026-03-24 | — |
| **Supermemory** | Facts, profiles (static/dynamic), documents/RAG, connectors | Local: single binary with an "embedded graph engine"; data in `~/.supermemory` | ✅ RAG + memory | ✅ claims temporal changes and automatic forgetting | ✅ "forget" tool | One binary or Docker; default embedder `bge-base-en-v1.5` (English) | TypeScript | Repo MIT, but **local server source is not public**; free "lite" licence capped at 10k documents | Server v0.0.9 (2026-10-12) | REST |
| **Official MCP memory server** | Entities, relations, observations | JSONL file | ❌ substring search | ❌ | ✅ delete tools | Tiny | TypeScript | MIT | Reference implementation | MCP |
| **mcp-memory-service** | Notes/memories | sqlite-vec (default), Milvus, Cloudflare | ◐ | ? | ? | FAQ: about 2 GB typical incl. embedding model | Python | Apache-2.0 | Active; v13.0.0 (2026-11-20) | MCP / REST |
| **basic-memory** | Markdown knowledge base | Markdown files + local index (per my understanding; not verified) | ? | ? | ? | ? | Python | **AGPL-3.0** | v1.0.0 (2026-10-29) | MCP |
| **memvid** | Append-only "smart frames", one `.mv2` file per memory | Single file | ✅ claimed | ✅ append-only timeline | ? (append-only design makes erasure hard; inferred) | Embedded | **Rust** | Apache-2.0 | v2.0.140 (May 2026); no pushes since Jul 2026 | No .NET binding found |
| **Cognee-RS** | Same remember/recall/improve/forget API as Cognee | SQLite + Ladybug + LanceDB (embedded) | ✅ | ? | `forget` | Native binary; needs cmake/protoc to build | **Rust** | ? (not verified; hit GitHub API rate limit) | New | None |
| **Small Rust projects** (`BenLocal/mem`, `rms-memory-mcp`) | Memories, transcript archive / Markdown vault | Lance + in-RAM Tantivy, fused with reciprocal rank fusion (RRF) / LanceDB | ✅ | ◐ | ? | Small | Rust | ? | Hobby-scale (inferred from repo size) | MCP / HTTP |

---

## 2. Platform evidence

### Letta
- The legacy repo README now says the current source lives in `letta-ai/letta-code`, and that the `archive` branch "contains the retired Letta V1 API server" (`letta-ai/letta:README.md`, fetched 2026-11-21).
- Letta Code features MemFS: "All context (including memory blocks) is tracked via git" (`letta-ai/letta-code:README.md`). With the local backend, the App Server reads and writes agent state in `~/.letta/lc-local-backend` (docs.letta.com/self-hosting/app-server).
- The legacy server used Postgres + pgvector for production and SQLite + sqlite-vec for development, with Alembic migrations (DeepWiki summary of letta-ai/letta; secondary source).
- Letta's own benchmark finding: an agent with only filesystem tools scored **74.0% on LoCoMo** (benchmark built on long multi-session conversations), above Mem0's reported 68.5% (letta.com/blog/benchmarking-ai-agent-memory).

### Mem0
- **April 2026 algorithm:** "Single-pass ADD-only extraction — one LLM call, no UPDATE/DELETE. Memories accumulate; nothing is overwritten." Retrieval is "semantic, BM25 keyword, and entity matching scored in parallel and fused" (`mem0ai/mem0:README.md`).
- **Migration guide:** "Graph memory moved to Platform: the external graph store integration is removed from OSS." `add()` now returns only ADD events. Default `top_k` dropped from 100 to 20, with a default similarity threshold of 0.1. On Qdrant, BM25 requires `fastembed`: "Without it, BM25 is silently disabled" (docs.mem0.ai/migration/oss-v2-to-v3).
- **Lemmatisation is language-scoped:** TypeScript and Python lemma fields differ, so collections must be "language-scoped." BM25 lemmatisation uses spaCy `en_core_web_sm`, which is English (same guide) — a concern for Russian/Danish.
- **Telemetry:** on by default — `MEM0_TELEMETRY = os.environ.get("MEM0_TELEMETRY", "True")`, sending to PostHog (`mem0ai/mem0:mem0/memory/telemetry.py:14-16`).
- **Erasure defect, verified in code at commit 753496c (2026-10-30):**
  - `_delete_memory` reads `prev_value`, deletes the vector, then calls `self.db.add_history(memory_id, prev_value, None, "DELETE", …, is_deleted=1)` (`mem0ai/mem0:mem0/memory/main.py:2116-2142`).
  - `history()` returns `self.db.get_history(memory_id)` (`main.py:1960-1973`).
  - So deleted text stays readable through the history API. An independent write-up reached the same conclusion (baeseokjae.github.io, 2026-11-01).
- **`delete_all()` leaves the scope's session messages**, which are re-sent to the LLM on the next `add()` and can be re-extracted (mem0 issue #7452, Sep 2026).

### Graphiti / Zep
- **Model:** each fact has "a validity window". Episodes are "the ground truth stream. Every derived fact traces back here". Hybrid retrieval is "semantic embeddings, keyword (BM25), and graph traversal" with "explicit bi-temporal tracking" (`getzep/graphiti:README.md`).
- **Requirements:** Neo4j 5.26 / FalkorDB 1.1.2 / Neptune. It "works best with LLM services that support Structured Output… particularly problematic when using smaller models." The reference MCP/REST servers have "no authentication, no authorization, and no prompt-injection protection."
- **Zep Community Edition:** "we've decided to stop maintaining and releasing Zep Community Edition" (getzep.com blog, 2025-04-02).
- **Erasure issues:**
  - Issue #2034 (Oct 2026): `remove_episode` "deletes an edge when `edge.episodes[0] == episode.uuid`, even if a later episode restated the fact", and "an application that calls `remove_episode` for a user's deletion request can therefore keep showing the deleted content in summaries."
  - Issue #1083: orphaned entity nodes accumulate after episode deletion.
- **Footprint:** Neo4j needs a minimum of 2 GB RAM (neo4j.com operations manual), which alone is at the top of Cortex's budget.
- Kuzu (one embedded graph option) was archived on GitHub in October 2025 (repo metadata `archived=True`; LadybugDB blog).

### Cognee
- **Defaults:** relational SQLite, vector LanceDB, graph Ladybug. "Ladybug is the renamed Kuzu engine." Isolation is per dataset, one file each (docs.cognee.ai/guides/store-configurations).
- **Operations:** `remember / recall / improve / forget`.
- **Single-Postgres mode:** "Using Postgres as a graph store is currently released as a demo feature. The production-ready version is available as a licensed product" (`topoteretes/cognee:README.md`).
- **Docker default is multi-tenant:** `ENABLE_BACKEND_ACCESS_CONTROL=false` is the single-user setting (same README).
- **Cognee-RS:** "On-device AI memory in Rust", with SQLite + Ladybug + LanceDB as the default embedded stack; needs cmake and protoc to build (`topoteretes/cognee-rs:README.md`).

### MemOS, Memobase, LangMem, Kernel Memory, LongMemory, Supermemory
- **MemOS:**
  - Self-hosting needs "Neo4j + Qdrant".
  - The local plugin uses "persistent SQLite, hybrid search (FTS5 + vector)", with tiers "L1 traces, L2 policies, L3 world models, and crystallized Skills" (`MemTensor/MemOS:README.md`; paper arXiv 2507.03724).
- **Memobase:** built on "FastAPI, Postgres and Redis". It keeps "a user profile and event timeline" per user, served with a few SQL operations (`memodb-io/memobase:readme.md`).
- **LangMem:** uses LangGraph `BaseStore`. LangGraph docs say to use `PostgresStore`, `MongoDBStore`, `RedisStore` or `UpstashStore` in production, with an embedding index (`dims`) (docs.langchain.com/oss/python/langgraph/stores).
- **Kernel Memory:** "This is an archived research project… No support is provided" (`microsoft/kernel-memory:README.md`).
- **LongMemory** (formerly CaviraOSS OpenMemory): "Durable local-first storage with SQLite", "Immutable content, provenance, and temporal truth", "recorded time and valid time are separate", with strict / historical / associative recall modes (`CaviraOSS/OpenMemory:README.md`).
- **Supermemory local:** "Its server source is not in the public repository"; free within a "lite license limit of 10,000 stored documents" (supermemory.ai/docs/self-hosting/local-vs-enterprise). Default local embedder is `Xenova/bge-base-en-v1.5` (`supermemoryai/supermemory:README.md`).

### Storage components (candidate building blocks)

| Component | Version / date | Relevant facts | Source |
|---|---|---|---|
| **Qdrant** | v1.20.2 (2026-11-16) | 1.20 (2026-10-22) adds "BM25 scoring directly on a full-text index", with k/b tunable per query and usable inside hybrid queries. Snowball stemmers via rust-stemmers (includes Danish and Russian), plus a multilingual tokenizer. Vectors are always memory-mapped from disk, with a configurable RAM tier. | qdrant.tech/blog/qdrant-1.20.x; qdrant.tech/documentation/search/text-search/full-text-search; rust-stemmers README |
| **Qdrant .NET client** | 1.20.0 (2026-10-22), Apache-2.0 | Official. The `Microsoft.Extensions.VectorData` connector implements `IKeywordHybridSearchable`. | GitHub API; learn.microsoft.com/dotnet/ai/vector-stores/vector-search |
| **SQLite FTS5** | — | Built-in tokenizers: unicode61, ascii, porter (an English stemming wrapper), trigram. No Russian/Danish stemmer built in. Contentless-delete tables exist. | sqlite.org/fts5.html |
| **SQLite `secure_delete`** | — | "When secure_delete is on, SQLite overwrites deleted content with zeros… or else run VACUUM after the delete." | sqlite.org/pragma.html |
| **sqlite-vec** | v0.1.9 (2026-03-31); 0.1.10 alphas | Pre-1.0. The alphas add approximate-nearest-neighbour indexes (DiskANN, rescore, experimental IVF). | github.com/asg017/sqlite-vec/releases |
| **pgvector** | v0.8.7 (latest tag) | — | GitHub tags API |
| **Postgres FTS** | — | Snowball dictionaries include Danish and Russian. | postgresql.org/docs/current/textsearch-dictionaries.html |
| **ParadeDB** | v0.27.0 | AGPL-3.0 | GitHub API |
| **LanceDB** | v0.41.0 (Rust) | C# SDK is **community-built** (lennylxx; NuGet `LanceDB` 2.2.0, P/Invoke over the Rust crate). | nuget.org/packages/LanceDB |
| **Tantivy** | 0.26.1 | MIT | GitHub API |

---

## 3. Design patterns for a unified memory service

1. **Event log as source of truth, indexes as projections.**
   - Graphiti, LongMemory and memvid all keep raw episodes or immutable frames, and derive facts from them with provenance.
   - Fowler's bitemporal pattern layers "an append-only record history over the actual history", so the actual history can be corrected while keeping a record of every correction (martinfowler.com/articles/bitemporal-history.html).
   - For Cortex: an `events` table (id, kind, channel, occurred_at, recorded_at, source_ref, content_hash, payload), plus derived `memory_items` (kind ∈ {episodic, semantic, procedural, document_chunk}, valid_from, valid_to, superseded_by, evidence_event_ids[], embedding_version).
2. **Keeping several indexes consistent.**
   - If the search index lives in the same SQLite or Postgres database, write the event and its projection rows in one transaction; no outbox is needed.
   - If any index is external (Qdrant, Tantivy, LanceDB), use a transactional outbox. Messages are "sent if and only if the database transaction commits", and the relay "might publish a message more than once", so consumers must be idempotent (microservices.io/patterns/data/transactional-outbox.html).
   - Use deterministic point IDs (for example, item_id + embedding_version) so replays are harmless.
3. **Erasure across all indexes.**
   - Make erasure a first-class event (`ErasureRequested{subject|item|event ids}`) that cascades in this order: source events → derived items, using the evidence-ID graph → FTS rows → vectors → caches, LLM summaries and profiles → history/audit rows → logs.
   - Two failures to design against explicitly:
     - Audit or history tables that store old values (Mem0 `main.py:2116-2142`).
     - Derived summaries that keep text from deleted sources (Graphiti #2034).
   - Re-derive or invalidate any item whose evidence set shrinks; delete it outright if the set becomes empty.
   - On SQLite, enable `secure_delete` or VACUUM afterwards, and checkpoint the write-ahead log (WAL).
   - For backups, **crypto-shredding** (per-subject or per-item keys; destroy the key to erase) lets an immutable log and old backups comply without being rewritten (pattern described in Encina docs and Relay articles; vendor or community sources).
4. **Versioned embeddings and re-embedding.**
   - Vectors from a new model are incompatible with old queries; "there's no in-place fix" (AWS builder article, Jan 2026).
   - Microsoft's 2026 guidance: "Regenerate document vectors; don't copy v3 vectors and only change the query model." Track "the embedding-contract version and source revision." Use a write pause or coordinated dual-write, then cut over via an alias (learn.microsoft.com/azure/search/vector-search-how-to-migrate-embedding-models, 2026-11-17).
   - For Cortex: store `embedding_model`, `dims` and `version` per vector. Run a background backfill into a shadow collection or column, then atomically switch the reader. At personal scale (thousands to low hundreds of thousands of items), a full re-embed with a local model is an overnight job.
5. **Bi-temporal facts.** Store both valid time and record time (Graphiti, LongMemory). Never hard-delete superseded facts except for erasure; filter on `valid_to IS NULL` for "current" queries. This directly addresses the LongMemEval "knowledge updates" and "temporal reasoning" abilities (arXiv 2410.10813).
6. **API shape seen across systems:**
   - Typed kinds plus scope filters: Mem0 moved IDs into a `filters` dict; Supermemory uses `containerTag`; Cognee uses datasets; MemOS uses cubes.
   - Recall modes: LongMemory has strict / historical / associative.
   - Evidence returned with results.
   - Suggested surface: `Append(event)`, `Upsert(item, evidenceIds)`, `Search(query, kinds[], filters{channel, time range, as-of time}, mode)`, `Erase(selector) → ErasureReport`, `Reindex(embeddingVersion)`, `Export/Snapshot`.
7. **Access scoping.** Even with one user, scope by channel/source and sensitivity. Graphiti's reference servers let "any caller… read, change, or delete data in any `group_id`" — authentication has to be designed in from the start.

---

## 4. Build vs. buy

| Option | Integration into .NET | Operations burden | Main risks |
|---|---|---|---|
| **Adopt a Python platform** (Mem0, Graphiti, Cognee, MemOS) | REST only; a second runtime to run | Python service plus the graph/vector DB it needs (Neo4j or Qdrant etc.); upgrades bring breaking changes (Mem0 v3) | Erasure bugs above; Mem0 telemetry on by default; English-centric NLP; LLM-heavy ingestion; product direction outside your control (Zep CE, Letta V1 and OpenMemory were all retired) |
| **Wrap Qdrant** as a derived index | Official .NET client and `Microsoft.Extensions.VectorData` connector | One extra Rust binary; snapshots; version upgrades | Two stores to keep consistent (needs an outbox); erasure must also reach Qdrant's on-disk segments, and I found no Qdrant documentation on secure physical deletion |
| **Postgres + pgvector** single store | Npgsql + Pgvector .NET | Moderate: major-version upgrades, vacuum, about 100–300 MB RAM (third-party estimate) | More operations than SQLite; erasure still requires VACUUM plus WAL/backup handling |
| **SQLite + FTS5 + sqlite-vec** single file | Microsoft.Data.Sqlite (already in Cortex) | Lowest: one file; backup is the online backup API or a file copy | No Russian/Danish stemming in FTS5 (needs a custom tokenizer, trigram, or pre-normalising text in .NET); sqlite-vec is pre-1.0 and brute-force in stable releases |
| **Custom Rust service** (Tantivy + LanceDB/usearch + SQLite) | gRPC/HTTP from .NET; a second toolchain | You own every bug, migration and security fix | Highest build cost; Tantivy + Lance are mature, but the glue (outbox, erasure cascade, re-embedding) is what's hard, and Rust doesn't remove it |

**When wrapping beats writing:** wrap Qdrant or Postgres when you need stemmed multilingual BM25, filtered approximate search above roughly 1M vectors, or snapshots without writing them yourself. A Rust service only pays off if you need an embedded, single-binary engine used by several hosts, or sub-millisecond latency. Neither applies to a single-user assistant.

---

## 5. Failure reports and lessons from production systems

1. **Stale and contradictory facts:**
   - Mem0's ADD-only design means "contradictory memories accumulate over time… retrieval… can return Company A as a top-ranked candidate because the scoring signals don't incorporate recency" (mem0 issue #4956, 2026).
   - A pre-publication review says "nobody decides conflicts" and quotes a maintainer: "Both memories being stored is intentional" (issue #7002, Aug 2026).
   - Lesson: with ADD-only, you need temporal validity or recency signals at retrieval time.
2. **LLM consolidation was abandoned by Mem0.** Mem0 replaced ADD/UPDATE/DELETE/NOOP consolidation with ADD-only plus hybrid retrieval and reported large benchmark gains: LoCoMo 71.4→91.6, LongMemEval 67.8→93.4 (docs.mem0.ai migration guide; vendor numbers). This is directly relevant to Cortex's current consolidation loop.
3. **Deletion bugs:**
   - Mem0 history table retains deleted text (code verified).
   - Mem0 `delete_all` leaves messages that are re-extracted (#7452).
   - Graphiti `remove_episode` mis-deletes restated facts and leaves deleted content in summaries (#2034), and leaves orphan nodes (#1083).
4. **Unreliable benchmarks.** Zep reported that Mem0's paper ran Zep incorrectly, and that "a simple full-context baseline… achieved a J score of ~73%, compared to Mem0's best score of ~68%" (getzep.com blog, May 2025). Letta's file-only agent scored 74% on LoCoMo. Lesson: build your own evaluation on real Cortex data and use LongMemEval-style ability categories.
5. **Hidden silent fallbacks.** Mem0 BM25 is "silently disabled" without `fastembed`. Re-embedding cannot be done in place (AWS / Microsoft guidance).
6. **Memory poisoning.** Prompt injection into ChatGPT's memory persisted instructions and exfiltrated later conversations ("SpAIware", Rehberger; disclosed 2024-09-20; ScienceDirect paper). Lesson: record provenance and trust level for every write, and allow user review and quarantine of agent-written memories.
7. **Unbounded growth.** LangGraph docs warn that "checkpoints accumulate" and recommend pruning. Graphiti accumulates orphan nodes. Plan retention, compaction and TTL policies for episodic data from the start.

---

## 6. Recommended architecture options for Cortex

**Option A (recommended first step): .NET memory service, single SQLite file.**
- An `events` log with provenance, hash and both timestamps, plus `memory_items` with kind, valid_from/valid_to, evidence IDs and embedding_version.
- FTS5 for keyword search, sqlite-vec for vectors, reciprocal rank fusion and filters in C#.
- All projections are written in the same transaction, so there is no outbox. Erasure is one transaction plus `secure_delete`, a WAL checkpoint and backup crypto-shredding.
- **Pros:** fits within ~200–500 MB RAM (estimate); almost no operations; reuses existing Cortex code; erasure is provable.
- **Cons:** Russian/Danish stemming must be done in .NET before indexing (for example a Snowball port, or a trigram tokenizer for fuzzy matching); sqlite-vec is pre-1.0; brute-force search is fine below roughly 10⁵–10⁶ vectors (inferred).
- Can run in-process or as its own container behind gRPC.

**Option B: Option A plus Qdrant as a rebuildable projection.**
- SQLite remains the source of truth. An outbox feeds Qdrant with deterministic IDs.
- Qdrant 1.20 gives BM25 with Snowball stemming for English/Russian/Danish plus dense hybrid search and filters, through the official .NET client.
- **Pros:** proper multilingual BM25; HNSW approximate search; Qdrant can be wiped and rebuilt from the log.
- **Cons:** a second container; outbox and idempotency code; erasure must be confirmed in both stores, and Qdrant's physical deletion semantics are not verified here.

**Option C: Postgres 17/18 + pgvector + built-in Danish/Russian text search.**
- Single transactional store with mature multilingual stemming.
- **Cons:** heavier operations (upgrades, vacuum) for marginal gain over A or B at single-user scale.

**Option D: custom Rust memory service** (Tantivy + LanceDB or usearch + SQLite).
- Only worth it if Cortex outgrows A/B or several non-.NET clients need an embedded engine.
- Cognee-RS, memvid and `BenLocal/mem` show the stack works, but none is a drop-in fit.

**Option E: adopt Graphiti, Mem0 or Cognee.**
- Not recommended as the system of record: Python runtime, RAM and graph-DB cost, erasure defects, product churn.
- Worth borrowing their ideas: Graphiti's bi-temporal edges, Mem0's hybrid scoring with entity boosting, LongMemory's strict/historical recall modes.

---

## 7. Sources

| Title | Org / author | Date | URL |
|---|---|---|---|
| Letta README (legacy → letta-code) | Letta | fetched 2026-11-21 | https://github.com/letta-ai/letta |
| Letta Code README | Letta | fetched 2026-11-21 | https://github.com/letta-ai/letta-code |
| Letta App Server docs | Letta | crawled 2026-11 | https://docs.letta.com/self-hosting/app-server |
| Benchmarking AI Agent Memory: Is a Filesystem All You Need? | Letta | Aug 2025 | https://www.letta.com/blog/benchmarking-ai-agent-memory/ |
| Mem0 README (April 2026 algorithm) | Mem0 | fetched 2026-11-21 | https://github.com/mem0ai/mem0 |
| Migrating to the new memory algorithm (OSS v2→v3) | Mem0 | 2026 | https://docs.mem0.ai/migration/oss-v2-to-v3 |
| mem0 `main.py` / `telemetry.py` @ 753496c | Mem0 | 2026-10-30 | https://github.com/mem0ai/mem0/blob/main/mem0/memory/main.py |
| Issue #4956 (ADD-only stale facts) | mem0 community | 2026 | https://github.com/mem0ai/mem0/issues/4956 |
| Issue #7002 (dedup write-path review) | mem0 community | Aug 2026 | https://github.com/mem0ai/mem0/issues/7002 |
| Issue #7452 (delete_all leaves messages) | mem0 community | Sep 2026 | https://github.com/mem0ai/mem0/issues/7452 |
| Delete Returns 200: What Agent-Memory Stores Actually Remove | baeseokjae (independent) | 2026-11-01 | https://baeseokjae.github.io/posts/agent-memory-delete-semantics-test-2026/ |
| mem0-mcp (archived) | Mem0 | 2026-03-24 | https://github.com/mem0ai/mem0-mcp |
| Graphiti README | Zep | fetched 2026-11-21 | https://github.com/getzep/graphiti |
| Zep: A Temporal Knowledge Graph Architecture | Rasmussen et al. | Jan 2025 | https://arxiv.org/abs/2501.13956 |
| Graphiti issue #2034 (remove_episode) | community | Oct 2026 | https://github.com/getzep/graphiti/issues/2034 |
| Graphiti issue #1083 (orphan entities) | community | 2025 | https://github.com/getzep/graphiti/issues/1083 |
| A New Direction for Zep's Open Source Strategy | Zep | 2025-04-02 | https://www.getzep.com/blog/announcing-a-new-direction-for-zeps-open-source-strategy/ |
| Is Mem0 Really SOTA in Agent Memory? | Zep | 2025-05 | https://www.getzep.com/blog/lies-damn-lies-statistics-is-mem0-really-sota-in-agent-memory/ |
| Cognee README; Store Configurations; prune | Topoteretes | 2026-10/11 | https://github.com/topoteretes/cognee ; https://docs.cognee.ai/guides/store-configurations |
| Cognee-RS README | Topoteretes | 2026 | https://github.com/topoteretes/cognee-rs |
| MemOS README; MemOS paper | MemTensor | 2026; Jul 2025 | https://github.com/MemTensor/MemOS ; https://arxiv.org/abs/2507.03724 |
| Memobase README | MemoDB | 2025–26 | https://github.com/memodb-io/memobase |
| LangMem README; LangGraph Stores / Persistence | LangChain | 2025–26 | https://github.com/langchain-ai/langmem ; https://docs.langchain.com/oss/python/langgraph/persistence |
| Kernel Memory README | Microsoft | archived | https://github.com/microsoft/kernel-memory |
| LongMemory (formerly OpenMemory) README | CaviraOSS | 2026 | https://github.com/CaviraOSS/OpenMemory |
| Supermemory README; Local vs Enterprise | Supermemory | 2026 | https://github.com/supermemoryai/supermemory ; https://supermemory.ai/docs/self-hosting/local-vs-enterprise |
| Knowledge Graph Memory Server | MCP project | 2026 | https://github.com/modelcontextprotocol/servers/tree/main/src/memory |
| mcp-memory-service FAQ | doobidoo | 2026 | https://github.com/doobidoo/mcp-memory-service/wiki/08-FAQ |
| memvid | Memvid | 2026 | https://github.com/memvid/memvid |
| BenLocal/mem; rms-memory-mcp | community | 2026 | https://github.com/BenLocal/mem ; https://github.com/max-ramas/rms-memory-mcp |
| Qdrant 1.20 – Full-Text Search Improvements | Qdrant (A. Vasnetsov) | 2026-10-22 | https://qdrant.tech/blog/qdrant-1.20.x/ |
| Qdrant full-text search / storage docs | Qdrant | 2026 | https://qdrant.tech/documentation/search/text-search/full-text-search/ ; https://qdrant.tech/documentation/manage-data/storage/ |
| Vector search with vector store providers (.NET) | Microsoft Learn | 2026-04 | https://learn.microsoft.com/en-us/dotnet/ai/vector-stores/vector-search |
| LanceDB C# SDK (NuGet) | community (lennylxx) | 2026-05 | https://www.nuget.org/packages/LanceDB/2.2.0 |
| sqlite-vec releases | A. Garcia | 2026-03/05 | https://github.com/asg017/sqlite-vec/releases |
| SQLite FTS5; PRAGMA secure_delete | SQLite | current | https://www.sqlite.org/fts5.html ; https://www.sqlite.org/pragma.html |
| PostgreSQL text search dictionaries | PGDG | current | https://www.postgresql.org/docs/current/textsearch-dictionaries.html |
| Neo4j system requirements | Neo4j | current | https://neo4j.com/docs/operations-manual/current/installation/requirements/ |
| Ladybug: Spreading its Wings (Kuzu archived) | LadybugDB | 2026 | https://blog.ladybugdb.com/post/ladybug-spreading-its-wings/ |
| Transactional outbox pattern | C. Richardson | — | https://microservices.io/patterns/data/transactional-outbox.html |
| Bitemporal History | M. Fowler | — | https://martinfowler.com/articles/bitemporal-history.html |
| Migrate embedding models (Azure AI Search) | Microsoft Learn | 2026-11-17 | https://learn.microsoft.com/en-us/azure/search/vector-search-how-to-migrate-embedding-models |
| Best Practices for Updating Embedding Models in OpenSearch | AWS Builder Center | 2026-01-23 | https://builder.aws.com/content/34l17SHJb0MNL1uErU8hyi6jCby/ |
| Crypto-shredding: GDPR erasure in event sourcing | Relay / Encina docs | 2026 | https://relay.nuvoralabs.com/articles/crypto-shredding-right-to-erasure/ |
| LongMemEval | Wu et al. (ICLR 2025) | Oct 2024 | https://arxiv.org/abs/2410.10813 |
| Memory in the Age of AI Agents (survey) | Hu et al. | Dec 2025 | https://arxiv.org/abs/2512.13564 |
| SpAIware (memory prompt injection) | Rehberger / ScienceDirect | 2024 / 2025 | https://www.sciencedirect.com/science/article/pii/S0167739X25002894 |

---

## 8. Uncertainty notes

- **RAM figures:**
  - Third-party or estimated: Letta (~500 MB + ~300 MB), Postgres (100–300 MB), and Option A (200–500 MB).
  - Vendor-published: mcp-memory-service ~2 GB (its FAQ) and Neo4j's 2 GB minimum.
  - Qdrant RAM depends on the data. My own arithmetic: 100k × 1024-dim float32 vectors ≈ 410 MB raw, about half with float16 or turbo16.
- **Mem0 versioning:** the docs call it "v2→v3", but the Python package on PyPI is 2.2.1 and the TypeScript SDK is `ts-v3.3.1`. How these map onto each other was not resolved.
- **Not verified:**
  - Cognee-RS licence (GitHub API rate limit).
  - basic-memory's storage design.
  - Supermemory's internal graph engine.
  - Whether Qdrant physically purges deleted points from disk segments.
  - Whether Letta's legacy server had hybrid BM25 search.
- **Vendor benchmark scores** (Mem0, MemOS, Supermemory "#1") are self-reported and disputed between vendors; treat them as marketing.
- **Third-party aggregators** (memoryatlas.dev, hysenlabs, zairalabs, frontiernow) were used only for discovery. Their claims were re-checked against primary sources where cited. One exception: "Graphiti Kuzu deprecated" comes from an aggregator; Graphiti's README simply no longer lists Kuzu.
- **Suggested follow-ups:**
  - Benchmark FTS5 with .NET-side Snowball stemming against Qdrant 1.20 BM25 on real English/Russian/Danish Cortex data.
  - Test sqlite-vec 0.1.10 approximate search when it reaches stable.
  - Test Qdrant point deletion against on-disk segments with `strings` / forensic grep.
