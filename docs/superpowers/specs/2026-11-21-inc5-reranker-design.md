# Increment 5: Reranker — design

Date: 2026-11-21
Status: Draft for implementation.
Parent: [`2026-11-21-episodic-history-memory-design.md`](2026-11-21-episodic-history-memory-design.md) (Part I: D1–D10, §4 roadmap row 5, §4.3 binding commitments, §5).
Builds on: increment 3 ([hybrid history search](2026-11-21-inc3-hybrid-history-search-design.md): fused relevance list, verified matching lines, embedder exposure guard, evaluation protocol tooling) and increment 4 ([curated memory on the shared engine](2026-11-21-inc4-memory-shared-engine-design.md): `IHybridMemorySearch`, shared evaluation kernel, `memory-eval`) as implemented on `feat/episodic-history-memory`.
Plan: [`../plans/2026-11-21-inc5-reranker.md`](../plans/2026-11-21-inc5-reranker.md).

---

## 1. Goal

Roadmap row 5: *"Reranker: `cortex-reranker` sidecar covered by Self-Update (build, pin, health,
upgrade/rollback). Gate R: significant paired gain, p95 ≤ 2.5 s."*

1. A cross-encoder reranks the head of the **fused** hybrid list of `history_search` and `memory_search`,
   seeing only the raw best-matching lines of each candidate, under a budget, a timeout and a circuit
   breaker, and saying honestly in every response whether it ran.
2. The reranker runs in a `cortex-reranker` sidecar that is pinned, authenticated, unpublished and silent
   (no request logging), and that Self-Update builds, pins, health-gates, upgrades and rolls back.
3. The owner can measure it: a latency/quality curve over TopN ∈ {10, 15, 30} × MaxDocChars ∈
   {600, 1200, 2000} with the embedder busy and with a cold model, and a Gate R report.
4. **Nothing changes until the owner enables it** (`Reranker:Enabled`, default false), per §4.3 "defer
   until increments 3–4 show a measured need".

## 2. Scope

**In scope**
- `docker/reranker` (Dockerfile, entrypoint, version), compose service `reranker` (profile `reranker`).
- Bridge: DPAPI-held API key, sidecar lifecycle, `RerankerGrant` push; Contracts: hub method and DTO.
- Agent: `Reranking` (options, access, exposure guard, llama-server client, document builder, circuit
  breaker, stage), history and memory integration, `/health → reranker`, configuration validation.
- Self-Update: `Build-RerankerImage.ps1`, `Build-All`, manifest image pins, health gate, rollback,
  downgrade documentation.
- Evaluation: rerank dimension on the ablation configurations, `rerank-curve` and `gate-r` in
  `history-eval` and `memory-eval`, `history-smoke reranker-check`.
- Documentation (`docs/reranker.md`, security, self-update, hybrid search guides).

**Out of scope** (§13): running the evaluation and enabling the reranker (owner), a Bridge UI toggle,
reranking keyword/dense/time-order results, publishing the image to GHCR or a separate repository,
caching rerank scores, reranking consolidation/subagent context, the standalone MemoryMcp server.

## 3. Decisions

| ID | Decision |
|---|---|
| I5-1 | **Server and model.** llama.cpp `llama-server --reranking` (`POST /v1/rerank`, Jina shape `{model, query, documents, top_n}` → `results[{index, relevance_score}]`), tag `server[-cuda]-b12602` **pinned by digest**. Model **Qwen3-Reranker-0.6B Q8_0** from `ggml-org/Qwen3-Reranker-0.6B-Q8_0-GGUF` (revision `a02f48b`, 639 MB), **baked in** with BuildKit `ADD --checksum=sha256:22c9979c…` (the build fails on any other bytes); no runtime download, no model volume. Fallback model documented in the Dockerfile: `bge-reranker-v2-m3` Q8_0 (gpustack revision `3093af0`, sha256 `a43c7c9b…`). The ggml-org conversion keeps the classifier head (verified: a relevant passage scores 0.99997, unrelated ones < 1e-4). |
| I5-2 | **In-repo build context `docker/reranker`, not a submodule.** Unlike `cortex-embeddings` it is two small files and a pin; a separate repository adds release plumbing without value now. Extracting it later is mechanical. |
| I5-3 | **GPU by default.** Measured on the owner's machine: CPU (20 cores) ≈ 365 prompt tokens/s — top10 × 600 chars takes **6.5 s**, top30 × 2000 chars **55 s**; CUDA (RTX 4070 Laptop) — top10 × 600 ≈ **0.1 s**, top15 × 1200 ≈ **0.24 s**, top30 × 2000 ≈ **0.7–1.1 s**. Gate R's p95 ≤ 2.5 s is out of reach on CPU, so `VARIANT=cuda` is the default image (it falls back to the CPU when no GPU is visible) and the compose service reserves the GPU like `stt`/`uni-voices`; `VARIANT=cpu` remains buildable. |
| I5-4 | **Exposure (§4.3).** Compose: internal network only, **no published port**, `pull_policy: never` (a local-only name must never be pulled from a registry), read-only root, `tmpfs /tmp`, memory limit, rotated logs. Image: unprivileged user (65532), model world-readable. Entrypoint: **refuses to start without `LLAMA_API_KEY`** (exit 64; llama-server would otherwise serve unauthenticated), `--no-webui`, `--no-slots` (slot monitoring can expose prompts), no `--metrics`, `--log-verbosity 2` (warnings only: no request lines, no bodies). Verified live: 401 without the key, `/slots` 501, logs hold only `unauthorized: Invalid API Key`. Agent: `RerankerEndpointGuard` re-checked on **every** call — an allowed in-compose host (`reranker`) or HTTPS, and **always** a key; the named HttpClient has no request logging (`RemoveAllLoggers`). Messages and exceptions never contain query or document text. |
| I5-5 | **Key.** Generated once by the Bridge (`SecretManager.GetOrCreateRerankerKey`: 64 hex chars, 256 bits, DPAPI in `secrets.json`; regenerated if it cannot be decrypted). It reaches the container only through the environment of the `docker compose` child process (`CORTEX_RERANKER_API_KEY`, interpolated into `LLAMA_API_KEY`) — never on a command line, never logged — and the agents through a new hub call `ProvideRerankerGrant(RerankerGrant { Enabled, ApiKey })` on every connect, held in memory only (`RerankerAccess`). Like the hub token, it is visible to host administrators via `docker inspect`. |
| I5-6 | **One switch, on the Bridge.** `Reranker:Enabled` in the Bridge configuration (default **false**), like `Memory:Enabled`: it starts/stops the sidecar and is pushed in the grant. The agent's `Reranker` section holds only technical settings: `Endpoint` (`http://reranker:8080`, set by compose), `Model`, `AllowedEndpointHosts`, `TimeoutMs` (2000), `FailureThreshold` (3), `BreakSeconds` (60), and per surface `History`/`Memory` { `Enabled` (true), `TopN` (15), `MaxDocChars` (1200) } so Gate R can pass for one surface only. `RerankerOptions.Validate()` runs at startup (TopN 1–50, MaxDocChars 200–4000, TimeoutMs 100–30000). |
| I5-7 | **Where: after fusion only.** `history_search` hybrid relevance order and `memory_search` hybrid mode. Keyword relevance (a streamed, page-by-page path), time order (exhaustive) and dense-only memory search (today's default, byte-identical) are never reranked; when reranking is on they state `rerank: not used: mode=… (reranking follows hybrid fusion)`. |
| I5-8 | **Input: raw best-matching lines only** (§4.3). `RerankDocument.Build(query, lines, maxChars)`: the candidate's lines — for history the hit's verified shown rows (keyword-matched rows ∪ the best dense chunk's visible rows, `DisplayContent`), for memory the title and content lines — ordered by the number of folded query words they contain, kept while they fit `MaxDocChars` (a first line longer than the budget is cut), emitted in original order, joined by newlines. No labels, timestamps, ids, summaries or other rows. Deterministic. |
| I5-9 | **Order and pagination.** The first `min(TopN, n)` fused candidates are sent; they are reordered by score (ties: fused order); the rest keep the fused order. History hits carry `RerankScore` (`| rerank 0.87`). Every page recomputes the same order (scores are deterministic for the same input); a page starting after the reranked head makes **no** call (`not used: this page starts after the reranked top N`). The cursor binding includes the budget label **only** when reranking applies, so cursors of unreranked searches are unchanged and a budget change makes a cursor stale. A reranker failure between pages degrades to the fused order (relevance pages are already at-least-once, inc1 §12). |
| I5-10 | **History reranks inside the read lease.** The verified rows are on the lease's snapshot, so deleted text is never sent and linearizability (§10 of the parent) holds; a purge waits at most `TimeoutMs` for an in-flight rerank. Alternative (release the lease, rerank, re-acquire and re-verify) rejected: a second verification pass for a 2 s bound on a rare, user-initiated purge. |
| I5-11 | **Failures never fail a search.** Timeout (`TimeoutMs`), transport/HTTP/JSON errors and invalid indices keep the fused order with `rerank: failed: <reason>; fused order kept`; unavailability (no key, guard) → `skipped: <reason>`; fewer than two candidates → `skipped: …`. A consecutive-failure **circuit breaker** opens after `FailureThreshold` failures for `BreakSeconds` (`skipped: circuit open after N failures (next try after …)`), then allows one trial call. `/health → subsystems.reranker` reports `status` (off/ok/degraded), `state` (off/ok/circuit-open/unavailable: …), failures, `circuitOpenUntil`, `lastError`, `lastLatencyMs`, `lastCallAt`; it never makes the agent unhealthy. |
| I5-12 | **Memory reranking lives in Agent.Host, not in an `IHybridMemorySearch` decorator.** `HybridSearchResult` (MemoryMcp.Core) cannot carry a rerank note, and this increment needs no submodule change. `HybridMemoryReranking` (widen the request to `max(limit, TopN)`, rerank the head of a hybrid result, cut to `limit`) is shared by `memory_search` and `MemoryEvalCorpus`. D10 quarantine still applies before scoring (the tool keeps using `QuarantineAwareHybridSearch`). |
| I5-13 | **Self-Update coverage.** *Build:* `Build-RerankerImage.ps1` (`cortex-reranker:<docker/reranker/version.json>` + `:latest`, variant label, keep 2). Opt-in: `Build-All` rebuilds it when this machine already has `cortex-reranker:latest` or with `-WithReranker` (first build ≈ 4 GB of base image + model). *Pin:* `Build-Launcher` records `images.cortex-reranker` and a new `imageIds` map (image id per tag, for **every** image); `Self-Update` refuses to deploy when a tag no longer points at its pinned id. *Upgrade:* the new Bridge runs `compose --profile reranker up -d reranker` with the key on startup; compose recreates the container when `:latest` or the key changed (verified) and is a no-op otherwise. *Health:* if the sidecar was running before the deploy, the gate waits until it runs the pinned id **and** its Docker healthcheck (`/health`) passes; `stopped`/`absent` (reranking disabled) pass. *Rollback:* the running image is tagged `cortex-reranker:rollback` before deploying and restored as `:latest` on rollback, so the last-known-good Bridge recreates it. *Downgrade:* deploy an older working tree (its Dockerfile pins the older server/model) or retag an older version and restart the Bridge. `-RollbackDrill` exercises it. |
| I5-14 | **Evaluation as tooling.** Ablation configurations gain `rerankTopN` (0 = off) and `rerankMaxDocChars`; history requests take a per-call `RerankRequest` (`Configured`/`Off`/`With(budget)`), memory configurations are applied by the corpus. `rerank-curve` runs the base hybrid configuration without reranking and with each of the 9 cells under one **condition** — `normal`, `embedder-busy` (background loops embed batches of maximum-size synthetic chunks while measuring), `cold` (run right after `docker restart cortex-reranker`: reports time to `/health` and the first call) — with a 30 s per-call timeout so the true latency is measured; it reports paired ΔnDCG@10 (bootstrap CI, randomisation p) vs no rerank, rerank p50/p95, first-page p95 and applied/failed/skipped counts. `gate-r --config C` (registration required) compares the registered reranked configuration with the same configuration without reranking on the test split, with the production timeout, then re-measures latency with the embedder busy: **Gate R passes when CI low > 0 and p < 0.05 and p95 ≤ 2.5 s in both conditions.** `ablate`/`holdout` refuse reranked configurations loudly. Outputs hold ids and numbers only; the key comes from `Reranker__ApiKey`. |
| I5-15 | **Live check.** `history-smoke reranker-check`: guard + key, readiness (`/health`), a request without the key is refused (401), `/slots` is off, a relevant passage ranks first, and the smallest and largest cells answer within `TimeoutMs` — synthetic text only. Verified against the built CUDA image: all PASS, top30 × 2000 in 1.3 s. |

## 4. Components

| Unit | Location | Responsibility |
|---|---|---|
| `Dockerfile`, `entrypoint.sh`, `version.json` | `docker/reranker/` | I5-1–I5-4 |
| compose `reranker`, agent `Reranker__Endpoint` | `docker-compose.yml` | I5-4 |
| `RerankerGrant`, `ISystemHub.ProvideRerankerGrant` | `Contracts/Hub/` | I5-5 |
| `RerankerSettingsConfig` (`BridgeConfig.Reranker`) | `Contracts/Config/BridgeConfig.cs` | I5-6 |
| `SecretManager.GetOrCreateRerankerKey` | `Bridge/Security/` | I5-5 |
| `IRerankerComposeRunner`, `DockerComposeCommandRunner` (env-only key), `RerankerSidecarLifecycle` | `Bridge/Speech/` | I5-13 upgrade path |
| `CredentialsPusher.PushRerankerGrantAsync`, `HubClient.ProvideRerankerGrantAsync` | `Bridge/Hosting/`, `Bridge/Hub/` | I5-5 |
| `RerankerOptions`, `RerankBudget`, `RerankSurfaceOptions` | `Agent.Host/Reranking/RerankerOptions.cs` | I5-6 |
| `RerankerAccess`, `RerankerEndpointGuard` | `Agent.Host/Reranking/` | I5-4, I5-5 |
| `IReranker`, `RerankerException`, `LlamaServerReranker` | `Agent.Host/Reranking/` | I5-1, I5-4 |
| `RerankDocument` | `Agent.Host/Reranking/` | I5-8 |
| `RerankCircuitBreaker`, `RerankStage` (`IRerankStage`, `RerankOutcome`, `RerankHealth`), `RerankRequest` | `Agent.Host/Reranking/` | I5-9, I5-11 |
| `HistoryRecallService.Hybrid` rerank step, `HistoryCursorCodec.Binding(…, rerank)`, `HistoryResultRenderer` | `Agent.Host/History/` | I5-7–I5-10 |
| `HybridMemoryReranking`, `MemorySearchTool` | `Agent.Host/Reranking/`, `Tools/BuiltIn/` | I5-12 |
| `AgentHub.ProvideRerankerGrant`, `Program.cs` (DI, validation, `/health`) | `Agent.Host/` | I5-5, I5-11 |
| `RerankEvaluation`, `RerankGrid`, `RerankConditions`, `BackgroundLoad`, `RerankEvalSetup` | `Agent.Host/Reranking/Evaluation/` | I5-14 |
| `HistoryRerankEval` (+ `history-eval rerank-curve`/`gate-r`), `EvalCorpus`, `AblationConfig` | `Agent.Host/History/Evaluation/` | I5-14 |
| `MemoryRerankEval` (+ `memory-eval rerank-curve`/`gate-r`), `MemoryEvalCorpus`, `MemoryAblationConfig` | `Agent.Host/Memory/Evaluation/` | I5-14 |
| `RerankerCheck` (+ `history-smoke reranker-check`) | `Agent.Host/History/Evaluation/Smoke/` | I5-15 |
| `Build-RerankerImage.ps1`, `Build-All.ps1`, `Build-Launcher.ps1`, `Self-Update.ps1` | `scripts/` | I5-13 |

## 5. Data flow

**Enable (owner):** `Reranker:Enabled: true` in the Bridge configuration → Bridge start: `GetOrCreateRerankerKey` →
`docker compose --profile reranker up -d reranker` with `CORTEX_RERANKER_API_KEY` in the child environment →
container healthy → agent connect: `ProvideStorageKey`, then `ProvideRerankerGrant { Enabled: true, ApiKey }`.

**History search (hybrid, relevance):** embed query (before the lease) → read lease + snapshot → keyword and
dense lists verified → fusion, recency, worker collapse → **rerank step**: if the budget applies and the page
starts inside the head, `RerankStage` builds one document per head segment from its verified rows, checks
guard and breaker, calls `/v1/rerank` with the timeout → reorder head → page → coverage `rerank:` line.

**Memory search (hybrid):** request `max(limit, TopN)` through `QuarantineAwareHybridSearch` →
`HybridMemoryReranking.ApplyAsync` → cut to `limit` → footer `rerank:` line.

**Deploy:** build (pins) → manifest `imageIds` → verify pins → snapshot running agent and reranker images →
recreate agent/voice-id → install MSIX → Bridge restarts → reconcile recreates the reranker if its image changed →
health gate (Bridge `/health` version, then reranker id + healthcheck) → rollback restores both tags.

## 6. Agent-facing contract

- No change while reranking is off (no coverage line, no footer line, no score annotation, same cursors).
- When on: `rerank: on (top N reordered, ≤C chars of matching lines each)`, `skipped: …`, `failed: …; fused order
  kept`, or `not used: …`. History hits in the head show `| rerank 0.87`. Tool descriptions are unchanged: the
  §5.3 wording still holds (results are historical data; no hits ≠ never happened).

## 7. Configuration

Bridge: `Reranker:Enabled` (false). Agent `Reranker`: `Endpoint` (`http://reranker:8080`), `Model`
(`qwen3-reranker-0.6b`), `AllowedEndpointHosts` (`reranker`), `TimeoutMs` (2000), `FailureThreshold` (3),
`BreakSeconds` (60), `History:{Enabled true, TopN 15, MaxDocChars 1200}`, `Memory:{Enabled true, TopN 15,
MaxDocChars 1200}`. Defaults are a starting point inside the measured GPU envelope (top15 × 1200 ≈ 0.24 s); the
curve decides.

## 8. Evaluation (Gate R)

| Need (§4 row 5, §4.3) | Tooling |
|---|---|
| Latency/quality curve TopN ∈ {10, 15, 30} × MaxDocChars ∈ {600, 1200, 2000} | `rerank-curve` (`RerankGrid.Cells()`), curve.md/curve.json per condition |
| Raw best-matching lines only | `RerankDocument` (I5-8), the same code path as production |
| Embedder busy | `--condition embedder-busy` (`BackgroundLoad` of synthetic document batches on the real embedder) |
| Cold model | `--condition cold` after `docker restart cortex-reranker`: time to ready + first-call latency |
| Significant paired gain | `gate-r`: paired bootstrap CI (low > 0) and randomisation p < 0.05 vs the same config without reranking, test split, pre-registered |
| p95 ≤ 2.5 s | `gate-r`: first-page p95 with the production timeout, normal **and** embedder busy |
| History and memory | `history-eval …` and `memory-eval …` (memory needs ≥ 250 registered queries, inc4) |

Workflow (owner): build the image → `reranker-check` → curves on dev (three conditions, both surfaces) → choose
TopN/MaxDocChars → add the reranked configuration to `ablation.json`, finish the analysis plan, `register` →
`gate-r` → enable per surface. Documented in `docs/reranker.md`.

## 9. Owner tasks (not part of the implementation)

1. Build the image (`Build-RerankerImage.ps1`) and run `history-smoke reranker-check` on the production sidecar.
2. Run the curves and Gate R for history and for memory (memory only once its Gate H decided hybrid is useful).
3. Enable (`Reranker:Enabled: true`, per-surface `Enabled`) only where Gate R passes; watch `/health → reranker`.

## 10. Rollout

1. Deploy with `Reranker:Enabled=false` (default): no sidecar, no grant key, no output change.
2. Opt in to the image (`Build-RerankerImage.ps1` once); the next Self-Update pins it.
3. Evaluation (§8); enable after Gate R.

## 11. Risks and accepted gaps

- **GPU contention.** The reranker shares the 8 GB GPU with STT/TTS (~1 GB for the model + KV). A busy GPU raises
  latency; the timeout and breaker keep searches fast, and the curve measures it. CPU-only hosts cannot pass
  Gate R (I5-3).
- **First build size** (~4 GB) — hence opt-in (I5-13).
- **Key visibility** via `docker inspect` on the host (same class as the hub token).
- **Purge may wait ≤ `TimeoutMs`** behind an in-flight history rerank (I5-10).
- **Page consistency** if the reranker fails between pages (at-least-once relevance pages, I5-9).
- **Model quality on Russian/Danish** is unmeasured: the curve's per-category CIs show it; bge-reranker-v2-m3 is
  the pinned fallback.
- **Compose `pull_policy: never`** means a missing image is an error (the Bridge logs it, searches keep the fused
  order) instead of a pull of an unrelated public image.
- **Third-party server pin** (llama.cpp moves fast): the digest pin makes upgrades deliberate; the build
  number is in the tag for traceability.

## 12. Commitment coverage (§4 row 5, §4.3)

| Commitment | Where |
|---|---|
| Defer until inc3–4 show a measured need | I5-6 default off; §9 owner decision |
| Build | I5-13, `Build-RerankerImage.ps1`, `Build-All` |
| Pinning | I5-1 (digest, sha256), I5-13 (`imageIds`) |
| Health checks | image `HEALTHCHECK`, compose, I5-11 `/health`, I5-13 deploy gate, I5-15 |
| Upgrade/downgrade and rollback in Self-Update | I5-13 |
| No published port | I5-4, `RerankerSidecarExposureTests` |
| Authentication required | I5-4, I5-5 (entrypoint refuses empty key; guard requires a key) |
| Request logging off | I5-4 (`--log-verbosity 2`, HttpClient without loggers) |
| Latency/quality curve over the grid, raw best-matching lines, embedder busy, cold model | I5-8, I5-14 |
| Gate R: significant paired gain, p95 ≤ 2.5 s | I5-14 `gate-r` |

## 13. Deliberately left out

Running the evaluation and enabling (owner); a Bridge web UI switch; reranking keyword, dense or time-order
results; a GHCR release or separate repository for the image; score caching; reranking for consolidation,
compaction or subagent seeding; the standalone MemoryMcp `search_memory`; voice-specific budgets.
