# Reranker (episodic history, increment 5)

A cross-encoder reranker reorders the head of the **fused** hybrid results of `history_search` and
`memory_search`. It is **off by default** and is enabled only after the owner's Gate R decision.

Design: [superpowers/specs/2026-11-21-inc5-reranker-design.md](superpowers/specs/2026-11-21-inc5-reranker-design.md).

## Pieces

| Piece | Where | Role |
|---|---|---|
| `cortex-reranker` sidecar | `docker/reranker` (Dockerfile, entrypoint, `version.json`) | llama.cpp `llama-server --reranking` (pinned by digest) with Qwen3-Reranker-0.6B Q8_0 baked in (pinned by sha256). `VARIANT=cuda` (default) or `cpu`. |
| Compose service `reranker` | `docker-compose.yml`, profile `reranker` | Internal network only, **no published port**, `pull_policy: never`, read-only root, GPU reservation, `LLAMA_API_KEY=${CORTEX_RERANKER_API_KEY:-}`. |
| Bridge | `RerankerSidecarLifecycle`, `SecretManager.GetOrCreateRerankerKey`, `CredentialsPusher` | `Reranker:Enabled` (Bridge config, default false) starts the sidecar with the DPAPI-held key and pushes `RerankerGrant { Enabled, ApiKey }` to every agent on connect. |
| Agent | `Agent.Host/Reranking` | `RerankerAccess` (the grant, memory only), `LlamaServerReranker` (`POST /v1/rerank`, bearer key, exposure guard), `RerankStage` (budget, timeout, circuit breaker), `RerankDocument` (raw best-matching lines). |
| Self-Update | `Build-RerankerImage.ps1`, `Build-All`, `Build-Launcher`, `Self-Update.ps1` | Build, image-id pin in the manifest, health gate, rollback — see [self-update.md](self-update.md#the-reranker-sidecar-opt-in). |

## Behaviour

- **Where.** After fusion only: `history_search` in hybrid relevance order, `memory_search` in hybrid mode.
  Keyword, dense and time-order searches are never reranked; with reranking on, their coverage says
  `rerank: not used: mode=… (reranking follows hybrid fusion)`.
- **What is sent.** For each of the first `TopN` fused candidates, its raw best-matching lines only (the
  verified matching rows of a history segment; a memory's title and content lines), lines sharing the most
  query words first, capped at `MaxDocChars` per candidate. No labels, timestamps, ids or summaries.
- **Order.** The first `TopN` candidates are reordered by reranker score; the rest keep the fused order.
  History pages are recomputed identically on every page; a page starting after the reranked head makes no
  call. History hits show `| rerank 0.87`.
- **Failures.** A timeout (`TimeoutMs`), an error or an open circuit (`FailureThreshold` consecutive failures
  → skip for `BreakSeconds`, then one trial) keeps the fused order and says so:
  `rerank: failed: timed out after 2000 ms; fused order kept`. A search never fails because of the reranker.
- **Read lease.** History reranking runs inside the read lease on the verified snapshot, so deleted text is
  never sent and a purge waits at most `TimeoutMs` for an in-flight rerank.
- **Health.** `/health → subsystems.reranker`: `status` (`off`/`ok`/`degraded`), `state`
  (`off`, `ok`, `circuit-open`, `half-open`, `unavailable: …`), `consecutiveFailures`, `circuitOpenUntil`, `lastError`,
  `lastLatencyMs`, `lastCallAt`. A degraded reranker never makes the agent unhealthy.

## Configuration

Bridge: `Reranker:Enabled` (false). Agent (`Reranker` section):

| Key | Default | Meaning |
|---|---|---|
| `Endpoint` | `http://reranker:8080` | The sidecar (compose sets it). |
| `Model` | `qwen3-reranker-0.6b` | Alias sent with each request (the sidecar's `--alias`). |
| `AllowedEndpointHosts` | `reranker` | Hosts that may receive text over plain HTTP; any other host needs HTTPS. A key is always required. |
| `TimeoutMs` | `2000` | Per-call budget; slower calls keep the fused order. |
| `FailureThreshold` / `BreakSeconds` | `3` / `60` | Circuit breaker. |
| `History:Enabled`, `History:TopN`, `History:MaxDocChars` | `true`, `15`, `1200` | `history_search` budget. |
| `Memory:Enabled`, `Memory:TopN`, `Memory:MaxDocChars` | `true`, `15`, `1200` | `memory_search` budget. |

## Exposure

The sidecar receives history and memory text, so: internal network only, no published port, an API key
on every request (the entrypoint refuses to start without one; `/health` is the only unauthenticated
route), request logging off (`--log-verbosity 2`: no request lines, no bodies), no web UI, no `/slots`,
no `/metrics`, stateless, unprivileged user, read-only root. The agent re-checks the exposure rule on every
call and its `reranker` HttpClient has no request logging. The key lives in the Bridge's DPAPI store, in
the agent's memory and in the container's environment (visible to host administrators via `docker inspect`,
like the hub token).

## Measured on the owner's machine (synthetic English text)

| Variant | Cell | Latency |
|---|---|---|
| CUDA (laptop GPU) | top10 × 600 chars | ~100 ms |
| CUDA | top15 × 1200 chars | ~240 ms |
| CUDA | top30 × 2000 chars | ~0.7–1.1 s |
| CPU (20 cores) | top10 × 600 chars | ~6.5 s |

The CPU variant cannot meet Gate R's p95 ≤ 2.5 s at any useful budget; use the GPU.

## Owner workflow (Gate R)

The evaluation CLIs run outside the agent process, so they read the key from the environment; take it from
the sidecar:

```powershell
$key = docker exec cortex-reranker printenv LLAMA_API_KEY
docker exec -e Reranker__ApiKey=$key cortex-agent dotnet Cortex.Contained.Agent.Host.dll history-smoke reranker-check
```

1. `Build-RerankerImage.ps1`, set `Reranker:Enabled: true` in the Bridge config **only on a staging copy**,
   or start the sidecar by hand with a key, and run `history-smoke reranker-check` (auth enforced, slots
   off, relevant passage first, budget extremes within `TimeoutMs`).
2. Curves on the dev split, per surface (`history-eval` / `memory-eval`), under each condition:
   `rerank-curve --condition normal`, `--condition embedder-busy` (the embedder kept busy with document
   batches), and `--condition cold` right after `docker restart cortex-reranker` (reports time to ready and
   the first call). Curve runs use a 30 s per-call timeout so they show true latency.
3. Choose `TopN`/`MaxDocChars`, add the reranked configuration (`"rerankTopN": 15, "rerankMaxDocChars": 1200`
   on the chosen hybrid configuration) to `ablation.json`, finish the analysis plan, `register`.
4. `gate-r --config <name>` on the test split: paired nDCG@10 gain against the same configuration without
   reranking (bootstrap CI above 0 and randomisation p < 0.05) **and** first-page p95 ≤ 2.5 s with the
   production timeout, also with the embedder busy.
5. If Gate R passes for a surface: set `Reranker:Enabled: true` in the Bridge config (and
   `Reranker:<Surface>:Enabled: false` for a surface that did not pass), restart the Bridge, watch
   `/health → reranker`.

`ablate` and `holdout` refuse reranked configurations (use `rerank-curve` and `gate-r`). Outputs hold ids and
numbers only.
