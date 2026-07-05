# HANDOFF — Feature 4: coda relay simplify + coda-source setting

**Parked:** 2026-07-05
**Branch:** `feat/coda-relay-simplify` (in `C:\Users\yurio\Documents\github\cortex-agent`)
**Method:** Superpowers Subagent-Driven Development (SDD)
**Spec:** `docs/superpowers/specs/2026-07-05-coda-relay-simplify-and-source-setting-design.md`
**Plan:** `docs/superpowers/plans/2026-07-05-coda-relay-simplify-and-source-setting.md` (7 tasks)
**Ledger:** `.superpowers/sdd/progress.md` (source of truth for what's done)

## What this feature does
Two cleanups to the cortex↔coda coding relay, enabled by coda now being single-provider:
- **Part A (done):** removed the Bridge's dead coda provider/model resolution machinery + `--provider` arg + Settings→Coding provider/model card.
- **Part B (in progress):** add a `CodaSource` setting (`Auto | Host | Bundled`, default Auto) — choose which coda binary the Bridge launches (host `coda` on PATH vs the bundled `coda/coda.exe`), runtime-mutable, with a Settings→Coding UI card showing selected source + resolved path + `coda --version` + bundle-present.

## Status: Tasks 1–5 COMPLETE, 6–7 + finalize REMAINING

| Task | State | Commits |
|------|-------|---------|
| 1. `CodaSource` enum + pure `CodaBinaryResolver` | ✅ | ..36a889e |
| 2. `CodaSourceStore` (runtime-mutable, `%APPDATA%\Cortex\coda-source.json`) | ✅ | ..d800665 |
| 3. Remove provider/model resolution machinery | ✅ | ..926e1ee |
| 4. `CodaOptions.Source` + resolve binary at spawn (`EffectiveOptions`) | ✅ | f555637, be46ac6 |
| 5. REST `GET/PUT /api/coding/coda-source` + version probe | ✅ | e382332, 38f1710 |
| 6. Web UI: replace provider/model card with coda-source card | ⬜ TODO | — |
| 7. Full verification + docs | ⬜ TODO | — |
| Final: whole-branch review → merge → rebuild + MSIX → verify | ⬜ TODO | — |

**HEAD = `38f1710`.** Working tree clean except `version.json` (pre-existing, unrelated — do NOT commit) and untracked `tests/Cortex.Contained.Evals/eval-results/*.json` (artifacts — ignore).

## Key implementation facts (so Task 6 knows the API contract)
- **Endpoint (Task 5):** `GET /api/coding/coda-source` → `{ source, resolvedPath, version, bundlePresent }` (camelCase). `PUT /api/coding/coda-source` body `{ source: "auto"|"host"|"bundled" }` (case-insensitive) → persists + returns the freshly-resolved same shape. Both `.RequireAuthorization()`. Route is **NOT tenant-scoped** (single setting per Bridge, mirroring `CodingMcpEndpoints` at `/api/coding/mcp-settings`).
  - `source` is lowercase (`"auto"`/`"host"`/`"bundled"`). `version` is null when the probe fails ("unknown" in UI). `bundlePresent` = does `<BaseDir>/coda/coda.exe` exist.
- File: `src/Cortex.Contained.Bridge/Coding/CodingCodaSourceEndpoints.cs` (DTO `CodaSourceState`, `ParseSource`, `BuildStateAsync`, `TryProbeVersionAsync`).
- `CodaSource` enum: `src/Cortex.Contained.Contracts/Coding/CodaSource.cs`.
- Binary resolution (pure): `CodaBinaryResolver.Resolve(source, baseDir, fileExists)` → `(Path, FellBackFromBundle)`. Host→`"coda"`; Bundled→bundle if exists else `"coda"`+fellBack; Auto→bundle if present else `"coda"`.
- Spawn wiring: `CodaSessionManager.EffectiveOptions()` — an explicitly-pinned `CodaBinaryPath` (non-`"coda"`) WINS over source resolution (escape hatch). Logs 9304 (bundled-missing warn), 9305 (resolved source+path), 9306 (pinned path).

## Task 6 — Web UI (the immediate next task)
- **Files:** `src/Cortex.Contained.Bridge/wwwroot/app.html` + `wwwroot/js/pages/global-settings.js`.
- **Do:** In the Settings→Coding area, replace the (already-removed-server-side) provider/model card with a coda-source card: a 3-way selector (Auto / Host / Built-in), a read-only line showing **resolved path** + **detected version** + "built-in available: yes/no", and a Save that PUTs `{source}` then re-renders from the returned state. On load, GET `/api/coding/coda-source` and populate.
- **NOTE / likely cleanup:** Part A (Task 3) removed the server-side provider/model endpoints, but the Task-3 scope may have left the **provider/model card HTML+JS still in the UI** (Task 3 was server-only in some passes). Task 6 must remove any dead provider/model picker markup + its load/save/list-models JS, and put the coda-source card in its place. Grep `app.html`/`global-settings.js` for `provider`, `model`, `coding` to find it.
- Get the exact task text: `bash "<SDD skill>/scripts/task-brief" docs/superpowers/plans/2026-07-05-coda-relay-simplify-and-source-setting.md 6`
- Mirror the existing MCP-policy card's fetch/render/save JS in `global-settings.js` for consistency.

## Task 7 — verification + docs
- Full Bridge test suite green: `dotnet test tests/Cortex.Contained.Bridge.Tests` (trust the Passed/Failed SUMMARY line, NOT the wrapper exit code).
- Docs: remove `Coding:Coda:Provider`/`:Model` from any sample config/docs; document `Coding:Coda:Source` + the coda-source card. Check `docs/mcp-plugin-system.md`, `docs/setup-guide.md`, any `cortex.yml` sample.
- Task-brief: `... task-brief <plan> 7`.

## Finalize
1. Whole-branch review (opus / most capable): `bash "<SDD skill>/scripts/review-package" $(git merge-base main HEAD) HEAD` → dispatch final code-reviewer with the printed path. One fix subagent for all Critical/Important findings (batch, not per-finding). Minors from ledger are triaged here.
2. `superpowers:finishing-a-development-branch` → merge `feat/coda-relay-simplify` → `main`.
3. Rebuild + install: `.\scripts\Build-All.ps1` (bundles coda to `<out>/Bridge/coda/coda.exe` — verified working), then `Add-AppxPackage -ForceUpdateFromAnyVersion -ForceApplicationShutdown` the MSIX, relaunch.
4. Live verify: `/health` OK; Settings→Coding coda-source card shows resolved path + version; a coding session runs on GitHub Copilot (coda self-resolves its single connected provider — no `--provider` passed anymore).

## Environment gotchas (learned this session)
- **Subagent implementers hang on background full-test runs.** Do implementation inline yourself, or dispatch subagents ONLY for read-only reviews (tell them NOT to run tests; hand them the review-package diff + test evidence). This session did Tasks 4–5 inline + sonnet read-only reviewers.
- **Trust the `Passed!/Failed!` summary line, not the wrapper exit code.** A `dotnet test` build-warning-as-error (e.g. CA1869) shows EXIT=1 with the real reason only in the error line.
- **`TreatWarningsAsErrors` is on globally** — e.g. CA1869 (cache `JsonSerializerOptions` in a `static readonly` field, don't `new` it inline).
- **Test filter syntax:** `--filter "FullyQualifiedName~X|FullyQualifiedName~Y"` (tilde, NOT `ClassName=`).
- **Do NOT pipe `dotnet test` through `grep | head`** — it buffers and can yield a 0-byte log; redirect full output to a file and grep the file.
- Style (CLAUDE.md): `this.` on instance members, braces on all blocks, `static readonly` fields camelCase no prefix, source-generated `[LoggerMessage]`, one type per file (endpoints files mirror `CodingMcpEndpoints` and hold DTO+class+request together — accepted).
- SDD scripts live at: `C:\Users\yurio\.claude\plugins\cache\claude-plugins-official\superpowers\6.1.1\skills\subagent-driven-development\scripts\` (`task-brief`, `review-package`).

## Resume prompt
See the message that accompanied this file (or paste the "RESUME PROMPT" block below into a fresh session).
