# Evaluation runbook (Gate H, Gate R, sanitised corpus)

This is the owner's step-by-step guide for the quality gates of episodic history and curated memory on an install
with **encryption at rest** (it works the same without encryption). Design:
[encrypted evaluation and sanitised corpus](superpowers/specs/2026-11-23-encrypted-eval-sanitised-corpus-design.md).

Everything runs **inside the agent process** as an evaluation job, because only that process holds the storage key.
You start jobs from Windows with `scripts\Invoke-CortexEval.ps1`; the Bridge asks for its password every time. Corpus
copies are encrypted with a dedicated evaluation key, datasets are sealed, and reports hold ids and numbers only.

| Script | What it does |
|---|---|
| `Invoke-CortexEval.ps1 -Tool <history-eval\|memory-eval\|eval-corpus> -- <command> [options]` | Runs a command as a job and prints its output. `-PromptPassphrase` for package export and import. |
| `Edit-CortexEvalFile.ps1 -Tool <tool> -Name <file>` | Opens a workspace file (dataset, plan, dictionary) in your editor and saves it back (sealed). `-Pull -Path` / `-Push -Path` move it to and from an explicit file instead. |
| `Export-CortexEvalCorpus.ps1 -Version vN` | Downloads the passphrase-encrypted package of a frozen version to a folder on this PC. |

Jobs refuse `--dir`, `--source`, `--labels`, `--embedder` and `--reranker`: they always use the configured workspaces
(`/app/state/eval/hybrid`, `/app/state/eval/memory`), stores and sidecars. One job runs at a time. Reports are plain
files in the container; read them with `docker exec cortex-agent cat <path printed by the job>`.

> Never paste dataset text, pools or seeds into chats, issues or commits. Files you `-Pull` to disk are plaintext:
> keep them under your user profile and delete them when you are done.

## 1. History search — Gate H

1. **Workspace:** `.\scripts\Invoke-CortexEval.ps1 -Tool history-eval -- init`
2. **Queries and labels** (§4.2 protocol): write `dataset.jsonl` with your assistant —
   `.\scripts\Edit-CortexEvalFile.ps1 -Tool history-eval -Name dataset.jsonl -Pull -Path .\work\history-dataset.jsonl`,
   label (graded 0–2 per message, query and judge model families differ), then
   `-Push -Path .\work\history-dataset.jsonl`. Delete the local copy afterwards.
3. `… -Tool history-eval -- validate` until it reports 0 problems; `… -- guard` (paraphrase guard).
4. **κ:** double-label a sample by hand; push `judge-agreement.csv` (`messageId,llmGrade,humanGrade` per line) with
   `Edit-CortexEvalFile.ps1 -Name judge-agreement.csv -Push`; `… -- kappa` must report ≥ 0.6.
5. **Dev ablations:** `… -- ablate --split dev`; read the report; pick the configuration; write it into
   `ablation.json` (`holdout.config`, `holdout.margin`) and fill in `analysis-plan.md` (both via `Edit-CortexEvalFile.ps1`).
6. **Register:** `… -- register`. From now on the dataset, the plan and the grid are fixed.
7. **Holdout on raw data:** `… -- holdout`. This is the Gate H result (D6).
8. **Reranker (Gate R, optional):** `… -- rerank-curve --condition normal` (also `embedder-busy`, `cold`), then
   `… -- gate-r --config <name>`. The Bridge must have the reranker switched on (Recall page).

## 2. Curated memory — Gate H for memory

Same steps with `-Tool memory-eval`: `init`, `sample --count 300` (writes sealed `seeds.jsonl`; pull it to write
queries, delete the pulled copy), write `dataset.jsonl` (≥ 250 queries), `pool` (ids only, for the judge), labels,
`validate`, `guard`, `kappa`, `ablate --split dev`, `register`, `holdout`, optionally `rerank-curve` / `gate-r`.

## 3. The sanitiser dictionary

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
   spellings, nicknames and scripts of one person under one pseudonym. Inflected forms (genitive *-s*, Russian case
   endings up to three letters) and spellings without diacritics are matched automatically.

## 4. Freeze a version

`.\scripts\Invoke-CortexEval.ps1 -Tool eval-corpus -- freeze`

Both workspaces must be registered and unchanged, and every label must point to a message or memory that still
exists. The job builds `/app/state/eval/frozen/vN` (encrypted), then runs the **leakage check**. If it prints lines
like `leak: messages.Content dictionary-term ×3`, nothing was kept: add the missing spellings to the dictionary
(another `suggest` helps) and freeze again. When it passes: `Frozen vN: … leakage check passed …`.

When the dictionary grows later, re-check older versions: `… -- check --version vN`.

## 5. Raw vs sanitised comparison (§4.2)

1. `… -Tool history-eval -- holdout --corpus frozen:vN` (and `-Tool memory-eval -- holdout --corpus frozen:vN`).
2. `… -Tool eval-corpus -- compare --version vN` (add `--tool memory-eval` for memory). The report
   (`/app/state/eval/compare/*.md`) shows nDCG@10, recall@10 and MRR on both corpora, the per-query difference with
   a 95% CI, and both confidence intervals of the pre-registered comparison. Report both numbers and the difference
   with the gate result. **The gate decision uses the raw run.** Results on a mostly pre-event-log snapshot are
   provisional (re-run after ≥ 4 weeks of live capture).

## 6. Off-machine backup in SafeExchange

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

## 7. When history is deleted (D3)

A deletion whose period overlaps a frozen version destroys that version on this machine (its runs and package too).
The deletion confirmation says so and names the attachment, and the Recall page then shows
**Delete these off-machine evaluation copies**. To finish:

1. With your assistant: `secret_get cortex-recovery-kit`, find the file `cortex-eval-corpus-vN.cxpkg` (or the
   recorded contentId), `file_delete` it (`confirm: true`). Check with `secret_get` and `audit_read`.
2. On the Recall page, enter the Bridge password and press **I deleted it**.
3. Freeze a new version (§4); labels that pointed to deleted messages must be removed and the workspace registered
   again first (the freeze tells you how many). Versions are never renumbered.

To drop a version yourself: `… -Tool eval-corpus -- delete --version vN` (an exported one is queued for off-machine
deletion the same way).

## 8. Evaluating elsewhere (import)

On another Cortex install (it may be unencrypted): copy the package into the agent's packages folder,
`docker cp cortex-eval-corpus-vN.cxpkg cortex-agent:/app/state/eval/packages/`, then
`.\scripts\Invoke-CortexEval.ps1 -Tool eval-corpus -PromptPassphrase -- import --package cortex-eval-corpus-vN.cxpkg`.
The import checks every file against the package's hashes and registers the sanitised datasets. Run
`holdout --corpus frozen:vN` there. The pseudonym secret never leaves the original machine, so the copy cannot be
mapped back to real names.

## 9. Without encryption at rest

The in-container commands keep working (`docker exec cortex-agent dotnet Cortex.Contained.Agent.Host.dll history-eval …`),
and so do the scripts. On an encrypted install, `docker exec` runs only `kappa` and the synthetic `history-smoke`
checks; everything else points you to `Invoke-CortexEval.ps1`.
