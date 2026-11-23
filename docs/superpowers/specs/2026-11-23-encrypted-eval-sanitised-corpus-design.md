# Evaluation on encrypted installs and the sanitised evaluation corpus — design

**Date:** 2026-11-23
**Status:** Approved for planning
**Builds on:** [episodic history memory master spec](2026-11-21-episodic-history-memory-design.md) (D3, D5, D6, §4.2),
[increment 3](2026-11-21-inc3-hybrid-history-search-design.md) (`history-eval`), [increment 4](2026-11-21-inc4-memory-shared-engine-design.md)
(`memory-eval`), [increment 5](2026-11-21-inc5-reranker-design.md) (`rerank-curve`, `gate-r`) and
[encryption at rest](2026-11-22-encryption-at-rest-complete-design.md).
**Plan:** [2026-11-23-encrypted-eval-sanitised-corpus.md](../plans/2026-11-23-encrypted-eval-sanitised-corpus.md)

## 1. Problem

Encryption at rest is on for the owner's install: `messages.db`, the history index, `memory.db` and the memory content
files are encrypted with SQLite3 Multiple Ciphers / AES-256-GCM under purpose keys derived from the tenant key. The
key lives only in the agent process (pushed by the Bridge). The evaluation CLIs (`history-eval`, `history-smoke`,
`memory-eval`) run as separate processes (`docker exec`), never hold the key, and would write plaintext copies of the
stores into their run directories, so `EvaluationGuard` makes them refuse to run. Gate H (history and memory) and
Gate R are blocked.

The master spec also asks for a **sanitised frozen copy** of the evaluation corpus as the off-machine backup
(D6, SafeExchange) and for every Gate H run to be reported on raw local data **and** on the sanitised copy, with the
difference (§4.2). D3 requires frozen snapshots that overlap a purged range to be destroyed and regenerated as a new
version.

## 2. Goals

1. Evaluations work on encrypted installs without plaintext copies: corpus and index copies are encrypted with a
   dedicated evaluation key, opened only in the agent process; D3 erasure still covers them; reports hold ids and
   numbers only (D6) and stay plaintext.
2. A deterministic sanitiser pseudonymises a corpus copy for off-machine backup, consistently across the corpus,
   the curated memories and the query datasets, so relevance labels stay valid. A fail-closed leakage check scans
   the result.
3. Frozen corpus versions (v1, v2, …) = sanitised corpus + datasets + analysis plans + registration hashes. A purge
   overlapping a version destroys the local copy and flags the off-machine copy for deletion.
4. One passphrase-encrypted export package per version, downloadable from the Bridge, uploaded by the owner as a
   **file attachment of the one SafeExchange secret `cortex-recovery-kit`**, and an import path to run the
   sanitised-copy evaluation elsewhere.
5. The raw-vs-sanitised comparison report, owner-run, with a runbook.

**Non-goals:** automating SafeExchange from the agent (the container cannot reach it; the owner's coding assistant
does the upload and deletion); evaluating `history-smoke` on encrypted installs (superseded by `history-eval`);
timestamp shifting (§4.4); a Bridge UI for labelling (files are edited with a script).

## 3. Decisions

| ID | Decision | Why |
|---|---|---|
| E1 | **Evaluation jobs run inside the agent process.** The Bridge starts `history-eval`, `memory-eval` and the new `eval-corpus` as background jobs over the hub (one at a time, output polled). `scripts/Invoke-CortexEval.ps1` is the owner's entry point. The in-container CLIs keep working on unencrypted installs; on encrypted installs the copying commands refuse with a pointer to the script, while store-free commands (`kappa`, `history-smoke embedder-check|reranker-check`) run. | Only the agent process holds the tenant key. Handing purpose keys to a second process (local socket, file) would widen the key's exposure to everything that can `docker exec` or reach loopback, which includes the agent's own `run_command` tool. The hub is already authenticated (hub token, loopback rejected) and the Bridge re-checks the owner's password for every job start. |
| E2 | **Evaluation key ring = `StorageKeyRing.Derive("eval")`**: a ring whose master key is HKDF-SHA256(tenant key, `cortex-storage/eval`). Copies reuse the stores' purpose labels under it (`messages.db`, `history-index`, `memory.db`, `memory-content`) plus `eval-files` for sealed files. | Evaluation copies never share a key with live stores (a leaked copy password does not open live data), while all existing encryption plumbing (`MessageStoreEncryption`, `HistoryIndexGenerations`, `SqliteVecMemoryStore` with a password and a content cipher) works unchanged. Losing the tenant key loses the copies too — they are disposable. |
| E3 | **Copies are written with `VACUUM main INTO 'file:…?key=…'`** (`EncryptedSqlite.CopyDatabase`), from a read-only connection on the live store. An empty key yields a plaintext target (SQLite3MC otherwise gives an unkeyed target the source key). | The encrypted pages are produced directly; no plaintext form of the copy ever reaches the disk. The same mechanism already migrates live stores. |
| E4 | **Sealed workspace files.** Files that carry personal text — `dataset.jsonl` (history and memory), `seeds.jsonl`, the sanitiser dictionary and its suggestions — are AES-256-GCM sealed (`StorageFileCipher` format `CXE1`) with the `eval-files` key; the associated data binds the file name. Encrypted mode refuses plaintext copies of those files (fail closed) and plaintext mode refuses sealed ones. Analysis plans, ablation plans, registrations, manifests, reports, verdicts and pools (ids only) stay plaintext. | Datasets hold query text with names; on an encrypted install they must not sit in plaintext on the state volume. Binding the name stops a sealed seeds file from being passed off as a dataset. |
| E5 | **Owner-authored files are read and written through the Bridge** (`/eval/files/read|write`, password again, rate limited; `scripts/Edit-CortexEvalFile.ps1`). Identical content is not rewritten. | Sealed files cannot be edited inside the container. Not rewriting identical content keeps pre-registration hashes valid (they hash the sealed bytes). The editing copy on the owner's Windows host is the one remaining plaintext window; the script keeps it in a private folder and deletes it. |
| E6 | **Sanitiser = deterministic detectors + owner dictionary; the LLM only proposes dictionary entries.** Detectors in priority order: URLs; the personal-identifier detectors of `SensitiveDataRedactor` (IBAN, Luhn cards, CPR, email, GPS, Danish and generic phones — one implementation shared with the persistence redactor via `FindPersonalIdentifiers`); `@handles`; owner dictionary terms (names, places, organisations; case-insensitive, whole word, up to three inflection letters, folded spelling matched too); numbers of 9+ digits. The dictionary (`dictionary.jsonl`, sealed) is seeded by `eval-corpus suggest`: proper-noun heuristics over the corpus and the curated memories, plus — with `--llm` — named entities proposed by the configured LLM provider (D5). Suggestions are never applied automatically. | Reproducible: the same inputs and secret produce the same corpus, which the registration hashes and the comparison depend on; auditable: the owner sees and owns every term. An LLM pass as the transform itself would be non-deterministic, unverifiable and silently incomplete; as a dictionary generator it raises recall while the owner keeps control. Names are the hardest class (no pattern), so the dictionary plus the fail-closed check (E9) is the safety net. |
| E7 | **Pseudonyms = HMAC-SHA256 under a per-version secret** (32 random bytes, sealed in the version, never exported): single-token, readable class prefixes (`Person…`, `Place…`, `Org…`, `user…`, `mail…@example.invalid`, `host….example`, `phone…`, `cpr…`, `iban…`, `card…`, `gps…`, `p…` for URL paths); long numbers become digits of the same length. Variants of one entity share a pseudonym through the dictionary's `entity` field. A pseudonym collision fails the freeze. | Consistency keeps labels valid (the same name in a query and a message maps to the same token). Per-version secrets make versions unlinkable; without the local secret nobody can confirm a guess. Single tokens keep keyword statistics close to the raw corpus. |
| E8 | **What is kept:** message ids, memory ids, roles, categories, timestamps and every date, time, price and short number in text; tool names. **What changes:** text, channel and user ids, tool-call argument previews, memory titles, tags and content, query text, model-written query text, oracle and model-written channel filters, tag filters. **What is dropped:** external message ids (Discord snowflakes as row metadata), events, journals, every other table, vectors (memories are re-embedded from sanitised text). | Temporal queries, gold filters and the optional recency prior depend on exact time; shifting would add an artefact to the raw-vs-sanitised difference that §4.2 wants measured. Dates are weak identifiers once names, places and ids are gone, and the package is passphrase-encrypted inside an audited secret. Vectors of raw text could be inverted, so they never leave. |
| E9 | **Leakage check, fail closed.** Every sanitised text (message content, channel and user ids, tool-call summaries, memories, datasets, plans) is normalised (NFKC, zero-width removed) and scanned for: dictionary terms on folded text (case and diacritics removed), the identifier detectors, 9+ digit runs, `@handles` and URL hosts that are neither public nor pseudonyms; produced pseudonyms are exempt. One finding deletes the half-built version; the report lists location, kind and count only. `eval-corpus check --version vN` re-scans an existing version for dictionary terms added later. | The scanner sees spelling variants the sanitiser missed (folded matching) and obfuscation (normalisation), so the owner learns exactly which class leaked and where, without the text being printed. |
| E10 | **Frozen version** `/app/state/eval/frozen/vN/`: `manifest.json` (plaintext: range, counts, SHA-256 of every sanitised file's plaintext and of the sanitised rows, the raw registrations), `secret.key` (sealed), `messages.db` (only `Messages`, evaluation key), `memories.jsonl` (sealed), `history/` and `memory/` workspaces (sealed sanitised dataset, unchanged plans, a fresh registration). Freezing requires registered, unchanged workspaces and judgments that reference existing ids. Version numbers are never reused. | A version is a self-contained, pre-registered evaluation: `history-eval|memory-eval … --corpus frozen:vN` runs on it with its own workspace. Requiring registration keeps the sanitised run inside the pre-registered plan. |
| E11 | **Package** `cortex-eval-corpus-vN.cxpkg`: header `CXPKG1` + JSON (KDF PBKDF2-HMAC-SHA256 600,000 iterations, salt, chunk size, nonce prefix), then a tar archive in a chunked AEAD stream (AES-256-GCM, 1 MiB chunks, nonce = prefix ‖ counter ‖ last-flag, header hash as associated data). `messages.db` travels encrypted with a random key stored in the package manifest; the secret is not included. Import verifies every file against the manifest hashes, re-seals for the local install and registers again. | One file, one passphrase, safe on any disk; truncation, reordering, appending and any modified byte fail. The KDF is the recovery kit's (shared `PassphraseKdf`). No plaintext on export or import. |
| E12 | **SafeExchange, one secret only:** packages are file attachments of the existing secret `cortex-recovery-kit` (audited). Upload in-band (`file_upload_begin/chunk/complete`, ≤ 256 KB chunks) for packages of a few MB, otherwise over REST with the owner's own token as `file_transfer_info` describes. The owner records `contentId` and SHA-256 (`eval-corpus uploaded`). Deletion = `secret_get cortex-recovery-kit` (metadata: file names, contentIds) then `file_delete`. No procedure creates, lists or reads any other secret. | Owner constraint. `secret_get` returns metadata only, never the kit's text. |
| E13 | **D3:** the `smoke-artifacts` erasure step also destroys every frozen version whose range overlaps the purge (and every incomplete version directory), with its runs and package; an exported version enters `offsite-deletions.json`. The purge preview lists affected versions and the confirmation names the attachment to delete; the Recall page shows pending off-machine deletions until the owner confirms (password again). Regeneration is the owner's `eval-corpus freeze` (new version, new secret). | The agent cannot reach SafeExchange, so the off-machine copy is the owner's task; the UI keeps it visible until done. Automatic regeneration would need fresh labels for the purged ids and an export passphrase. |
| E14 | **Comparison report:** `eval-corpus compare --version vN [--tool …]` pairs the newest raw holdout run with the newest sanitised one: per configuration nDCG@10, recall@10 and MRR of both and the paired per-query nDCG@10 difference (sanitised − raw) with a bootstrap 95% CI, and both CIs of every pre-registered comparison. Gate decisions use the raw run (D6). | §4.2 "run on raw local data and on the sanitised copy, report the difference". |

## 4. Architecture

```
 Owner (PowerShell / coding assistant)                     Bridge (Windows)                     Agent process (Docker)
 ─────────────────────────────────────                     ────────────────                     ─────────────────────────────────────────────
 Invoke-CortexEval.ps1 ──login, password──► POST /eval/jobs ─────hub──► EvaluationJobService ── EvalProtection.Encrypted(tenant ring)
                         ◄──poll output──── GET /eval/jobs/{id}                │                     ├─ ring "eval" = HKDF(tenant key)
 Edit-CortexEvalFile.ps1 ──password──────► /eval/files/read|write ──hub──► EvaluationFiles       │   ├─ copies: VACUUM INTO keyed target
                                                                              │                  │   └─ sealed files: CXE1, AD = name
                                                                              ├─ history-eval ── EvalCorpus (encrypted copy + indexes)
                                                                              ├─ memory-eval ─── MemoryEvalCorpus (encrypted copy | snapshot)
                                                                              └─ eval-corpus ─── suggest │ freeze │ check │ export │ import │ compare
                                                                                                    │
 Export-CortexEvalCorpus.ps1 ─password──► /eval/frozen/packages/{name} ◄─chunks── packages/*.cxpkg  (passphrase-encrypted)
        │
        └─► SafeExchange secret "cortex-recovery-kit": file cortex-eval-corpus-vN.cxpkg  (assistant: upload / file_delete)

 Purge ─► SmokeArtifactErasureStep ─► FrozenCorpusStore.Destroy(vN) ─► offsite-deletions.json ─► Recall banner ─► owner deletes, confirms
```

### 4.1 Encrypted-workspace mode

`EvalProtection` carries the source ring (live tenant ring, or the evaluation ring for a frozen source) and the copy
ring (evaluation ring). `EvalCorpus`, `MemoryEvalCorpus`, `EvalWorkspace` and `MemoryEvalWorkspace` take it; the CLIs
take it as an optional argument, defaulting to plaintext, so unencrypted installs and the in-container CLI behave as
before. Run directories keep `manifest.json` (corpus range) for D3; `MemoryEvalCorpus` still deletes its copy on
disposal and sweeps leftovers.

### 4.2 Jobs and files

`EvaluationJobService` keeps the last 20 jobs, runs one at a time, captures at most 64 KiB of output, refuses
`--dir`, `--source`, `--labels`, `--embedder` and `--reranker` (jobs use the configured workspaces, stores and
endpoints), refuses when the key has not arrived, never echoes options or the passphrase, and reports only the
exception type of a crash. The reranker key comes from the Bridge grant (`RerankerAccess`), the embedder from the
agent's memory options.

`EvaluationFiles` exposes an allow-list per tool: `history-eval` and `memory-eval` datasets, plans, ablation plans,
judge-agreement CSVs (read: also registrations, examples, seeds, pools, the generation guide) and `eval-corpus`
`dictionary.jsonl` (read: also `dictionary.suggested.jsonl`).

### 4.3 Sanitiser, lexicon, leakage check

See E6–E9. Text is NFKC-normalised and zero-width characters removed before detection. URL handling keeps the scheme,
keeps the host only when the dictionary lists it as `public-host`, and replaces path, query and fragment with one
pseudonym. All-digit mentions (`<@123…>`) are numbers, not handles. Tool-call summaries keep tool names (history
labels depend on them) and sanitise their 32-character argument previews.

### 4.4 Rejected alternatives

* **Plaintext copies inside the agent with deletion afterwards** — still a plaintext window on disk (crashes, power
  loss), and `history-eval` run directories live for days.
* **Key hand-off to the CLI process** — see E1.
* **LLM rewriting the corpus** — non-deterministic, unverifiable, may drop or invent text (D5 permits processing,
  not this risk to the measurement).
* **Shifting timestamps by a secret offset** — distorts temporal filters and the recency prior; would need the offset
  in the package anyway to evaluate elsewhere.
* **Separate SafeExchange secret per version** — excluded by the owner (one secret only).

## 5. Security and privacy

| Asset | Where | Protection |
|---|---|---|
| Raw stores | `/app/state` | Tenant key (unchanged). |
| Evaluation copies (runs, frozen `messages.db`, memory copies) | `/app/state/eval/**` | Evaluation ring; D3 erasure; memory copies deleted after each command. |
| Sealed datasets, seeds, dictionary, suggestions, version secret | `/app/state/eval/**` | AES-256-GCM (`eval-files`, name-bound). |
| Reports, verdicts, manifests, comparisons | `/app/state/eval/**` | Plaintext by design: ids, numbers, hashes (D6). |
| Export package | agent `packages/`, owner host, SafeExchange attachment | Passphrase (PBKDF2 600k, AES-256-GCM STREAM); sanitised content only; no secret. |
| File edits | owner host, transient | Script folder under the user profile, deleted after the editor closes (or explicit `-Pull/-Push` path the owner deletes). |
| LLM suggestions | configured provider | Raw text leaves under D5 only with `--llm`; output is a sealed suggestion list. |

Threats considered: a stolen Bridge session (every content-bearing endpoint re-checks the password, rate limited 5/min;
the job API refuses path and endpoint options); a prompt-injected agent tool (`run_command` cannot reach the hub —
loopback rejected — and the in-container CLI refuses to copy encrypted stores); a stolen package (passphrase); a
leaked package passphrase (sanitised content; pseudonyms unlinkable without the local secret; dates and numbers
remain — accepted); a dictionary gap (fail-closed check catches folded variants of known terms; unknown names are the
residual risk, reduced by `suggest`).

## 6. Owner workflow (summary; full runbook in `docs/evaluation-runbook.md`)

1. Label as before, through `Edit-CortexEvalFile.ps1` (datasets are sealed); run `history-eval|memory-eval` commands
   with `Invoke-CortexEval.ps1`; `kappa`, `register` as before.
2. Build the dictionary: `eval-corpus suggest [--llm]`, review, edit `dictionary.jsonl`.
3. `eval-corpus freeze` (repeat after adding terms until the leakage check passes).
4. `history-eval holdout` and `history-eval holdout --corpus frozen:vN` (same for memory), then
   `eval-corpus compare --version vN`.
5. `eval-corpus export --version vN` (passphrase), `Export-CortexEvalCorpus.ps1 -Version vN`, attach to
   `cortex-recovery-kit`, `eval-corpus uploaded --version vN --content-id … --sha256 …`.
6. After a purge that names a version: delete the attachment (`secret_get` → `file_delete`), confirm on the Recall
   page, freeze a new version.

## 7. Testing

Unit and integration tests per component with synthetic text and the fixed test key: encrypted copies contain no
marker text and do not open with the live key; sealed files round-trip and fail closed; jobs refuse options and run
encrypted; endpoints re-authenticate and are rate limited; the sanitiser is consistent and keeps dates; the leakage
check finds every class without printing text; freezing produces an encrypted, registered version and fails closed;
packages round-trip, reject tampering and wrong passphrases; frozen runs execute holdouts; the comparison report is
produced; D3 destroys overlapping versions and queues exported ones; the purge confirmation names the attachment.

## 8. Risks

* **Unknown names** in the corpus that no dictionary entry or suggestion covers stay in the sanitised copy (the check
  only knows listed terms and patterns). Mitigation: `suggest --llm`, owner review, `check` after the dictionary grows,
  package passphrase and audited secret as the outer layers.
* **Over-sanitisation** (common words that are also names) changes keyword statistics; it shows up in the comparison
  report — which is its purpose.
* **Agent load:** evaluation jobs run in the agent process (CPU, memory, embedder); one job at a time; the owner runs
  them at quiet times. A crash of the agent kills the job; memory copies are swept on the next run.
* **Browser download** of very large packages holds them in memory; `Export-CortexEvalCorpus.ps1` streams to disk.
* **Package passphrase** is the owner's; losing it loses the off-machine copy (the local version remains).

## 9. Coverage of the request

| Requirement | Where |
|---|---|
| Evaluations on encrypted installs, no plaintext copies, evaluation purpose key, opened only in the agent process | E1–E4, §4.1–4.2; plan tasks 1–9 |
| CLIs replace the refusal with encrypted-workspace mode | E1, plan task 8 |
| D3 still covers evaluation copies | §4.1, E13; plan tasks 3, 19 |
| Reports ids and numbers only, plaintext | E4 |
| Sanitiser: names, places, organisations, emails, phones, CPR/IBAN/cards, URLs, long ids, GPS, handles | E6–E8; plan tasks 10–11 |
| Consistent keyed mapping per version | E7 |
| Dates and numbers policy | E8 |
| Dictionary seeded from memories, optional LLM entity detection | E6; plan task 14 |
| Leakage check, fail closed | E9; plan task 12 |
| Datasets sanitised consistently | E8, E10; plan task 13 |
| Frozen versions with plans and registration hashes | E10; plan task 13 |
| Purge destroys the local copy, marks the off-machine copy, UI and confirmation | E13; plan tasks 19–20 |
| Passphrase-encrypted package reusing the recovery-kit primitives, chunked AEAD | E11; plan tasks 15–16 |
| Download from the Bridge with re-authentication and rate limit, or host folder | E11, plan task 20 (`Export-CortexEvalCorpus.ps1`) |
| SafeExchange procedure with the single secret `cortex-recovery-kit` | E12; plan task 21 (runbook) |
| Import / restore to evaluate elsewhere | E11; plan tasks 16–18 |
| Raw vs sanitised comparison report and runbook | E14; plan tasks 17–18, 21 |
