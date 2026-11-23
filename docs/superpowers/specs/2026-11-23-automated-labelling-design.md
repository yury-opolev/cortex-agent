# Automated labelling and one-click evaluation — design

Status: proposed (2026-11-23). Builds on the [encrypted evaluation and sanitised corpus](2026-11-23-encrypted-eval-sanitised-corpus-design.md)
(E1–E14) and the evaluation protocol of the [master spec](2026-11-21-episodic-history-memory-design.md) §4.2. Decisions
below are L1–L16; D3, D5 and D6 are the master spec's.

## 1. Problem

`docs/evaluation-runbook.md` asks the owner to write ~500 history and ~250 memory queries by hand, pool candidates,
grade them 0–2, double-label a sample in CSV files, pick the configuration, fill in the analysis plan and run about
twenty commands in the right order. Nobody will do that, so Gate H would never be decided on real data. §4.2 still has to
hold: graded message-level labels, different model families for query writing and judging, the paraphrase guard, real
recall requests as a co-primary stratum, judged@10, κ ≥ 0.6 against the owner, calibrated abstention and enumeration,
`gold_filters`, pre-registration before the held-out run, raw and sanitised runs.

## 2. Goals

- One button ("Run evaluation") runs everything; the owner's inputs are exactly: the Bridge password at the start, the
  hand grading of 75 sampled items (configurable) in the UI, approving dictionary suggestions, and the package passphrase.
- Every §4.2 commitment is met mechanically and stated in an automatically written, pre-registered analysis plan.
- Reuse the existing commands (`init`, `validate`, `guard`, `ablate`, `register`, `holdout`, `kappa`, `rerank-curve`,
  `gate-r`, `eval-corpus suggest|freeze|compare|export`), jobs, protection and Bridge relay; add only what is missing.
- Resumable: crashes, restarts, call caps and cancellations lose at most a few LLM calls.
- Privacy unchanged: text goes only to the configured LLM provider (D5); nothing plaintext on disk; logs, run state and
  reports hold ids and numbers.
- Testable without live calls (fake `ILlmClient`, fake embedders, fake commands).

Non-goals: a better retrieval system, a new statistics engine (the existing paired bootstrap and randomisation test
stay), changing the manual path (it stays as "advanced").

## 3. Decisions

**L1 — Models.** `Evaluation:Labelling:QueryModel` = `gpt-6.1-sol` writes queries and oracle filters;
`JudgeModel` = `claude-opus-5.5` grades. Both are configurable and must be listed by a configured provider (the Bridge's
`llmProviders`, e.g. the `github-copilot-api` provider). `ModelFamily.Of` maps ids to families (`gpt`, `claude`, …) and a
run with equal families is refused. Calls are *pinned*: `DirectLlmClient` fails over to another provider's default model
on provider errors, which would silently change the family, so a result whose `ProviderId` is not the provider that
lists the model (`DirectLlmClient.ProviderFor`, new) counts as a failed attempt.

**L2 — Labelling is a job.** `history-eval label` and `memory-eval label [--queries N] [--kappa N] [--estimate]` run
inside the agent like every evaluation command (E-series: storage key, `EvalProtection`, refused path options). The job
copies the store into `runs/label-*` (encrypted with the evaluation key), labels it and deletes the copy.

**L3 — Sampling.** History seeds: user and assistant messages of ≥ 40 characters, without recall echoes (rows whose
tool-call summary names a recall tool) and internal rows; strata channel × month × length bucket, round-robin in a
seeded hash order; category quotas lookup 20 %, paraphrase 20 %, cross_language 10 %, temporal 15 %, identifier 10 %,
enumeration 15 %, abstention 10 % (identifier only for identifier-like text, enumeration only with ≥ 2 nearby messages
of the same channel; abstention seeds are topic hints, not gold). Memory seeds: the existing `MemoryQuerySeeds`
(stratified by tag) plus related memories for `multi`. **Real requests** (co-primary stratum, up to `RealShare` = 30 %,
spread over months): every `tool_call` event of `history_search`/`memory_search` with recorded arguments, paired with
the owner's preceding message in that channel (one seed per message). The writer rewrites the request as a standalone
query from the request and its four preceding turns (same intent and language, no added facts) and classifies its
category; the recorded arguments are its model-written query. A real request's oracle `until` is clamped to the moment
it was asked and later messages are not pooled.

**L4 — Writer checks.** A draft is rejected and written again with the reason (up to `MaxDraftAttempts` = 3) when it
repeats four consecutive folded words of the snippet (identifier queries excepted), when a paraphrase fails the
paraphrase guard (Jaccard ≤ 0.2, no shared rare token, document frequencies of the corpus copy), when a temporal query's
filters do not contain the message time, when an identifier query has no identifier, or when tag_filtered tags are not
the memory's. The writer sees the current time so "last spring" can be resolved.

**L5 — Model-written queries.** "Oracle vs model-written filters" needs the main model's own search: the agent's
`DefaultModel` gets exactly the `history_search`/`memory_search` description and schema it sees in conversation
(`MemorySearchTool` now exposes them statically like `HistorySearchTool`) and writes one call per synthetic query.

**L6 — Pooling (judged@10).** For each query the union of the top `PoolDepth` = 10 of every configuration in the
workspace's `ablation.json` (reranked ones excluded), interleaved by rank, plus the gold source, capped at 60. Every
configuration the holdout can test therefore has judged@10 ≈ 1; reranked configurations are reported with their judged@10.

**L7 — Judge.** Batches of `JudgeBatchSize` = 10 candidates per call with short aliases (`c1`…; ids never reach the
model), a fixed 0–2 rubric, strict JSON `{"grades":{"c1":2,…}}` that must name every alias exactly once with grades in
0–2; invalid answers are asked again (`MaxAttempts` = 4, exponential backoff on failures, ×4 on HTTP 429, at most 2 min);
a batch that never validates is split in halves down to single items; a query whose judging still fails is retried by
the next run of the job.

**L8 — Finalisation rules (pre-declared).** A synthetic query whose source the judge found irrelevant is dropped; a real
request with no relevant item becomes an abstention query (`expectAbstain`); an abstention query with a relevant item,
an enumeration/multi query with fewer than two relevant items, a paraphrase failing the guard against its judged gold
and tag_filtered without tags become `lookup`. Grade-0 judgments are kept (judged@10). Rules are applied before any
system comparison and written into the analysis plan.

**L9 — Checkpoints and caps.** The job state (`label-state.json`: seeds by id, drafts, pools, grades, cumulative call
count) is sealed and saved every 5 drafts and after every judged query. `MaxCalls` (5,000 per job) is a hard cap across
resumed runs; the job prints its estimate first and refuses when the estimate exceeds the cap; at the cap it stops with
exit code 3 and the next run continues. `MinCallIntervalMs` paces calls.

**L10 — κ sample.** 75 items (`KappaSample`) split by query count (50 history, 25 memory), stratified by the judge's
grade (a third each of 0, 1, 2, topped up when one is scarce), one item per query while possible, deterministic and
shuffled by a seeded hash of (query, candidate) so the order carries no information about the judge's grade.
items (query text and rendered candidate) are stored in the sealed `kappa-sample.json`; the owner grades blind. When the
last item is graded, `judge-agreement.csv` (ids and grades) is written for the existing `kappa` command and κ is computed
over all 75 items — **the gate is κ ≥ 0.6 over the whole sample** (one judge, one rubric, one grader; 25 memory items
alone give too wide an interval to gate on); per-tool κ is reported.

**L11 — Grading access.** The owner enters the Bridge password once (`POST …/eval/grading/session`, `reauth` rate limit)
and gets a grading grant bound to the session cookie (15 minutes idle, 2 hours at most, in memory only). Items are
fetched one at a time with the grant (`X-Grading-Grant`), rate limited to 60/min (`eval-grading`), `Cache-Control:
no-store`, rendered with `x-text`; the judge's grade is never sent. An expired grant answers 403 (a 401 would log the
owner out of the web UI).

**L12 — The run.** `EvaluationRunService` (a hosted service) is a persisted state machine (`/app/state/eval/run/run.json`,
no text): prepare → label history → label memory → validate → guard → *grading* → κ → plan → ablate dev → select →
[rerank curve → select rerank] → register → holdout raw → [gate-r] → suggest → *dictionary* → freeze → holdout frozen →
compare → *passphrase* → export → summary. Commands run as jobs of `EvaluationJobService` (one at a time, shared with
manual jobs, same refusal rules). A failed command stops the run with the last lines of its output (ids and numbers)
and guidance; *Resume* continues the same step. After a restart the run continues once the storage key has arrived; an
interrupted command runs again (all are idempotent; `register` is skipped when already registered). The export
passphrase is never stored: after a restart in the export phase the run asks for it again.

**L13 — Pre-registration and selection.** Before the dev ablation the run writes each workspace's `analysis-plan.md`
(counts per category and stratum, dev/test sizes, models, pool depth, κ, finalisation rules, metrics, statistics,
decision rule and the **selection rule**). After `ablate --split dev` the rule chooses `holdout.config`: among the
configurations except the baseline, the `*-model-queries` ones and reranked ones, with dev judged@10 ≥ 0.8, the highest
lower bound of the paired 95 % CI of Δ nDCG@10 vs the baseline, ties by mean then name; margin 0.02. With the reranker
on, the rerank curve runs on dev with that base and the cell with the highest lower bound among cells with p95 ≤ 2.5 s
and a lower bound > 0 becomes a registered `…-rerank-<cell>` configuration for `gate-r`. Then `register`, then the
holdout: the test split is used once, after everything is fixed.

**L14 — Fresh start.** A run refuses to start over an earlier evaluation's dataset or registration unless the owner
ticks *Start fresh*, which deletes the workspaces' datasets, plans, registrations, labelling state, κ files and runs;
frozen versions, packages, the dictionary and the off-machine ledger stay.

**L15 — Dictionary approval.** The review reads `dictionary.suggested.jsonl` through the existing password-protected file
endpoint; the owner ticks terms, sets kinds and *same person as* groups, adds spellings; *Approve* sends them to the run,
which merges them into the sealed `dictionary.jsonl` (case- and diacritics-insensitive de-duplication) and freezes. A
refused freeze shows its findings (location, kind, count), runs `suggest --llm` again and asks again, at most 5 rounds.

**L16 — Results.** `summary.json` (ids and numbers) collects the raw and sanitised holdout verdicts, Gate R verdicts, κ,
the chosen configurations and the comparison report paths. The Recall page shows per tool the chosen configuration,
Δ nDCG@10 with its CI, the Gate H verdict (non-inferior), the sanitised result and the raw − sanitised difference, Gate
R, and links to every recorded report (served as text by `…/eval/run/reports/{key}`).

## 4. Architecture

```
Recall page (Alpine)                 Bridge                               Agent (holds the key)
 Evaluation card ── /eval/run/* ───▶ EvaluationRunEndpoints ── hub ──▶ EvaluationRunApi ─▶ EvaluationRunService (hosted)
 grading screen ── /eval/grading/* ▶ GradingGrants + endpoints ─ hub ─▶ GradingService          │ jobs
 dictionary review ─ /eval/files/read (existing)                                                ▼
                                                                         EvaluationJobService ─▶ history-eval / memory-eval / eval-corpus
                                                                                                   └─ label ─▶ LabelCli ─▶ Labeller
                                                                                                        ILabelSource: HistoryLabelSource (EvalCorpus copy)
                                                                                                                      MemoryLabelSource (MemoryEvalCorpus copy)
                                                                                                        LabellingLlm (pinned, capped) ─▶ DirectLlmClient
                                                                                                        QueryDrafter · MainModelQueryWriter · RelevanceJudge
                                                                                                        DatasetFinaliser · KappaSampler · LabellingStateStore
```

New code lives in `Evaluation/Labelling` (sampling, writing, judging, the pipeline, the `label` command) and
`Evaluation/Orchestration` (grading, analysis plan, the run's plan, service and hub API). Existing code changes are
small: `DirectLlmClient.ProviderFor`, `EvalCorpus.OpenCopy`, `MemorySearchTool` static description, the embedder
factories of the two CLIs, the `label` dispatch and DI in `EvaluationRegistration`, five `IEvaluationHub` methods, the
Bridge relay, one rate-limit policy and the Recall page card.

Files per workspace (new ones in bold): `dataset.jsonl` (sealed), **`label-state.json`** (sealed), **`kappa-sample.json`**
(sealed), **`label-report.json`** (counts), `analysis-plan.md` (now generated), `ablation.json` (holdout chosen by the
rule), `judge-agreement.csv` (generated), `preregistration.json`, `runs/`. Run files: **`eval/run/run.json`**,
**`eval/run/summary.json`**.

## 5. Security and privacy

- **D5.** Snippets, real requests with their four preceding turns, drafts and candidates go to the configured provider
  only, through the agent's existing client; the Bridge never sees them except the 75 grading items (decrypted on
  request, never cached) and dictionary suggestions (existing sealed-file endpoint).
- **At rest.** Everything with text is sealed with the evaluation file key (file name as associated data); the run state,
  reports, `label-report.json`, `judge-agreement.csv` and logs hold ids, counts, model ids and paths. Corpus copies are
  encrypted with the evaluation ring and deleted when the label job ends. The passphrase lives only in the export job.
- **Access.** Starting, resuming, approving and exporting need the Bridge password (`reauth`, 5/min); grading needs a
  session-bound grant; every route requires authorization.
- **D3.** Label copies never outlive the job; frozen versions and their D3 handling are unchanged; a purge during a run
  makes the next `freeze`/`holdout` fail with guidance (the owner starts fresh).
- **Logs.** `[LoggerMessage]` entries carry run id, phase, tool, command and exit code only; prompts and answers are
  never logged.

## 6. Owner workflow

1. Recall page → Evaluation → password → *Run evaluation* (*Start fresh* when asked).
2. Grade 75 items when asked (password once; keys 0/1/2, →, ←).
3. Approve dictionary terms when asked (repeat if a freeze is refused).
4. Enter the package passphrase twice when asked; download and attach the package to SafeExchange (runbook A6).
5. Read the results and decide Gate H.

## 7. Cost and time (defaults: 520 + 270 drafted queries)

| | History | Memory | Total |
|---|---|---|---|
| Query writer (`gpt-6.1-sol`, ~1.3 per query incl. rewrites) | ≈ 680 | ≈ 350 | ≈ 1,030 |
| Main model (synthetic queries only) | ≈ 365 | ≈ 190 | ≈ 550 (up to 790 with few real requests) |
| Judge (`claude-opus-5.5`, ~30 candidates per query, batches of 10, +10 % retries) | ≈ 1,720 | ≈ 890 | ≈ 2,600 (up to ~4,000 with 50–60 candidates) |
| **LLM calls** | ≈ 2,760 | ≈ 1,430 | **≈ 4,200** |
| LLM time (5 s writer, 4 s main, 15 s judge, 0.5 s pacing) | ≈ 8.9 h | ≈ 4.6 h | **≈ 13.5 h** |

Embedding: the label job embeds a copy per keyword variant (as `ablate` does); with the other commands about six
embedding passes of the corpus. Owner time: ~20 minutes of grading plus the dictionary review.

## 8. Testing

Unit tests with `FakeLabellingLlm` (scripted answers, failover simulation) for the client, drafter, main-model writer and
judge; `LabelSeeds`, `DatasetFinaliser`, `KappaSampler`, `AnalysisPlanWriter`, `ConfigSelector`, `RerankCellSelector`
and `EvaluationRunPlan` are pure. The pipeline runs against a fake source (cap, resume, sealing, counts-only output); the
label sources and `label` command run on real encrypted corpus copies with the fake embedders. `EvaluationRunService`
runs the whole protocol against fake commands (waits, κ gate, freeze loop, restart, cancel/resume, passphrase never
persisted). Bridge tests cover routes, rate-limit policies, password and grant checks.

## 9. Risks

- **Cost/time** dominated by the judge; mitigated by the estimate, the cap and resumability. Provider rate limits:
  pacing and backoff; a long outage stops the run (*Resume*).
- **κ < 0.6**: the run stops before any comparison; the owner switches the judge model and starts fresh.
- **Judge bias toward its own family's style**: mitigated by family separation and the κ check, not removed.
- **Writer leakage of wording**: the copy check is lexical; semantic copying is possible; the paraphrase category and
  the guard bound it where it matters.
- **Few real requests** on a mostly legacy history: the real stratum may be small; the plan states its size and the
  result is provisional (§4.2).
- **Pool bias**: systems outside the grid are not pooled; reranked configurations may have judged@10 < 1 (reported).
- **Memory size**: Gate H for memory needs ≥ 250 queries; a small memory store fails `validate` with guidance.
- **Model ids** must exist at the provider; the job checks this before the first call.

## 10. Rejected alternatives

- *One LLM for writing and judging*: violates §4.2.
- *Grading in CSV files or chat*: rejected by the owner; plaintext on disk.
- *κ gate per tool*: 25 memory items give a CI too wide to gate on with 75 items in total (reported instead).
- *Judging every candidate in its own call*: ~10× the calls.
- *Selecting the configuration by hand*: not mechanical, invites forking paths; the pre-declared rule replaces it.
- *Orchestrating from the Bridge*: the Bridge cannot read sealed files or hold the key; the agent already runs the jobs.

## 11. Coverage of the request

| Request | Where |
|---|---|
| `label` for history and memory, stratified sampling incl. enumeration/abstention/temporal | L2, L3; `LabelSeeds`, `LabelCli` |
| Real recall requests mined from past `history_search`/`memory_search` calls (co-primary) | L3; `RecallRequestMiner` |
| Writer drafts queries + gold_filters, never copying, paraphrase guard, regenerate | L4; `QueryDrafter`, `DraftChecks` |
| Pool union of keyword/dense/hybrid/reranked systems + gold, judged@10 | L6; `ILabelSource.PoolAsync` |
| Judge in batches, strict JSON, retries, backoff, resumable checkpoints | L7, L9; `RelevanceJudge`, `LabellingLlm`, `LabellingStateStore` |
| Sealed dataset, κ sample of 75 stratified by grade | L8, L10; `DatasetFinaliser`, `KappaSampler`, `KappaSampleFile` |
| Cost/time estimate up front, hard caps | L9, §7; `Labeller.Estimate`, `--estimate` |
| Privacy (provider only, nothing plaintext, ids-only logs) | §5 |
| Grading UI: one item, decrypted on demand, re-auth/session, rate limit, x-text, 0/1/2/skip/back, progress, keys, sealed grades, κ | L10, L11; `GradingService`, `GradingGrants`, `eval-run.js` |
| Orchestration state machine with waits, persistence, one run, cancel, Gate R optional | L12, L13; `EvaluationRunPlan`, `EvaluationRunService` |
| Pre-registration before holdout, selection only on dev, auto-written plan | L13; `AnalysisPlanWriter`, `ConfigSelector` |
| Dictionary approval and freeze loop | L15 |
| Passphrase export | L12; `Export` |
| Results page (verdicts, CIs, raw vs sanitised, κ, reports) | L16 |
| Runbook: ~5 owner bullets, manual path kept | §6; `docs/evaluation-runbook.md` |
| Models `gpt-6.1-sol` / `claude-opus-5.5` via the configured provider, configurable | L1 |
