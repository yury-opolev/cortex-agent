# Coda Relay Simplify + Coda-Source Setting — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the Bridge's now-redundant coda provider/model resolution, and add an explicit `CodaSource` (Auto/Host/Bundled) setting + Settings→Coding UI showing the resolved coda binary path and version.

**Architecture:** coda is single-provider (self-resolves its connected provider), so the Bridge stops resolving/passing `--provider` and deletes the model-settings machinery. A pure `CodaBinaryResolver` maps a `CodaSource` to a binary path; a runtime-mutable `CodaSourceStore` (mirroring `CodaMcpSettingsStore`) backs a new REST endpoint + UI card. The coda bundle already ships; no build change.

**Tech Stack:** .NET 10, C#, xUnit + NSubstitute, ASP.NET Core minimal APIs, Alpine.js, `JsonFileSettingsStore<T>`.

## Global Constraints

- Repo: `cortex-agent`, branch `feat/coda-relay-simplify`. Build `dotnet build cortex-contained.sln`; tests `dotnet test tests/Cortex.Contained.Bridge.Tests` (+ others as noted). Test filter: `--filter "Name~..."` or `--filter "FullyQualifiedName~..."`.
- C# style (user global): `this.` on instance members; braces always; file-scoped namespaces; `sealed`; source-generated `[LoggerMessage]`; `ConfigureAwait(false)` in library code; async suffix; DTOs = named-prop records (no ValueTuples — add a serialization-shape test).
- `TreatWarningsAsErrors` ON globally.
- `CodaSource` values: `Auto`, `Host`, `Bundled`. Default `Auto`. Resolution: `Host`→`"coda"`; `Bundled`→`<baseDir>/coda/coda.exe` if it exists else `"coda"` with a "fell back" flag (caller warns); `Auto`→bundled-if-present-else-`"coda"`.
- Runtime store: `%APPDATA%\Cortex\coda-source.json`, overrides the `Coding:Coda:Source` YAML value; read on demand (no restart) — mirror `CodaMcpSettingsStore`.
- KEEP untouched: MCP-policy machinery (`CodaMcpSettingsStore`, `--no-mcp`/`--no-project-mcp`, curated dir), folders/policy, timeouts.
- Bundling is NOT changed (verified working: `<install>/Bridge/coda/coda.exe` ships).

**Build:** `dotnet build cortex-contained.sln`
**Test:** `dotnet test tests/Cortex.Contained.Bridge.Tests`

---

### Task 1: CodaSource enum + pure CodaBinaryResolver

**Files:**
- Create: `src/Cortex.Contained.Contracts/Coding/CodaSource.cs`
- Create: `src/Cortex.Contained.Bridge/Coding/CodaBinaryResolver.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/Coding/CodaBinaryResolverTests.cs`

**Interfaces:**
- Produces: `enum CodaSource { Auto, Host, Bundled }` (namespace `Cortex.Contained.Contracts.Coding`).
- Produces: `static class CodaBinaryResolver` with
  `static (string Path, bool FellBackFromBundle) Resolve(CodaSource source, string baseDirectory, Func<string, bool> fileExists)`.
  `Bundled`/`Auto` bundle path = `Path.Combine(baseDirectory, "coda", "coda.exe")`.

- [ ] **Step 1: Write the failing test**

```csharp
using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public class CodaBinaryResolverTests
{
    private static string Bundle(string b) => System.IO.Path.Combine(b, "coda", "coda.exe");

    [Fact]
    public void Host_AlwaysReturnsPathCoda()
    {
        var (path, fell) = CodaBinaryResolver.Resolve(CodaSource.Host, "/app", _ => true);
        Assert.Equal("coda", path);
        Assert.False(fell);
    }

    [Fact]
    public void Bundled_Present_ReturnsBundlePath()
    {
        var (path, fell) = CodaBinaryResolver.Resolve(CodaSource.Bundled, "/app", p => p == Bundle("/app"));
        Assert.Equal(Bundle("/app"), path);
        Assert.False(fell);
    }

    [Fact]
    public void Bundled_Absent_FallsBackToHost_WithFlag()
    {
        var (path, fell) = CodaBinaryResolver.Resolve(CodaSource.Bundled, "/app", _ => false);
        Assert.Equal("coda", path);
        Assert.True(fell);
    }

    [Fact]
    public void Auto_Present_UsesBundle_Absent_UsesHost()
    {
        Assert.Equal(Bundle("/app"), CodaBinaryResolver.Resolve(CodaSource.Auto, "/app", p => p == Bundle("/app")).Path);
        Assert.Equal("coda", CodaBinaryResolver.Resolve(CodaSource.Auto, "/app", _ => false).Path);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~CodaBinaryResolverTests"`
Expected: FAIL — types undefined.

- [ ] **Step 3: Implement**

`CodaSource.cs`:

```csharp
namespace Cortex.Contained.Contracts.Coding;

/// <summary>Which coda binary the Bridge launches for coding sessions.</summary>
public enum CodaSource
{
    /// <summary>Bundled coda if present, else the host <c>coda</c> on PATH.</summary>
    Auto,

    /// <summary>Always the host-installed <c>coda</c> (PATH).</summary>
    Host,

    /// <summary>Always the bundled <c>coda.exe</c>; falls back to host if absent.</summary>
    Bundled,
}
```

`CodaBinaryResolver.cs`:

```csharp
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Pure resolution of the coda binary path from the configured <see cref="CodaSource"/>.
/// Bundled path is <c>&lt;baseDirectory&gt;/coda/coda.exe</c> (next to the Bridge).
/// </summary>
public static class CodaBinaryResolver
{
    public static (string Path, bool FellBackFromBundle) Resolve(
        CodaSource source, string baseDirectory, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        var bundled = System.IO.Path.Combine(baseDirectory, "coda", "coda.exe");

        return source switch
        {
            CodaSource.Host => ("coda", false),
            CodaSource.Bundled => fileExists(bundled) ? (bundled, false) : ("coda", true),
            _ => fileExists(bundled) ? (bundled, false) : ("coda", false), // Auto
        };
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~CodaBinaryResolverTests"`
Expected: PASS (5 assertions across 4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Contracts/Coding/CodaSource.cs src/Cortex.Contained.Bridge/Coding/CodaBinaryResolver.cs tests/Cortex.Contained.Bridge.Tests/Coding/CodaBinaryResolverTests.cs
git commit -m "feat(coding): CodaSource enum + pure CodaBinaryResolver"
```

---

### Task 2: CodaSourceStore (runtime-mutable setting)

**Files:**
- Create: `src/Cortex.Contained.Bridge/Coding/CodaSourceStore.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/Coding/CodaSourceStoreTests.cs`

**Interfaces:**
- Consumes: `CodaSource`, `JsonFileSettingsStore<T>` (base class — read `CodaMcpSettingsStore.cs` for the exact pattern).
- Produces: `sealed class CodaSourceStore : JsonFileSettingsStore<CodaSourceStore.CodaSourceFile>` with
  `static CodaSourceStore Default()` (→ `%APPDATA%\Cortex\coda-source.json`), `CodaSource? Get()` (null = not set → use YAML), `void Set(CodaSource? source)`.

- [ ] **Step 1: Write the failing test**

```csharp
using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public class CodaSourceStoreTests : IDisposable
{
    private readonly string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coda-source-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Get_Unset_ReturnsNull()
    {
        Assert.Null(new CodaSourceStore(this.path).Get());
    }

    [Fact]
    public void SetThenGet_RoundTrips()
    {
        new CodaSourceStore(this.path).Set(CodaSource.Host);
        Assert.Equal(CodaSource.Host, new CodaSourceStore(this.path).Get());
    }

    public void Dispose()
    {
        if (File.Exists(this.path)) { File.Delete(this.path); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~CodaSourceStoreTests"`
Expected: FAIL — `CodaSourceStore` undefined.

- [ ] **Step 3: Implement** (mirror `CodaMcpSettingsStore.cs` exactly — same base class + `Default()` + Get/Set + nested file record):

```csharp
using System.Text.Json.Serialization;
using Cortex.Contained.Bridge.Storage;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Runtime-mutable store for the coda binary source (Auto/Host/Bundled), set from the web UI.
/// Persists to JSON and reads on demand (no restart). Overrides the <c>Coding:Coda:Source</c>
/// value in cortex.yml when set. Mirrors <see cref="CodaMcpSettingsStore"/>.
/// </summary>
public sealed class CodaSourceStore : JsonFileSettingsStore<CodaSourceStore.CodaSourceFile>
{
    public CodaSourceStore(string filePath)
        : base(filePath)
    {
    }

    /// <summary>Default location: <c>%APPDATA%\Cortex\coda-source.json</c>.</summary>
    public static CodaSourceStore Default()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new CodaSourceStore(Path.Combine(appData, "Cortex", "coda-source.json"));
    }

    /// <summary>Current source override, or null when unset (use the YAML default).</summary>
    public CodaSource? Get() => this.Load().Source;

    /// <summary>Persist the source override (null clears it).</summary>
    public void Set(CodaSource? source) => this.Save(new CodaSourceFile { Source = source });

    /// <summary>On-disk shape.</summary>
    public sealed class CodaSourceFile
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public CodaSource? Source { get; set; }
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~CodaSourceStoreTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Bridge/Coding/CodaSourceStore.cs tests/Cortex.Contained.Bridge.Tests/Coding/CodaSourceStoreTests.cs
git commit -m "feat(coding): runtime-mutable CodaSourceStore"
```

---

### Task 3: Remove provider/model resolution machinery

**Files:**
- Delete: `src/Cortex.Contained.Bridge/Coding/CodaModelSettings.cs`, `CodaModelSettingsStore.cs`, `CodaMachineSettingsReader.cs`, `CodingModelEndpoints.cs`
- Modify: `CodaServeArgsBuilder.cs`, `CodaSessionManager.cs`, `CodaOptions.cs`, `CodaSession.cs` (the `CodaServeArgsBuilder.Build(...)` call at `CodaSession.cs:455`), `Program.cs`
- Delete tests: `tests/Cortex.Contained.Bridge.Tests/Coding/CodaMachineSettingsReaderTests.cs`, `CodaModelSettingsStoreTests.cs` (they test deleted files)
- Update tests: `tests/Cortex.Contained.Bridge.Tests/Coding/CodaServeArgsBuilderTests.cs` (assert `--provider` no longer emitted), `CodaOptionsTests.cs` (drop Provider/Model assertions). Grep `tests/` for any other references.

**Interfaces:**
- Produces: `CodaServeArgsBuilder.Build(...)` without a `provider` parameter and without emitting `--provider`. `CodaOptions` without `Provider`/`Model`. `CodaSessionManager` without `ResolveProviderModel` / `NoProvider` guards.

- [ ] **Step 1: Delete the four files and remove their references.**

Delete the four listed files. In `Program.cs`, remove: the `CodaModelSettingsStore` registration/singleton, and the `CodingModelEndpoints.Map...` call. Grep first: `grep -rn "CodaModelSettings\|CodaMachineSettingsReader\|CodingModelEndpoints" src/` and remove every reference.

- [ ] **Step 2: `CodaServeArgsBuilder` — drop `--provider`.**

Remove the `string? provider = null` parameter and the block that appends `--provider`. Update its callers: `CodaSession.cs:455` (`CodaServeArgsBuilder.Build(...)`) — drop the provider argument. Keep all other args (cwd, session-id, permission-mode, telemetry, goal, session-memory, MCP flags).

- [ ] **Step 3: `CodaSessionManager` — remove provider/model resolution.**

Delete `ResolveProviderModel`. In `EffectiveOptions()`, remove the `effective.Provider = ...` / `effective.Model = ...` lines and the `CodaMachineSettingsReader`/`modelSettingsStore` usage for provider/model (KEEP the `mcpSettingsStore` MCP-policy override block). Remove the two `CodingAgentErrorCodes.NoProvider` guard blocks (the sessions no longer need a provider to be configured). Remove the `modelSettingsStore` field + ctor param (update the `Program.cs` construction accordingly).

- [ ] **Step 4: `CodaOptions` — remove `Provider`/`Model`.**

Delete the `Provider` and `Model` properties and their lines in `Clone()`. Leave `CodaBinaryPath`, timeouts, MCP fields, and `ResolveDefaultBinaryPath` (Task 4 replaces the latter's use).

- [ ] **Step 5: Build + update tests.**

Run: `dotnet build cortex-contained.sln`. Fix any test that referenced the removed provider/model API — update coda arg-builder tests to assert `--provider` is NO LONGER emitted (add an explicit `Assert.DoesNotContain("--provider", args)` where an arg-list test exists). Run `dotnet test tests/Cortex.Contained.Bridge.Tests`.
Expected: build clean; tests green (with the provider assertions inverted).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(coding): remove Bridge coda provider/model resolution (coda self-resolves)"
```

---

### Task 4: CodaOptions.Source + resolve binary by source at spawn

**Files:**
- Modify: `src/Cortex.Contained.Bridge/Coding/CodaOptions.cs` (add `Source`), `CodaSessionManager.cs` (resolve `CodaBinaryPath` via `CodaBinaryResolver` in `EffectiveOptions`), `Program.cs` (register `CodaSourceStore`; pass to `CodaSessionManager`)
- Test: `tests/Cortex.Contained.Bridge.Tests/Coding/CodaSessionManagerSourceTests.cs` (or extend existing manager tests)

**Interfaces:**
- Consumes: `CodaSource`, `CodaBinaryResolver.Resolve`, `CodaSourceStore.Get()`.
- Produces: `CodaOptions.Source` (`CodaSource`, default `Auto`, YAML `Coding:Coda:Source`); `EffectiveOptions()` sets `CodaBinaryPath` to the resolved path (store override → YAML `Source`), logging the resolved path/source and any bundle-fallback warning.

- [ ] **Step 1: Write the failing test**

Assert `EffectiveOptions()` resolves the binary from the source. Construct `CodaSessionManager` the way existing manager tests do (search `tests/…/Coding` for the setup helper), injecting a `CodaSourceStore` over a temp file and a `CodaOptions` with a known `Source`. If `EffectiveOptions` is internal, the test project already has access (other Coding internals are tested).

```csharp
[Fact]
public void EffectiveOptions_HostSource_UsesPathCoda()
{
    var mgr = NewManager(source: CodaSource.Host);   // helper builds CodaSessionManager
    Assert.Equal("coda", mgr.EffectiveOptions().CodaBinaryPath);
}
```

(Model `NewManager` on the existing manager-test construction; set the source via the injected `CodaSourceStore` or the `CodaOptions.Source`.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~CodaSessionManagerSourceTests"`
Expected: FAIL — `Source` undefined / binary not resolved.

- [ ] **Step 3: Implement**

`CodaOptions`: add
```csharp
/// <summary>Which coda binary to launch. YAML: Coding:Coda:Source. Default Auto.</summary>
public Cortex.Contained.Contracts.Coding.CodaSource Source { get; set; } = Cortex.Contained.Contracts.Coding.CodaSource.Auto;
```
and add `Source = this.Source,` to `Clone()`.

`CodaSessionManager.EffectiveOptions()`: after cloning, resolve the binary:
```csharp
var source = this.sourceStore?.Get() ?? effective.Source;
var (binPath, fellBack) = CodaBinaryResolver.Resolve(source, AppContext.BaseDirectory, System.IO.File.Exists);
effective.CodaBinaryPath = binPath;
if (fellBack)
{
    this.LogBundledCodaMissing(binPath); // [LoggerMessage] Warning
}
this.LogResolvedCodaBinary(source.ToString(), binPath); // [LoggerMessage] Information
```
Add the `CodaSourceStore? sourceStore` field + ctor param. In `Program.cs`: register `CodaSourceStore` (`AddSingleton(_ => CodaSourceStore.Default())`) and pass it into the `CodaSessionManager` construction (replacing the removed `CodaModelSettingsStore` slot).

- [ ] **Step 4: Run + build**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~CodaSessionManagerSourceTests"` → PASS
Run: `dotnet build cortex-contained.sln` → clean.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(coding): resolve coda binary from CodaSource (store override -> yaml)"
```

---

### Task 5: REST endpoint for coda source (+ version probe)

**Files:**
- Create: `src/Cortex.Contained.Bridge/Coding/CodingCodaSourceEndpoints.cs` (mirror `CodingMcpEndpoints.cs` — read it for the exact `MapGroup`/auth/registration pattern)
- Modify: `Program.cs` (map the new endpoints)
- Test: `tests/Cortex.Contained.Bridge.Tests/Coding/CodaSourceEndpointShapeTests.cs`

**Interfaces:**
- `GET /api/tenants/{tenantId}/coding/coda-source` → `{ source, resolvedPath, version, bundlePresent }`.
- `PUT /api/tenants/{tenantId}/coding/coda-source` `{ source }` → persists via `CodaSourceStore`; returns the new resolved state.
- Produces a DTO `record CodaSourceState(string Source, string ResolvedPath, string? Version, bool BundlePresent)` (camelCase JSON).

- [ ] **Step 1: Write the failing test** (serialization shape + resolved-state assembly is pure enough to unit test; the version probe is best-effort I/O so keep it out of the unit):

```csharp
using System.Text.Json;
using Cortex.Contained.Bridge.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public class CodaSourceEndpointShapeTests
{
    [Fact]
    public void CodaSourceState_SerializesCamelCaseNamedProps()
    {
        var state = new CodaSourceState("Host", "coda", "Coda v0.1.55", false);
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Contains("\"source\"", json, StringComparison.Ordinal);
        Assert.Contains("\"resolvedPath\"", json, StringComparison.Ordinal);
        Assert.Contains("\"version\"", json, StringComparison.Ordinal);
        Assert.Contains("\"bundlePresent\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Item1", json, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~CodaSourceEndpointShapeTests"`
Expected: FAIL — `CodaSourceState` undefined.

- [ ] **Step 3: Implement**

Add `public sealed record CodaSourceState(string Source, string ResolvedPath, string? Version, bool BundlePresent);` (in the endpoints file). The endpoints (mirror `CodingMcpEndpoints` auth/group): GET reads the effective source (`store.Get() ?? options.Source`), resolves the path via `CodaBinaryResolver.Resolve(source, AppContext.BaseDirectory, File.Exists)`, computes `bundlePresent = File.Exists(Path.Combine(AppContext.BaseDirectory, "coda", "coda.exe"))`, and probes `version` by running `<resolvedPath> --version` with a short timeout (best-effort; null on failure — use a small helper `TryProbeVersionAsync(path)` that starts the process, reads the first stdout line, kills it after ~3s, and returns null on any exception). PUT parses `{ source }` (enum, case-insensitive), calls `store.Set(source)`, and returns the freshly-resolved `CodaSourceState`. Register the endpoints in `Program.cs` next to the MCP endpoints mapping.

- [ ] **Step 4: Run + build**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~CodaSourceEndpointShapeTests"` → PASS
Run: `dotnet build cortex-contained.sln` → clean.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(bridge): coda-source REST endpoint (source/resolvedPath/version/bundlePresent)"
```

---

### Task 6: Web UI — replace provider/model card with coda-source card

**Files:**
- Modify: `src/Cortex.Contained.Bridge/wwwroot/js/pages/global-settings.js`, `src/Cortex.Contained.Bridge/wwwroot/app.html`

**Interfaces:**
- Consumes: `GET/PUT /api/tenants/{id}/coding/coda-source`.

- [ ] **Step 1: Remove the coda provider/model card + its JS.**

In `global-settings.js`, remove the state + methods that load/save the coda provider/model and list coda models (search for `coda-model` / the provider/model card handlers). In `app.html`, remove the provider/model picker card markup.

- [ ] **Step 2: Add the coda-source card.**

Alpine state: `codaSource: { source: "Auto", resolvedPath: "", version: "", bundlePresent: false }`, `codaSourceSaving: false`. Methods (mirror the MCP-policy card's load/save):
```javascript
async loadCodaSource() {
    try {
        this.codaSource = await api.get(`/api/tenants/${encodeURIComponent(this.tenantId)}/coding/coda-source`);
    } catch (e) { Alpine.store("toast").error("Failed to load coda source"); }
},
async saveCodaSource() {
    this.codaSourceSaving = true;
    try {
        this.codaSource = await api.put(`/api/tenants/${encodeURIComponent(this.tenantId)}/coding/coda-source`, { source: this.codaSource.source });
        Alpine.store("toast").success("Coda source saved");
    } catch (e) { Alpine.store("toast").error("Save failed"); }
    this.codaSourceSaving = false;
},
```
Add `this.loadCodaSource()` to the page's existing init `Promise.all([...])`. Markup: a card with a 3-way `<select>` bound to `codaSource.source` (Auto / Host / Built-in), read-only lines showing `codaSource.resolvedPath`, `codaSource.version` (or "unknown"), and "built-in available: {{ codaSource.bundlePresent ? 'yes' : 'no' }}", and a Save button (disabled while `codaSourceSaving`) calling `saveCodaSource`. Reuse the existing card/select/button classes from the MCP-policy card for visual consistency.

- [ ] **Step 3: Build + manual check**

Run: `dotnet build cortex-contained.sln` (packages static assets) → clean. If practical, `.\scripts\Start-Cortex.ps1 -BridgeOnly` and confirm the Settings→Coding page shows the coda-source card with the resolved path + version and the provider/model card is gone; else note it for Task 7 smoke.

- [ ] **Step 4: Commit**

```bash
git add src/Cortex.Contained.Bridge/wwwroot/js/pages/global-settings.js src/Cortex.Contained.Bridge/wwwroot/app.html
git commit -m "feat(bridge-ui): replace coda provider/model card with coda-source card"
```

---

### Task 7: Full verification + docs

- [ ] **Step 1: Full build + test**

Run: `dotnet build cortex-contained.sln` → 0 warnings.
Run: `dotnet test cortex-contained.sln` → all green (report totals). Fix any lingering reference to removed provider/model API.

- [ ] **Step 2: Docs**

Update `docs/mcp-plugin-system.md` or `docs/architecture.md` coding-relay section (whichever documents the relay): note coda is single-provider (Bridge no longer passes `--provider`/resolves a provider) and the new `Coding:Coda:Source` setting + Settings→Coding card. Commit.

- [ ] **Step 3: Commit docs**

```bash
git add docs/
git commit -m "docs: coda relay single-provider + coda-source setting"
```

## Rollout (after all tasks green + merged)
1. Merge `feat/coda-relay-simplify` → cortex `main` (branch push + PR or ff), push.
2. `scripts/Build-All.ps1` → `Add-AppxPackage -ForceUpdateFromAnyVersion -ForceApplicationShutdown` → relaunch.
3. Verify: `/health` healthy; the Settings→Coding card shows the resolved coda path (`Bridge/coda/coda.exe`) + version; a coding session runs on Copilot; switching to Host and back works.

## Self-Review Notes
- Spec coverage: resolver (T1), store (T2), removal (T3), source wiring (T4), endpoint+version (T5), UI (T6), verify/docs (T7), rollout (section). All mapped; bundling explicitly out of scope (verified working).
- Type consistency: `CodaSource`, `CodaBinaryResolver.Resolve(source, baseDir, fileExists)→(Path,FellBackFromBundle)`, `CodaSourceStore.Get/Set`, `CodaOptions.Source`, `CodaSourceState(Source,ResolvedPath,Version,BundlePresent)` used identically across tasks.
