# Coda Relay: Drop Provider/Model Resolution + Add Coda-Source Setting — Design

**Date:** 2026-07-05
**Repo:** cortex-agent (Bridge)
**Status:** Approved (brainstorming complete)

## Problem

Two related cleanups to the cortex↔coda coding relay, enabled by coda now being
**single-provider** (it self-resolves its one connected provider — see
`docs/… / coda single-provider` and coda PR #7):

1. **The Bridge's coda provider/model resolution is now dead weight.** The Bridge resolves a
   provider (UI store → `cortex.yml` → `~/.coda/settings.json`) and passes `--provider` to
   `coda serve`. coda now ignores `defaultProvider` and uses its connected credential, and the
   Bridge never passed `--model` anyway. So the whole `CodaModelSettings*` /
   `CodaMachineSettingsReader` machinery, the `--provider` arg, and the Settings→Coding
   provider/model card are redundant.

2. **No first-class control over which coda binary runs.** The MSIX bundles a "built-in"
   `coda/coda.exe` (verified present at `<install>/Bridge/coda/coda.exe` in 0.2.300) and
   `Program.cs` prefers it (`CodaOptions.ResolveDefaultBinaryPath` → bundled-if-present else host
   `coda` on PATH). This resolution is implicit and invisible in the UI. Users want to explicitly
   choose host vs built-in and see which is active (e.g. to update coda via `dotnet tool update`
   without rebuilding cortex).

## Approved decisions

1. **Part A — remove** the provider/model resolution machinery (including the model half; the
   UI card, endpoints, config keys). Retired `cortex.yml` keys are **ignored if present** (no
   migration).
2. **Part B — add** a `CodaSource` setting (`Auto | Host | Bundled`, default **Auto**),
   runtime-mutable, with a Settings→Coding UI card showing the selected source, the resolved
   binary path, and its `coda --version`. (Bundling already works — verified — so no build change
   is needed; the plan includes a verification step only.)
3. MCP-policy machinery (`CodaMcpSettingsStore`, `--no-*-mcp`, curated dir) is **untouched**.

## Part A — remove provider/model resolution

**Delete:** `src/Cortex.Contained.Bridge/Coding/CodaModelSettings.cs`,
`CodaModelSettingsStore.cs`, `CodaMachineSettingsReader.cs`, `CodingModelEndpoints.cs`.

**Modify:**
- `CodaServeArgsBuilder.Build(...)` — drop the `provider` parameter and the `--provider`
  emission. (Keep `--no-mcp` / `--no-project-mcp`, goal, session-memory, telemetry, etc.)
- `CodaSessionManager` — remove `ResolveProviderModel`, the provider/model bits of
  `EffectiveOptions`, and the two `CodingAgentErrorCodes.NoProvider` guards. Sessions launch
  `coda serve` and let coda self-resolve its connected provider. (Keep the MCP-policy override
  from `CodaMcpSettingsStore` in `EffectiveOptions`.)
- `CodaOptions` — remove `Provider`/`Model` fields (and from `Clone()`). Keep `CodaBinaryPath`,
  timeouts, MCP fields.
- `Program.cs` — remove the `CodaModelSettingsStore` registration and the `CodingModelEndpoints`
  mapping.
- Web UI (`wwwroot/app.html`, `wwwroot/js/pages/global-settings.js`) — remove the coda
  provider/model picker card + its JS (load/save/list-models).

**Config:** the `Coding:Coda:Provider`/`:Model` `cortex.yml` keys are no longer bound; existing
values are harmlessly ignored (YAML binder skips unknown keys). Remove them from any sample
config/docs.

## Part B — coda-source setting + UI + bundling

**Setting model:**
- New enum `CodaSource { Auto, Host, Bundled }` (Contracts).
- `CodaOptions` gains `CodaSource Source { get; set; } = CodaSource.Auto;` (YAML-bindable as
  `Coding:Coda:Source`).
- A runtime-mutable `CodaSourceStore` (JSON at `%APPDATA%\Cortex\coda-source.json`, mirroring
  `CodaMcpSettingsStore`) overrides the YAML value when set, so the UI takes effect without a
  restart.
- **Resolution** — a dedicated pure `CodaBinaryResolver.Resolve(CodaSource source, string baseDir,
  Func<string,bool> fileExists)` returning `(string path, bool fellBackFromBundle)` (replaces the
  ad-hoc `CodaOptions.ResolveDefaultBinaryPath`):
  - `Host` → `"coda"` (PATH).
  - `Bundled` → `<baseDir>/coda/coda.exe` if it exists; else `"coda"` with
    `fellBackFromBundle = true` (caller **logs a warning** — never hard-fail a session over a
    missing bundle).
  - `Auto` → bundled if present, else host (current `ResolveDefaultBinaryPath` behavior).
  The resolved path feeds `CodaOptions.CodaBinaryPath` used by `CodaSession.BuildProcessStartInfo`.

**REST + hub (mirror the MCP-policy endpoints):**
- `GET /api/tenants/{id}/coding/coda-source` → `{ source, resolvedPath, version, bundlePresent }`
  where `version` is obtained by running `<resolvedPath> --version` (short timeout, best-effort;
  null on failure) and `bundlePresent` reflects whether `<BaseDir>/coda/coda.exe` exists.
- `PUT /api/tenants/{id}/coding/coda-source` `{ source }` → persists via `CodaSourceStore`,
  returns the new resolved state.

**Web UI card** (Settings→Coding, where the provider/model card was): a 3-way selector
(Auto / Host / Built-in), a read-only line showing **resolved path** + **detected version** +
a "built-in available: yes/no" indicator, and a Save that re-queries the resolved state.

**Bundling (already works):** `Build-Launcher.ps1` publishes coda self-contained to
`<OutputDir>/Bridge/coda/coda.exe` and the 0.2.300 install contains it (verified). No build change
is required; the plan only re-verifies the bundle ships after the rebuild so `Bundled`/`Auto`
resolve to it.

## Components

| Unit | Change |
|---|---|
| `CodaModelSettings*`, `CodaMachineSettingsReader`, `CodingModelEndpoints` | delete |
| `CodaServeArgsBuilder` | drop `provider` param + `--provider` |
| `CodaSessionManager` | remove provider/model resolution + NoProvider guards |
| `CodaOptions` | remove `Provider`/`Model`; add `Source`; resolve binary by source |
| `CodaSource` (enum, Contracts) + `CodaSourceStore` | new: setting + runtime store |
| `CodingCodaSourceEndpoints` (new) | GET/PUT coda-source (source, resolvedPath, version) |
| `CodaBinaryResolver` (new, pure) | resolve binary path from source (Host/Bundled/Auto) |
| `Program.cs` | drop model-settings/endpoints; register source store + endpoints; use resolver |
| Web UI (`app.html`, `global-settings.js`) | replace provider/model card with coda-source card |
| `Build-Launcher.ps1` / MSIX | unchanged — bundling verified working (re-verify after rebuild) |

## Telemetry / error handling
- Log the resolved coda binary path + source at session spawn (aids "which coda ran?").
- `Bundled` with a missing bundle logs a warning and falls back to host — never fails the session.
- `--version` probe is best-effort with a short timeout; UI shows "unknown" on failure.

## Testing (TDD)
- `CodaServeArgsBuilder`: no `--provider` emitted; MCP/goal/etc. still emitted (update existing
  arg tests).
- Binary resolution: `Host`→"coda"; `Bundled`→bundled path when present, host + warning when
  absent; `Auto`→bundled-if-present-else-host. Pure/unit-testable resolver.
- `CodaSourceStore`: round-trip/reset, runtime override precedence over YAML.
- `CodaSessionManager`: launches without provider resolution; no `NoProvider` path remains.
- Endpoint round-trip (source persists; resolved state shape: `source`, `resolvedPath`,
  `version`, `bundlePresent`) — camelCase named props.
- Regression: existing coda-relay tests still pass with `--provider` gone.

## Rollout
Same as prior cortex deploys: `Build-All.ps1` (which must now reliably bundle coda) →
`Add-AppxPackage -ForceUpdateFromAnyVersion -ForceApplicationShutdown` → relaunch → verify
`/health` + the Settings→Coding card shows the resolved coda path/version, and a coding session
runs on Copilot.

## Out of scope
- Any change to coda-cli itself (already single-provider).
- Per-tenant coda-source (single setting per Bridge, like the MCP policy).
