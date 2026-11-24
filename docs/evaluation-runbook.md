# Evaluation runbook (Gate H, Gate R, sanitised corpus)

This is the owner's guide for the quality gates of episodic history and curated memory on an install
with **encryption at rest** (it works the same without encryption). Design:
[encrypted evaluation and sanitised corpus](superpowers/specs/2026-11-23-encrypted-eval-sanitised-corpus-design.md).

Everything runs **inside the agent process** as an evaluation job, because only that process holds the storage key.
You start jobs from Windows with `scripts\Invoke-CortexEval.ps1`; the Bridge asks for its password every time. Corpus
copies are encrypted with a dedicated evaluation key, datasets are sealed, and reports hold ids and numbers only.

| Script | What it does |
|---|---|
| `Invoke-CortexEval.ps1 -Tool <history-eval\|memory-eval\|eval-corpus> -- <command> [options]` | Runs a command as a job and prints its output. `-PromptPassphrase` for package export and import. |
| `Edit-CortexEvalFile.ps1 -Tool <tool> -Name <file>` | Opens a workspace file (dataset, plan, dictionary) in your editor and saves it back (sealed) if it changed. Use an editor that waits (`$env:EDITOR` or `-Editor "code --wait"`); if the editor returns at once the script asks you to press Enter once the file is saved and closed. The local copy is overwritten and deleted afterwards (kept only when the upload failed, so the edits are not lost). `-Pull -Path` / `-Push -Path` move it to and from an explicit file instead (a relative path is relative to the current PowerShell location). |
| `Export-CortexEvalCorpus.ps1 -Version vN` | Downloads the passphrase-encrypted package of a frozen version to a folder on this PC. |

Jobs refuse `--dir`, `--source`, `--labels`, `--embedder` and `--reranker`: they always use the configured workspaces
(`/app/state/eval/hybrid`, `/app/state/eval/memory`), stores and sidecars. One job runs at a time. Reports are plain
files in the container; read them with `docker exec cortex-agent cat <path printed by the job>`.

> Never paste dataset text, pools or seeds into chats, issues or commits. Files you `-Pull` to disk are plaintext:
> keep them under your user profile and delete them when you are done.

## Run the evaluation (one button)

Design: [automated labelling and one-click evaluation](superpowers/specs/2026-11-23-automated-labelling-design.md).
Your part is five steps; everything else runs inside the agent, resumable across restarts:

1. **Recall page → Evaluation → Bridge password → Run evaluation.** Tick *Start fresh* if an earlier evaluation
   exists (its datasets, registrations and runs are deleted; frozen versions and the dictionary stay).
2. **Grade the evaluation sample** when the page asks: enter the password once, then grade each item blind — **2**
   answers the query, **1** partly or as needed context, **0** not relevant (keys `0` `1` `2`, `→` skip, `←` back).
   75 items by default.
3. **Approve dictionary terms** when asked: tick the names, places, organisations and handles to replace, add missing
   spellings (*same person as* groups nicknames), *Approve and freeze*. If the leakage check still finds personal
   values, the page lists where (location, kind, count) and asks again.
4. **Package passphrase** when asked: at least 12 characters, twice (not the recovery kit's), *Export*. Then download the
   package and attach it to SafeExchange as in A6, steps 2–5.
5. **Read the results**: per tool the chosen configuration, Δ nDCG@10 with its 95 % CI, the Gate H verdict
   (non-inferior or not), the sanitised-copy result and the raw − sanitised difference, κ, Gate R when it ran, and the
   reports. Gate H is your decision.

What runs automatically, in order: workspaces → `label` (history, then memory: stratified samples of your messages and
memories plus your real past recall requests as the co-primary stratum; `Evaluation:Labelling:QueryModel` writes each
query and its oracle filters without copying the wording — paraphrase guard and copy check enforced, rewritten on
failure; the agent's main model writes its own version of the search; the union of the top 10 of every configuration
plus the source is pooled and `JudgeModel` grades it 0–2 in batches) → `validate` → `guard` → *your grading* (press *Done*: κ is computed and your grades are locked) → κ gate
(≥ 0.6 over the whole sample) → the analysis plan with the selection rule is written → `ablate --split dev` → the
configuration is chosen by that rule on dev only (`holdout` in `ablation.json`; with the reranker on, the rerank curve
and a Gate R configuration too) → `register` → `holdout` (raw) → `gate-r` → `eval-corpus suggest --llm` → *your
dictionary approval* → `freeze` → `holdout --corpus frozen:vN` → `compare` → *your passphrase* → `export` → summary.

| Setting (`Evaluation:Labelling:…`) | Default | |
|---|---|---|
| `QueryModel` / `JudgeModel` | `gpt-6.1-sol` / `claude-opus-5.5` | Different model families are required; both must be listed by a configured provider (the Bridge's `llmProviders`) |
| `JudgeRubric` | *(empty: the default rubric)* | The judge's grading guidance (text; the JSON answer format is always appended). Its id (`default` or `sha256:<12 hex>`) is recorded in the label state, `label-report.json` and the analysis plan; a label job refuses to resume with another rubric. See [Judge calibration](#judge-calibration) |
| `HistoryQueries` / `MemoryQueries` | 520 / 270 | Drafted; a few are dropped after judging (Gate H for memory needs ≥ 250) |
| `RealShare` | 0.3 | Largest share of real recall requests |
| `KappaSample` | 75 | Items you grade, split by query count (50 history, 25 memory) |
| `PoolDepth`, `JudgeBatchSize`, `MaxCandidatesPerQuery` | 10, 10, 60 | judged@10 coverage and judge batching |
| `MaxCalls`, `MinCallIntervalMs`, `MaxAttempts` | 5000, 500, 4 | Hard cap per label job, pacing, retries with backoff |

Set them like any agent option (environment `Evaluation__Labelling__MaxCalls=8000`, or the agent's configuration file).
`… -Tool history-eval -- label --estimate` prints the expected calls and hours. With the defaults a full run makes about 4,200 LLM
calls (≈ 1,030 query-writer, ≈ 550 main-model and ≈ 2,600 judge calls; more judge calls when the pools are larger, up to the cap)
and takes about 13–14 hours of LLM time plus the embedding runs: start it in the evening.

**When it stops** the page says why and what to do; *Resume* (password) continues from the same step:
- *κ below 0.6*: the judge disagrees with you too often. You can change grades until you press **Done**, which
  computes κ and locks them (no κ is shown before, so it cannot be tuned); below the gate the grading panel stays open
  read-only with κ and the guidance. Press *Review grades* to see the judge's grade next to yours and step through the
  disagreements, then calibrate the judge ([Judge calibration](#judge-calibration)), cancel the run and start fresh.
- *MaxCalls reached*: raise the cap and Resume; the label job continues where it stopped (its checkpoint is sealed).
- *The LLM provider kept failing* (rate limit, outage, a model the provider no longer lists; label exit 4): nothing is
  marked failed. Labelling calls never fail over to another provider or model (that would change the model family) and
  every attempt counts against `MaxCalls`. Check the provider in the Bridge, then Resume.
- *Some queries could not be judged yet* (label exit 5): the judge gave no valid answer; nothing is written until Resume
  has retried them (a query is given up after 3 runs and reported as `judgingFailed`).
- *Too few queries survived judging* (label exit 6; memory needs ≥ 250): the job already sampled extra seeds up to its
  top-up rounds; raise `MemoryQueries` (or add memories) and Resume.
- *A command failed*: the last lines of its output (ids and numbers) are shown; fix the cause and Resume. A label job
  that finds its saved progress was made with other models (query, judge or main model) or another judge rubric refuses: restore the
  configuration and Resume, or start a fresh run.
- After an agent restart the run continues by itself once the storage key has arrived; an interrupted command runs again
  (a job that needs the LLM waits up to 2 minutes for the Bridge's credentials first).
  Only the passphrase must be entered again.

Privacy: snippets, queries and candidates go only to the configured provider (D5), as in normal chat. Everything with
text on disk is sealed (`label-state.json`, `dataset.jsonl`, `kappa-sample.json`, the dictionary); the run state,
reports and logs hold ids and numbers only. Grading items are decrypted one at a time for a grading grant bound to your
session (15 minutes idle).

## Judge calibration

When the κ gate fails, the judge's notion of a grade differs from yours — usually by one step (0↔1, 1↔2), i.e. the
boundary of **1** is ambiguous. Calibrate the judge on the items you already graded, then measure it again on new ones:

1. **See the disagreements.** In the grading panel press *Review grades* (password): after the grades are locked each
   item shows `Judge: X · You: Y` (highlighted where you differ), and *◀ Prev disagreement* / *Next disagreement ▶*
   step through the items where you differ (the panel opens on the first). Note the pattern — never copy item text
   into chats, issues or rubric files.
2. **Write rubric variants.** A rubric is the grading guidance only (what 2, 1 and 0 mean; the JSON answer format is
   appended by the judge). Variants are plain-text files `/app/state/eval/calibration/rubrics/<name>.txt` (name:
   lowercase letters, digits and dashes; at most 8 KB). They are **not** sealed, so they must hold no personal data:
   invented examples only. Put one there with `docker exec cortex-agent mkdir -p /app/state/eval/calibration/rubrics`, then
   `docker cp .\v2.txt cortex-agent:/app/state/eval/calibration/rubrics/v2.txt`.
   `default` is the built-in rubric.
3. **Pilot judges × rubrics** on the calibration set (password prompt):
   ```powershell
   .\scripts\Invoke-CortexEval.ps1 -Tool judge-calibration -- calibrate --models claude-opus-5.5,claude-sonnet-5.5,grok-4.7 --rubrics default,v2
   ```
   Add `--estimate` first to print the items, combinations and LLM calls (items × models × rubrics; at most 6 models and
   4 rubrics; the whole calibration must fit `MaxCalls`, and every model must be listed by a provider — both are
   checked before any call). Each item is judged on its own (one call per item), so the result can differ from the
   run's batched judging; the row `stored judge (run)` shows the run's stored grades for comparison.
4. **Read the table.** One line per model × rubric, best κ first: κ (unweighted, exactly as the gate computes it),
   linear and quadratic weighted κ (one-step disagreements cost less), exact agreement, κ per tool, failed items and
   calls; then the confusion matrices (rows the judge's grade, columns yours) with how often the judge graded higher or
   lower than you. A JSON copy (ids and numbers only) is written to `/app/state/eval/calibration/reports/`.
   Choose the judge (model and rubric) with the highest κ on the calibration set. The judge must be of another model
   family than the query writer (`gpt-6.1-sol`): `gpt-*` models are informational only in the pilot.
5. **Start a fresh run with it.** Set `Evaluation:Labelling:JudgeModel` and, for a rubric other than `default`,
   `Evaluation:Labelling:JudgeRubric` (the file's text, e.g. a YAML block scalar in the agent configuration), restart
   the agent, then cancel the failed run and *Run evaluation* with *Start fresh*. A fresh run draws a **new** κ sample,
   so the gate is measured on items the judge was not tuned on.

Every locked κ sample — passing or failing — is archived when the gate sees it and again before a fresh start wipes
the workspaces: `/app/state/eval/calibration/sets/<runId>/<tool>/` holds the sealed `kappa-sample.json` and
`judge-agreement.csv` copied byte for byte. The calibration set is every archived set plus the current locked sample
unless it is already archived (each set is its own population: ids restart with every run, so nothing else is
deduplicated), so it grows with every run. A fresh run keeps the sets; every history purge (D3) removes all κ samples
automatically — the archived sets and the live `kappa-sample.json` / `judge-agreement.csv` of both workspaces —
since their items hold message and memory text and which messages they came from is not known (rubrics and reports
stay). A run waiting for your grading then has no items left to grade: cancel it and start a fresh run.

## Advanced: commands one by one

The run above chains these commands. Run them yourself to repeat one step or to label by hand.

### A1. History search — Gate H

1. **Workspace:** `.\scripts\Invoke-CortexEval.ps1 -Tool history-eval -- init`
2. **Queries and labels** (§4.2 protocol): `… -Tool history-eval -- label` writes and judges it automatically (see above); or write `dataset.jsonl` with your assistant —
   `.\scripts\Edit-CortexEvalFile.ps1 -Tool history-eval -Name dataset.jsonl -Pull -Path .\work\history-dataset.jsonl`,
   label (graded 0–2 per message, query and judge model families differ), then
   `-Push -Path .\work\history-dataset.jsonl`. Delete the local copy afterwards.
3. `… -Tool history-eval -- validate` until it reports 0 problems; `… -- guard` (paraphrase guard).
4. **κ:** double-label a sample by hand; push `judge-agreement.csv` (`messageId,llmGrade,humanGrade` per line) with
   `Edit-CortexEvalFile.ps1 -Name judge-agreement.csv -Push -Path .\judge-agreement.csv`; `… -- kappa` must report ≥ 0.6.
5. **Dev ablations:** `… -- ablate --split dev`; read the report; pick the configuration; write it into
   `ablation.json` (`holdout.config`, `holdout.margin`) and fill in `analysis-plan.md` (both via `Edit-CortexEvalFile.ps1`).
6. **Register:** `… -- register`. From now on the dataset, the plan and the grid are fixed.
7. **Holdout on raw data:** `… -- holdout`. This is the Gate H result (D6).
8. **Reranker (Gate R, optional):** `… -- rerank-curve --condition normal` (also `embedder-busy`, `cold`), then
   `… -- gate-r --config <name>`. The Bridge must have the reranker switched on (Recall page).

### A2. Curated memory — Gate H for memory

Same steps with `-Tool memory-eval`: `init`, `sample --count 300` (writes sealed `seeds.jsonl`; pull it to write
queries, delete the pulled copy), write `dataset.jsonl` (≥ 250 queries), `pool` (ids only, for the judge), labels,
`validate`, `guard`, `kappa`, `ablate --split dev`, `register`, `holdout`, optionally `rerank-curve` / `gate-r`.

### A3. The sanitiser dictionary

The sanitised copy replaces names, places, organisations and handles that are listed in the dictionary, plus every
e-mail, phone number, CPR number, IBAN, card number, GPS position, URL path, non-public URL host and number with
9 or more digits. Dates, times, prices and short numbers stay.

1. `.\scripts\Invoke-CortexEval.ps1 -Tool eval-corpus -- suggest` — proper-noun candidates from your history and
   memories. Add `--llm` to let the configured LLM provider propose names too (the text goes to that provider, as in
   normal chat; nothing else sees it). Suggestions are never applied automatically.
2. Pull `dictionary.suggested.jsonl`, review it, and write the accepted terms into `dictionary.jsonl`
   (`Edit-CortexEvalFile.ps1 -Tool eval-corpus -Name dictionary.jsonl`). One JSON object per line:
   ```json
   {"term":"PersonA","kind":"person"}
   {"term":"Persona","kind":"person","entity":"persona"}
   {"term":"TownB","kind":"place"}
   {"term":"example.org","kind":"public-host"}
   ```
   Kinds: `person`, `place`, `org`, `handle`, `other`, `public-host` (a URL host that is kept). `entity` groups
   spellings, nicknames and scripts of one person under one pseudonym (compared case- and diacritics-insensitively; a
   phone number gets one pseudonym in every notation, `+45 12 34 56 78` = `12345678`). Matching is case-insensitive, on whole words
   (a letter on either side blocks a match; digits and `_` do not, so `anna_cv.pdf` and `Mette2024` match) and also finds
   the term without diacritics. Inflection is matched like this, and nothing more:
   - terms of four or more letters: up to three trailing letters (`Mette` → `Mettes`);
   - terms of one to three letters: an optional `s` (`Ida` → `Idas`, `Bo` → `Bos`);
   - Cyrillic terms of three or more letters ending in a vowel, `й` or `ь`: also the term without that last letter plus
     up to two letters (`Анна` → `Анну`, `Анны`, `Анной`; `Маша` → `Машей`; `Дмитрий` → `Дмитрия`, `Дмитрием`);
   - Cyrillic terms of four or more letters ending in `е`/`о` plus a consonant: the mobile vowel is dropped and up to
     three letters follow (`Павел` → `Павла`, `Павлом`).
   Other stem changes (`Лев` → `Льва`, `Любовь` → `Любви`) and irregular forms are not found: add them as their own
   lines with the same `entity`. The leakage check uses the same rules, so a form the sanitiser missed is reported.

### A4. Freeze a version

`.\scripts\Invoke-CortexEval.ps1 -Tool eval-corpus -- freeze`

Both workspaces must be registered and unchanged, and every label must point to a message or memory that still
exists. The job builds `/app/state/eval/frozen/vN` (encrypted), then runs the **leakage check**. If it prints lines
like `leak: messages.Content dictionary-term ×3`, nothing was kept: add the missing spellings to the dictionary
(another `suggest` helps) and freeze again. When it passes: `Frozen vN: … leakage check passed …`.

When the dictionary grows later, re-check older versions: `… -- check --version vN`.

### A5. Raw vs sanitised comparison (§4.2)

1. `… -Tool history-eval -- holdout --corpus frozen:vN` (and `-Tool memory-eval -- holdout --corpus frozen:vN`).
2. `… -Tool eval-corpus -- compare --version vN` (add `--tool memory-eval` for memory). The report
   (`/app/state/eval/compare/*.md`) shows nDCG@10, recall@10 and MRR on both corpora, the per-query difference with
   a 95% CI, and both confidence intervals of the pre-registered comparison. Report both numbers and the difference
   with the gate result. **The gate decision uses the raw run.** Results on a mostly pre-event-log snapshot are
   provisional (re-run after ≥ 4 weeks of live capture).

### A6. Off-machine backup in SafeExchange

Packages are attached to the **existing secret `cortex-recovery-kit`** (it also holds the storage recovery kit). Never
create, list or read any other secret for this.

1. **Export:** `.\scripts\Invoke-CortexEval.ps1 -Tool eval-corpus -PromptPassphrase -- export --version vN`. Use a
   passphrase of at least 12 characters that differs from the recovery kit's; keep it with your other offline secrets.
   The job prints the package name `cortex-eval-corpus-vN.cxpkg`, its size and SHA-256.
2. **Download:** `.\scripts\Export-CortexEvalCorpus.ps1 -Version vN` (or the download button under Storage →
   Evaluation corpora). The script prints the SHA-256; it must match step 1.
3. **Upload** with your coding assistant, as a file of `cortex-recovery-kit` named exactly `cortex-eval-corpus-vN.cxpkg`:
   * up to a few MB: `file_upload_begin` (name `cortex-recovery-kit`, fileName as above), `file_upload_chunk`
     (≤ 256 KB per chunk, index 0, 1, …, SHA-256 of each chunk, the ticket of the previous chunk),
     `file_upload_complete` (SHA-256 of the whole file);
   * larger: ask for `file_transfer_info` (name `cortex-recovery-kit`, direction `upload`) and follow it with your
     own token (`az account get-access-token` with the scope it names): create the file content, upload 4 MB chunks
     with their hashes and the ticket, commit with the whole-file hash. Do not save the URLs or scopes it returns in
     any repository.
4. **Verify:** `secret_get cortex-recovery-kit` lists the attachment with its `contentId` and SHA-256; the hash must
   match. `audit_read cortex-recovery-kit` shows the upload.
5. **Record it:** `… -Tool eval-corpus -- uploaded --version vN --content-id <contentId> --sha256 <sha256>`.
6. Delete the host copy of the package once it is uploaded (it is encrypted, but there is no reason to keep it).

### A7. When history is deleted (D3)

A deletion whose period overlaps a frozen version destroys that version on this machine (its runs and package too).
The deletion confirmation says so and names the attachment, and the Recall page then shows
**Delete these off-machine evaluation copies**. To finish:

1. With your assistant: `secret_get cortex-recovery-kit`, find the file `cortex-eval-corpus-vN.cxpkg` (or the
   recorded contentId), `file_delete` it (`confirm: true`). Check with `secret_get` and `audit_read`.
2. On the Recall page, enter the Bridge password and press **I deleted it**.
3. Freeze a new version (A4); labels that pointed to deleted messages must be removed and the workspace registered
   again first (the freeze tells you how many). Versions are never renumbered.

To drop a version yourself: `… -Tool eval-corpus -- delete --version vN` (an exported or imported one is queued for
off-machine deletion the same way). The pending list, the version counter and a log of completed deletions are kept in
`/app/state/eval-ledger`, outside `eval/`, so erasing the evaluation artifacts never loses them.

### A8. Evaluating elsewhere (import)

On another Cortex install (it may be unencrypted): copy the package into the agent's packages folder,
`docker cp cortex-eval-corpus-vN.cxpkg cortex-agent:/app/state/eval/packages/`, then
`.\scripts\Invoke-CortexEval.ps1 -Tool eval-corpus -PromptPassphrase -- import --package cortex-eval-corpus-vN.cxpkg`.
The import checks every file against the package's hashes and registers the sanitised datasets. Run
`holdout --corpus frozen:vN` there. The pseudonym secret never leaves the original machine, so the copy cannot be
mapped back to real names. An import is refused while the version is still on the "delete it off-machine" list, and
when the package's corpus range overlaps a history deletion that already happened (the purged messages must not come
back from an old copy); delete such a package instead.

### A9. Without encryption at rest

The in-container commands keep working (`docker exec cortex-agent dotnet Cortex.Contained.Agent.Host.dll history-eval …`),
and so do the scripts. On an encrypted install, `docker exec` runs only `kappa` and the synthetic `history-smoke`
checks; everything else points you to `Invoke-CortexEval.ps1`.
