# Increment 5 — Reranker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an opt-in cross-encoder reranking stage after hybrid fusion to `history_search` and `memory_search`, served by a pinned, authenticated, unpublished `cortex-reranker` sidecar (llama.cpp `llama-server --reranking` + Qwen3-Reranker-0.6B) that the Bridge keys and starts and that Self-Update builds, pins, health-gates and rolls back; plus the evaluation tooling for Gate R (latency/quality curve over TopN × MaxDocChars with the embedder busy and a cold model; significant paired gain and p95 ≤ 2.5 s). `Reranker:Enabled` (Bridge) defaults to false: no behaviour change for existing installs.

**Architecture:**
- **Sidecar** `docker/reranker`: llama.cpp server image pinned by digest (`VARIANT=cuda` default, `cpu` optional), GGUF pinned by sha256 and baked in, entrypoint that refuses to start without `LLAMA_API_KEY` and runs reranking only, no web UI, no slots, no request logging. Compose service `reranker` (profile `reranker`): internal network, no ports, `pull_policy: never`, GPU reservation.
- **Bridge**: `Reranker:Enabled` (default false); a DPAPI-held 256-bit key; `RerankerSidecarLifecycle` runs `compose up` with the key in the child environment (compose recreates on key or image change); `ProvideRerankerGrant(RerankerGrant)` pushed to every agent on connect.
- **Agent** `Reranking/`: options + validation, `RerankerAccess` (grant in memory), `RerankerEndpointGuard` (allowed host or HTTPS, key always), `LlamaServerReranker` (`/v1/rerank`, bearer), `RerankDocument` (raw best-matching lines), `RerankStage` (budget, timeout, circuit breaker, honest notes, `/health`). History reranks the fused head inside the read lease; memory reranks hybrid results in the tool. Keyword, dense and time-order searches are never reranked.
- **Self-Update**: `Build-RerankerImage.ps1`, opt-in rebuild in `Build-All`, `imageIds` pins in the manifest verified at deploy, a health gate on the recreated sidecar, `cortex-reranker:rollback` snapshot/restore.
- **Evaluation**: rerank dimension on the ablation configurations; `rerank-curve` and `gate-r` in `history-eval` and `memory-eval`; `history-smoke reranker-check`.

**Tech Stack:** .NET 10, C#, ASP.NET Core minimal API + SignalR, `IHttpClientFactory`, xUnit v2 + NSubstitute + `Microsoft.Extensions.Time.Testing` (Agent.Host.Tests, Bridge.Tests), YamlDotNet (compose test), Docker BuildKit (`ADD --checksum`), Docker Compose v2 profiles, llama.cpp b12602, Qwen3-Reranker-0.6B Q8_0 GGUF, PowerShell 7 / Windows PowerShell scripts.

**Design spec:** [`docs/superpowers/specs/2026-11-21-inc5-reranker-design.md`](../specs/2026-11-21-inc5-reranker-design.md) (decisions I5-1…I5-15). Parent: [`2026-11-21-episodic-history-memory-design.md`](../specs/2026-11-21-episodic-history-memory-design.md) roadmap row 5 and §4.3.

**Base:** `feat/episodic-history-memory` at `ab2edb0` (increment 4 plus its review fixes; `lib/memory-mcp` at `ae7e657`). This increment changes **no submodule** (I5-12); if a later fix moves the submodule, no task here depends on it.

---

## Before you start (gotchas found while replaying this plan)

- **Line endings and BOMs:** `src/**/*.cs` working copies are CRLF; `Program.cs` files start with a UTF-8 BOM. `docker/reranker/Dockerfile` and `entrypoint.sh` **must be LF** (`.gitattributes` already forces `Dockerfile` and `*.sh` to LF — a CRLF entrypoint breaks the shebang). Diffs below are LF; apply them with `git apply --ignore-whitespace --recount`, an editor or the edit tool, and keep each file's BOM and line endings. **Create** blocks are complete files.
- **PowerShell here-strings:** never paste C#/Markdown into a *double-quoted* here-string (`@" … "@`): `` `r ``, `` `n ``, `` `f `` inside backticked Markdown become control characters (this corrupted a doc during the replay). Use single-quoted here-strings (`@' … '@`) or the edit tool.
- **Never stage `version.json`.** Stage only the files listed in each task's commit step.
- **NuGet audit:** every `dotnet build`/`dotnet test` uses `-p:NuGetAudit=false` (known NU1902 advisory on MessagePack).
- **Locked DLLs on Windows:** set `$env:MSBUILDDISABLENODEREUSE=1` if MSBuild nodes keep `bin\` files locked.
- **Tests never need a reranker or Docker:** `FakeReranker` (Task 4) and `HttpMessageHandler` stubs replace the sidecar; the compose/Dockerfile test (Task 8) is static. Building and running the image are optional manual steps (needs Docker; the first CUDA build pulls ~3 GB plus the 639 MB model).
- **Analyzers that bit during the replay:** CA1001 (a test class owning an `HttpMessageHandler` field must be `IDisposable`), CA1873 (don't compute a `[LoggerMessage]` argument inline — assign it to a local first), CS1998 (a `[Fact]` without `await` must be `void`).
- **Docker build details:** `ADD --chmod` also applies the mode to the directory it creates, so the model uses `0555` (with `0444` the unprivileged user cannot enter `/models`); the llama.cpp image sets `LLAMA_ARG_HOST`/`LLAMA_ARG_PORT`, which the entrypoint unsets to keep its explicit flags the single source.
- **Windows PowerShell scripts:** with `$ErrorActionPreference = 'Stop'`, redirecting a failing native command's stderr (`docker image inspect missing 2>$null`) throws in Windows PowerShell 5.1 — every new docker probe in `scripts/` is wrapped in `try { … } catch { }`.
- **Test environment variable:** `RerankerCheckTests` sets and clears `Reranker__ApiKey` (the CLIs read the Bridge-held key from it); no other test reads it.
- **Memory evaluation:** `register` needs ≥ 250 queries (increment 4), so the memory `gate-r` test writes 250.
- **Commits:** every commit ends with the trailer passed as a second `-m`: `-m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"`.

## File structure

| File | Change | Task(s) |
|---|---|---|
| `CLAUDE.md` | modify | 15 |
| `docker-compose.yml` | modify | 8 |
| `docker/reranker/Dockerfile` | create | 8 |
| `docker/reranker/entrypoint.sh` | create | 8 |
| `docker/reranker/version.json` | create | 8 |
| `docs/history-hybrid-search.md` | modify | 15 |
| `docs/memory-hybrid-search.md` | modify | 15 |
| `docs/reranker.md` | create | 15 |
| `docs/security.md` | modify | 15 |
| `docs/self-update.md` | modify | 10 |
| `scripts/Build-All.ps1` | modify | 10 |
| `scripts/Build-Launcher.ps1` | modify | 10 |
| `scripts/Build-RerankerImage.ps1` | create | 10 |
| `scripts/Self-Update.ps1` | modify | 10 |
| `src/Cortex.Contained.Agent.Host/History/Evaluation/AblationGrid.cs` | modify | 12 |
| `src/Cortex.Contained.Agent.Host/History/Evaluation/AblationRunner.cs` | modify | 12 |
| `src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs` | modify | 12 |
| `src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs` | modify | 12 |
| `src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryRerankEval.cs` | create | 12 |
| `src/Cortex.Contained.Agent.Host/History/Evaluation/Smoke/HistorySmokeCli.cs` | modify | 13 |
| `src/Cortex.Contained.Agent.Host/History/Evaluation/Smoke/RerankerCheck.cs` | create | 13 |
| `src/Cortex.Contained.Agent.Host/History/HistoryCursorCodec.cs` | modify | 5 |
| `src/Cortex.Contained.Agent.Host/History/HistoryRecallModels.cs` | modify | 5 |
| `src/Cortex.Contained.Agent.Host/History/HistoryRecallService.Hybrid.cs` | modify | 5 |
| `src/Cortex.Contained.Agent.Host/History/HistoryRecallService.cs` | modify | 5 |
| `src/Cortex.Contained.Agent.Host/History/HistoryResultRenderer.cs` | modify | 5 |
| `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs` | modify | 7 |
| `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryAblationGrid.cs` | modify | 14 |
| `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryAblationRunner.cs` | modify | 14 |
| `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs` | modify | 14 |
| `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs` | modify | 14 |
| `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryRerankEval.cs` | create | 14 |
| `src/Cortex.Contained.Agent.Host/Program.cs` | modify | 7 |
| `src/Cortex.Contained.Agent.Host/Reranking/Evaluation/RerankEvaluation.cs` | create | 11 |
| `src/Cortex.Contained.Agent.Host/Reranking/HybridMemoryReranking.cs` | create | 6 |
| `src/Cortex.Contained.Agent.Host/Reranking/IReranker.cs` | create | 3 |
| `src/Cortex.Contained.Agent.Host/Reranking/LlamaServerReranker.cs` | create | 3, 11 |
| `src/Cortex.Contained.Agent.Host/Reranking/RerankCircuitBreaker.cs` | create | 4 |
| `src/Cortex.Contained.Agent.Host/Reranking/RerankDocument.cs` | create | 2 |
| `src/Cortex.Contained.Agent.Host/Reranking/RerankRequest.cs` | create | 5 |
| `src/Cortex.Contained.Agent.Host/Reranking/RerankStage.cs` | create | 4 |
| `src/Cortex.Contained.Agent.Host/Reranking/RerankerAccess.cs` | create | 1 |
| `src/Cortex.Contained.Agent.Host/Reranking/RerankerEndpointGuard.cs` | create | 1 |
| `src/Cortex.Contained.Agent.Host/Reranking/RerankerOptions.cs` | create | 1 |
| `src/Cortex.Contained.Agent.Host/Tools/BuiltIn/MemorySearchTool.cs` | modify | 6 |
| `src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs` | modify | 9 |
| `src/Cortex.Contained.Bridge/Hub/HubClient.cs` | modify | 9 |
| `src/Cortex.Contained.Bridge/Program.cs` | modify | 9 |
| `src/Cortex.Contained.Bridge/Security/SecretManager.cs` | modify | 9 |
| `src/Cortex.Contained.Bridge/Speech/DockerComposeCommandRunner.cs` | modify | 9 |
| `src/Cortex.Contained.Bridge/Speech/IRerankerComposeRunner.cs` | create | 9 |
| `src/Cortex.Contained.Bridge/Speech/RerankerSidecarLifecycle.cs` | create | 9 |
| `src/Cortex.Contained.Contracts/Config/BridgeConfig.cs` | modify | 9 |
| `src/Cortex.Contained.Contracts/Hub/ISystemHub.cs` | modify | 7 |
| `src/Cortex.Contained.Contracts/Hub/RerankerGrant.cs` | create | 1 |
| `tests/Cortex.Contained.Agent.Host.Tests/Reranking/FakeReranker.cs` | create | 4 |
| `tests/Cortex.Contained.Agent.Host.Tests/Reranking/HistoryRerankEvalTests.cs` | create | 12 |
| `tests/Cortex.Contained.Agent.Host.Tests/Reranking/HistoryRerankTests.cs` | create | 5 |
| `tests/Cortex.Contained.Agent.Host.Tests/Reranking/LlamaServerRerankerTests.cs` | create | 3 |
| `tests/Cortex.Contained.Agent.Host.Tests/Reranking/MemoryRerankEvalTests.cs` | create | 14 |
| `tests/Cortex.Contained.Agent.Host.Tests/Reranking/MemoryRerankTests.cs` | create | 6 |
| `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankCircuitBreakerTests.cs` | create | 4 |
| `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankDocumentTests.cs` | create | 2 |
| `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankEvaluationTests.cs` | create | 11 |
| `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankStageTests.cs` | create | 4 |
| `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankerCheckTests.cs` | create | 13 |
| `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankerConfigurationTests.cs` | create | 1 |
| `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankerSidecarExposureTests.cs` | create | 8 |
| `tests/Cortex.Contained.Bridge.Tests/Hosting/CredentialsPusherRerankerGrantTests.cs` | create | 9 |
| `tests/Cortex.Contained.Bridge.Tests/Speech/RerankerSidecarLifecycleTests.cs` | create | 9 |

## Tasks overview

| # | Task | Model |
|---|---|---|
| 1 | Reranker settings, Bridge grant, access and exposure guard | cheap |
| 2 | Raw best-matching lines for the reranker | cheap |
| 3 | llama-server reranker client | standard |
| 4 | Circuit breaker and rerank stage | standard |
| 5 | Rerank stage after hybrid fusion in `history_search` | standard |
| 6 | Rerank stage after hybrid fusion in `memory_search` | standard |
| 7 | Agent wiring: hub grant, DI, validation and `/health` | standard |
| 8 | `cortex-reranker` sidecar image and compose service | standard |
| 9 | Bridge: DPAPI key, sidecar lifecycle and grant push | standard |
| 10 | Self-Update coverage: build, pin, health gate, rollback | standard |
| 11 | Shared rerank evaluation: grid, curve, Gate R, embedder load | standard |
| 12 | `history-eval rerank-curve` and `gate-r` | standard |
| 13 | `history-smoke reranker-check` | cheap |
| 14 | `memory-eval rerank-curve` and `gate-r` | standard |
| 15 | Documentation | cheap |
| 16 | Final verification | standard |

---

### Task 1: Reranker settings, Bridge grant, access and exposure guard

**Model:** cheap

I5-4, I5-5, I5-6. The agent-side `Reranker` options (endpoint, model, allowed hosts, timeout, circuit breaker, per-surface budgets) with validation, the `RerankerGrant` DTO the Bridge will push, the in-memory `RerankerAccess` holding it, and `RerankerEndpointGuard`: candidate text goes only to an allowed in-compose host or over HTTPS, and **always** with a key (stricter than the embedder guard).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Reranking/RerankerAccess.cs`
- Create: `src/Cortex.Contained.Agent.Host/Reranking/RerankerEndpointGuard.cs`
- Create: `src/Cortex.Contained.Agent.Host/Reranking/RerankerOptions.cs`
- Create: `src/Cortex.Contained.Contracts/Hub/RerankerGrant.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankerConfigurationTests.cs`

- [ ] **Step 1: Write the failing test**

**Create** `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankerConfigurationTests.cs`:

````csharp
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary>Reranker settings, the Bridge grant and the exposure guard (§4.3: authentication always required).</summary>
public sealed class RerankerConfigurationTests
{
    private static readonly string[] Sidecar = ["reranker"];

    [Fact]
    public void Defaults_AreValid_AndUseTheInComposeSidecar()
    {
        var options = new RerankerOptions();

        Assert.Empty(options.Validate());
        Assert.Equal("http://reranker:8080", options.Endpoint);
        Assert.Equal(new RerankBudget(15, 1200), options.History.Budget());
    }

    [Fact]
    public void Validate_BudgetOutOfRange_ReportsTheSection()
    {
        var options = new RerankerOptions();
        options.History.TopN = 0;
        options.Memory.MaxDocChars = 50_000;

        var errors = options.Validate();

        Assert.Contains(errors, e => e.StartsWith("Reranker:History:TopN", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Reranker:Memory:MaxDocChars", StringComparison.Ordinal));
    }

    [Fact]
    public void Access_BeforeAnyGrant_IsOff_AndBlankKeyIsNoKey()
    {
        var access = new RerankerAccess();
        Assert.False(access.Enabled);

        access.Apply(new RerankerGrant { Enabled = true, ApiKey = " " });

        Assert.True(access.Enabled);
        Assert.Null(access.ApiKey);
    }

    [Fact]
    public void Guard_SidecarWithKey_Allowed()
    {
        Assert.Null(RerankerEndpointGuard.Check("http://reranker:8080", "k", Sidecar));
    }

    [Fact]
    public void Guard_SidecarWithoutKey_Refused()
    {
        Assert.Contains("API key", RerankerEndpointGuard.Check("http://reranker:8080", null, Sidecar), StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_RemotePlainHttpWithKey_Refused()
    {
        Assert.Contains("HTTPS", RerankerEndpointGuard.Check("http://10.0.0.5:8080", "k", Sidecar), StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_RemoteHttpsWithKey_Allowed()
    {
        Assert.Null(RerankerEndpointGuard.Check("https://rerank.example.net", "k", Sidecar));
    }
}
````

- [ ] **Step 2: Run it and confirm it fails**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.RerankerConfigurationTests"
```

Expected: FAIL — the build fails: `RerankerOptions`, `RerankerAccess`, `RerankerGrant` and `RerankerEndpointGuard` do not exist yet.

- [ ] **Step 3: Implement**

**Create** `src/Cortex.Contained.Agent.Host/Reranking/RerankerAccess.cs`:

````csharp
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>
/// What the Bridge granted: whether reranking is enabled and the sidecar's API key. Held in memory
/// only, like every other credential; until the Bridge pushes a grant reranking is off.
/// </summary>
public sealed class RerankerAccess
{
    private volatile Snapshot current = new(false, null);

    /// <summary>True when the Bridge enabled reranking.</summary>
    public bool Enabled => this.current.Enabled;

    /// <summary>The sidecar API key, or null.</summary>
    public string? ApiKey => this.current.ApiKey;

    /// <summary>Replaces the grant (every Bridge connect pushes one).</summary>
    public void Apply(RerankerGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        this.current = new Snapshot(grant.Enabled, string.IsNullOrWhiteSpace(grant.ApiKey) ? null : grant.ApiKey);
    }

    private sealed record Snapshot(bool Enabled, string? ApiKey);
}
````

**Create** `src/Cortex.Contained.Agent.Host/Reranking/RerankerEndpointGuard.cs`:

````csharp
namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>
/// Reranker exposure rule (§4.3, same spirit as the history embedder guard): candidate text goes only
/// to the in-compose sidecar (a host in <see cref="RerankerOptions.AllowedEndpointHosts"/>) or to a
/// remote endpoint over HTTPS, and <b>always</b> with an API key — the sidecar requires authentication.
/// </summary>
public static class RerankerEndpointGuard
{
    /// <summary>Returns why text must not be sent to <paramref name="endpoint"/>, or null when it may.</summary>
    public static string? Check(string? endpoint, string? apiKey, IReadOnlyList<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return "the reranker endpoint is not a valid URL";
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            return "no reranker API key (the Bridge provides it; the sidecar requires authentication)";
        }

        if (allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"reranker host '{uri.Host}' is not an allowed in-compose sidecar ({string.Join(", ", allowedHosts)}); a remote reranker needs HTTPS";
    }
}
````

**Create** `src/Cortex.Contained.Agent.Host/Reranking/RerankerOptions.cs`:

````csharp
using System.Globalization;

namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>
/// How many fused candidates are reranked and how much of each one's text the reranker sees.
/// Only the first <see cref="TopN"/> candidates are reordered; the rest keep the fused order.
/// </summary>
/// <param name="TopN">Fused candidates sent to the reranker (1–50).</param>
/// <param name="MaxDocChars">Characters of raw best-matching lines per candidate (200–4000).</param>
public sealed record RerankBudget(int TopN, int MaxDocChars)
{
    /// <summary>Smallest accepted <see cref="TopN"/>.</summary>
    public const int MinTopN = 1;

    /// <summary>Largest accepted <see cref="TopN"/>.</summary>
    public const int MaxTopN = 50;

    /// <summary>Smallest accepted <see cref="MaxDocChars"/>.</summary>
    public const int MinDocChars = 200;

    /// <summary>Largest accepted <see cref="MaxDocChars"/>.</summary>
    public const int MaxDocCharsLimit = 4000;

    /// <summary>Stable text form, used in cursor bindings and reports.</summary>
    public string Label => string.Create(CultureInfo.InvariantCulture, $"top{this.TopN}-chars{this.MaxDocChars}");

    /// <summary>Returns validation errors; <paramref name="prefix"/> names the configuration section.</summary>
    public IReadOnlyList<string> Validate(string prefix)
    {
        var errors = new List<string>();
        if (this.TopN is < MinTopN or > MaxTopN)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{prefix}:TopN must be in [{MinTopN}, {MaxTopN}]."));
        }

        if (this.MaxDocChars is < MinDocChars or > MaxDocCharsLimit)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{prefix}:MaxDocChars must be in [{MinDocChars}, {MaxDocCharsLimit}]."));
        }

        return errors;
    }
}

/// <summary>Per-surface reranking settings (<c>Reranker:History</c>, <c>Reranker:Memory</c>).</summary>
public sealed class RerankSurfaceOptions
{
    /// <summary>Reranks this surface's hybrid results while the Bridge has the reranker enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Fused candidates reranked per search.</summary>
    public int TopN { get; set; } = 15;

    /// <summary>Characters of raw best-matching lines per candidate.</summary>
    public int MaxDocChars { get; set; } = 1200;

    /// <summary>The budget these settings describe.</summary>
    public RerankBudget Budget() => new(this.TopN, this.MaxDocChars);
}

/// <summary>
/// Agent-side reranker settings, bound from <c>Reranker</c>. Whether reranking runs at all is the
/// Bridge's decision (<c>Reranker:Enabled</c> in the Bridge configuration, default false): the Bridge
/// starts the sidecar and pushes the switch and the API key (<see cref="RerankerAccess"/>).
/// </summary>
public sealed class RerankerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Reranker";

    /// <summary>Base URL of the llama.cpp server (the in-compose sidecar by default).</summary>
    public string Endpoint { get; set; } = "http://reranker:8080";

    /// <summary>Model alias sent with every request (the sidecar's <c>--alias</c>).</summary>
    public string Model { get; set; } = "qwen3-reranker-0.6b";

    /// <summary>Comma-separated hosts candidate text may be sent to over plain HTTP (the in-compose sidecar).</summary>
    public string AllowedEndpointHosts { get; set; } = "reranker";

    /// <summary>Budget for one rerank call; on timeout the fused order is kept.</summary>
    public int TimeoutMs { get; set; } = 2000;

    /// <summary>Consecutive failures that open the circuit.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>How long an open circuit skips reranking before one trial call.</summary>
    public int BreakSeconds { get; set; } = 60;

    /// <summary><c>history_search</c> (hybrid relevance order).</summary>
    public RerankSurfaceOptions History { get; set; } = new();

    /// <summary><c>memory_search</c> (hybrid mode).</summary>
    public RerankSurfaceOptions Memory { get; set; } = new();

    /// <summary>Parsed <see cref="AllowedEndpointHosts"/>.</summary>
    public IReadOnlyList<string> AllowedHosts() =>
        this.AllowedEndpointHosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Returns validation errors.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(this.Endpoint, UriKind.Absolute, out _))
        {
            errors.Add("Reranker:Endpoint must be an absolute URL.");
        }

        if (string.IsNullOrWhiteSpace(this.Model))
        {
            errors.Add("Reranker:Model must not be empty.");
        }

        if (this.TimeoutMs is < 100 or > 30_000)
        {
            errors.Add("Reranker:TimeoutMs must be in [100, 30000].");
        }

        if (this.FailureThreshold < 1 || this.BreakSeconds < 1)
        {
            errors.Add("Reranker:FailureThreshold and BreakSeconds must be positive.");
        }

        errors.AddRange(this.History.Budget().Validate("Reranker:History"));
        errors.AddRange(this.Memory.Budget().Validate("Reranker:Memory"));
        return errors;
    }
}
````

**Create** `src/Cortex.Contained.Contracts/Hub/RerankerGrant.cs`:

````csharp
namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// The reranker switch and the <c>cortex-reranker</c> sidecar's API key, pushed by the Bridge on every
/// connect (the key is generated once and kept in DPAPI storage). The agent holds it in memory only.
/// </summary>
public sealed record RerankerGrant
{
    /// <summary>Reranking is enabled in the Bridge configuration (<c>Reranker:Enabled</c>).</summary>
    public bool Enabled { get; init; }

    /// <summary>The sidecar's API key; null when reranking is disabled.</summary>
    public string? ApiKey { get; init; }
}
````

- [ ] **Step 4: Run the tests and confirm they pass**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.RerankerConfigurationTests"
```

Expected: PASS — `Passed: 7`.

- [ ] **Step 5: Commit** (never stage `version.json`)

```powershell
git add src/Cortex.Contained.Agent.Host/Reranking/RerankerAccess.cs src/Cortex.Contained.Agent.Host/Reranking/RerankerEndpointGuard.cs src/Cortex.Contained.Agent.Host/Reranking/RerankerOptions.cs src/Cortex.Contained.Contracts/Hub/RerankerGrant.cs tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankerConfigurationTests.cs
git commit -m "feat(reranker): reranker settings, Bridge grant, in-memory access and exposure guard" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 2: Raw best-matching lines for the reranker

**Model:** cheap

I5-8 (§4.3 "raw best-matching lines only"). `RerankDocument.Build` keeps the lines sharing the most folded query words first, within `MaxDocChars` (cutting a single over-long first line), and emits them in their original order — no labels, timestamps or ids. Uses the shared `TextFolder`/`KeywordTokenizer`.

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Reranking/RerankDocument.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankDocumentTests.cs`

- [ ] **Step 1: Write the failing test**

**Create** `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankDocumentTests.cs`:

````csharp
using Cortex.Contained.Agent.Host.Reranking;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary>The reranker sees raw best-matching lines only, within the character budget.</summary>
public sealed class RerankDocumentTests
{
    [Fact]
    public void Build_KeepsBestMatchingLinesWithinBudget_InOriginalOrder()
    {
        string[] lines = ["hello there", "the night train to TownB leaves at 22:10", "ok", "train tickets are booked"];

        var document = RerankDocument.Build("night train", lines, 66);

        Assert.Equal("the night train to TownB leaves at 22:10\ntrain tickets are booked", document);
    }

    [Fact]
    public void Build_FirstLineLongerThanBudget_IsCut()
    {
        var document = RerankDocument.Build("budget", [new string('x', 300) + " budget"], 200);

        Assert.Equal(200, document.Length);
    }

    [Fact]
    public void Build_FoldsQueryAndLines_AndFlattensLineBreaks()
    {
        string[] lines = ["unrelated", "Køreplan\nfor toget"];

        var document = RerankDocument.Build("koreplan", lines, 15);

        Assert.Equal("Køreplan for toget"[..15], document);
    }
}
````

- [ ] **Step 2: Run it and confirm it fails**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.RerankDocumentTests"
```

Expected: FAIL — the build fails: `RerankDocument` does not exist yet.

- [ ] **Step 3: Implement**

**Create** `src/Cortex.Contained.Agent.Host/Reranking/RerankDocument.cs`:

````csharp
namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>
/// Builds the text the reranker sees for one candidate (§4.3): the raw best-matching lines only — no
/// labels, timestamps, ids or summaries. Lines sharing the most folded query words are kept first,
/// within <c>maxChars</c>, and emitted in their original order. Deterministic.
/// </summary>
public static class RerankDocument
{
    /// <summary>The candidate document for <paramref name="query"/> from <paramref name="lines"/>.</summary>
    public static string Build(string query, IReadOnlyList<string> lines, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);

        var queryTerms = KeywordTokenizer.Tokenize(TextFolder.Fold(query)).ToHashSet(StringComparer.Ordinal);
        var candidates = lines
            .Select((line, index) => (Index: index, Text: line.ReplaceLineEndings(" ").Trim()))
            .Where(l => l.Text.Length > 0)
            .Select(l => (l.Index, l.Text, Overlap: KeywordTokenizer.Tokenize(TextFolder.Fold(l.Text)).Distinct(StringComparer.Ordinal).Count(queryTerms.Contains)))
            .OrderByDescending(l => l.Overlap)
            .ThenBy(l => l.Index)
            .ToList();

        var chosen = new List<(int Index, string Text)>();
        var used = 0;
        foreach (var (index, text, _) in candidates)
        {
            var separator = chosen.Count == 0 ? 0 : 1;
            if (used + separator + text.Length <= maxChars)
            {
                chosen.Add((index, text));
                used += separator + text.Length;
            }
            else if (chosen.Count == 0)
            {
                chosen.Add((index, text[..maxChars]));
                used = maxChars;
            }
        }

        return string.Join('\n', chosen.OrderBy(c => c.Index).Select(c => c.Text));
    }
}
````

- [ ] **Step 4: Run the tests and confirm they pass**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.RerankDocumentTests"
```

Expected: PASS — `Passed: 3`.

- [ ] **Step 5: Commit** (never stage `version.json`)

```powershell
git add src/Cortex.Contained.Agent.Host/Reranking/RerankDocument.cs tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankDocumentTests.cs
git commit -m "feat(reranker): candidate documents from raw best-matching lines within a character budget" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 3: llama-server reranker client

**Model:** standard

I5-1, I5-4. `IReranker` and `LlamaServerReranker`: `POST {Endpoint}/v1/rerank` with the Jina shape (`model`, `query`, `documents`, `top_n`) and `Authorization: Bearer`, response `results[{index, relevance_score}]` validated (range, duplicates, finite). The exposure guard is re-checked on every call, so nothing is sent without a key or to a disallowed host; error messages never contain text. The client uses the named HttpClient `reranker` (registered without request logging in Task 7).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Reranking/IReranker.cs`
- Create: `src/Cortex.Contained.Agent.Host/Reranking/LlamaServerReranker.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Reranking/LlamaServerRerankerTests.cs`

- [ ] **Step 1: Write the failing test**

**Create** `tests/Cortex.Contained.Agent.Host.Tests/Reranking/LlamaServerRerankerTests.cs`:

````csharp
using System.Net;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Reranking;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary>The llama-server client: request shape, authentication, response validation, exposure guard.</summary>
public sealed class LlamaServerRerankerTests : IDisposable
{
    private readonly StubHandler handler = new();
    private readonly RerankerOptions options = new();
    private string? key = "secret-key";

    public void Dispose() => this.handler.Dispose();

    private LlamaServerReranker Sut()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(LlamaServerReranker.HttpClientName).Returns(_ => new HttpClient(this.handler, disposeHandler: false));
        var monitor = Substitute.For<IOptionsMonitor<RerankerOptions>>();
        monitor.CurrentValue.Returns(this.options);
        return new LlamaServerReranker(factory, monitor, () => this.key);
    }

    [Fact]
    public async Task RerankAsync_PostsJinaRequestWithBearerKey_AndParsesScores()
    {
        this.handler.Respond(HttpStatusCode.OK, """{"model":"m","results":[{"index":1,"relevance_score":0.9},{"index":0,"relevance_score":0.1}]}""");

        var scores = await this.Sut().RerankAsync("night train", ["coffee", "train at 22:10"], CancellationToken.None);

        Assert.Equal([new RerankScore(1, 0.9), new RerankScore(0, 0.1)], scores);
        var request = Assert.Single(this.handler.Requests);
        Assert.Equal("http://reranker:8080/v1/rerank", request.Uri);
        Assert.Equal("Bearer secret-key", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("qwen3-reranker-0.6b", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("night train", body.RootElement.GetProperty("query").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("documents").GetArrayLength());
        Assert.Equal(2, body.RootElement.GetProperty("top_n").GetInt32());
    }

    [Fact]
    public async Task RerankAsync_Unauthorized_ThrowsWithoutText()
    {
        this.handler.Respond(HttpStatusCode.Unauthorized, """{"error":"invalid key"}""");

        var ex = await Assert.ThrowsAsync<RerankerException>(() => this.Sut().RerankAsync("private words", ["secret doc"], CancellationToken.None));

        Assert.Equal("The reranker rejected the API key.", ex.Message);
    }

    [Fact]
    public async Task RerankAsync_IndexOutOfRange_Throws()
    {
        this.handler.Respond(HttpStatusCode.OK, """{"results":[{"index":5,"relevance_score":0.9}]}""");

        await Assert.ThrowsAsync<RerankerException>(() => this.Sut().RerankAsync("q", ["a", "b"], CancellationToken.None));
    }

    [Fact]
    public async Task RerankAsync_NoKey_SendsNothing()
    {
        this.key = null;

        var ex = await Assert.ThrowsAsync<RerankerException>(() => this.Sut().RerankAsync("q", ["a"], CancellationToken.None));

        Assert.Contains("API key", ex.Message, StringComparison.Ordinal);
        Assert.Empty(this.handler.Requests);
    }

    [Fact]
    public async Task RerankAsync_EndpointChangedToRemotePlainHttp_SendsNothing()
    {
        var sut = this.Sut();
        this.options.Endpoint = "http://192.168.1.20:8080";

        await Assert.ThrowsAsync<RerankerException>(() => sut.RerankAsync("q", ["a"], CancellationToken.None));

        Assert.Empty(this.handler.Requests);
        Assert.NotNull(sut.UnavailableReason);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode status = HttpStatusCode.OK;
        private string body = "{}";

        public List<(string Uri, string? Authorization, string Body)> Requests { get; } = [];

        public void Respond(HttpStatusCode code, string json)
        {
            this.status = code;
            this.body = json;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            this.Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), content));
            return new HttpResponseMessage(this.status) { Content = new StringContent(this.body, Encoding.UTF8, "application/json") };
        }
    }
}
````

- [ ] **Step 2: Run it and confirm it fails**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.LlamaServerRerankerTests"
```

Expected: FAIL — the build fails: `IReranker`, `RerankerException` and `LlamaServerReranker` do not exist yet.

- [ ] **Step 3: Implement**

**Create** `src/Cortex.Contained.Agent.Host/Reranking/IReranker.cs`:

````csharp
namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>The reranker's relevance score for one document of a request.</summary>
/// <param name="Index">Zero-based index of the document in the request.</param>
/// <param name="Score">Relevance; higher is better.</param>
public sealed record RerankScore(int Index, double Score);

/// <summary>A cross-encoder reranker (the <c>cortex-reranker</c> sidecar in production).</summary>
public interface IReranker
{
    /// <summary>Why no text may be sent right now (exposure guard, missing key), or null when it may.</summary>
    string? UnavailableReason { get; }

    /// <summary>Scores every document against <paramref name="query"/>; throws <see cref="RerankerException"/> on failure.</summary>
    Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken);
}

/// <summary>A reranker call failed. Messages never contain query or document text.</summary>
public sealed class RerankerException : Exception
{
    public RerankerException()
    {
    }

    public RerankerException(string message)
        : base(message)
    {
    }

    public RerankerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
````

**Create** `src/Cortex.Contained.Agent.Host/Reranking/LlamaServerReranker.cs`:

````csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>
/// <see cref="IReranker"/> over llama.cpp <c>llama-server --reranking</c> (<c>POST /v1/rerank</c>,
/// Jina request/response shape, <c>Authorization: Bearer</c>). Every call re-checks
/// <see cref="RerankerEndpointGuard"/>, so a runtime endpoint change can never leak text, and nothing
/// of the request or response body is logged or put into exception messages.
/// </summary>
public sealed class LlamaServerReranker : IReranker
{
    /// <summary>Named <see cref="HttpClient"/> (registered without request logging).</summary>
    public const string HttpClientName = "reranker";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptionsMonitor<RerankerOptions> options;
    private readonly Func<string?> apiKey;

    public LlamaServerReranker(IHttpClientFactory httpClientFactory, IOptionsMonitor<RerankerOptions> options, RerankerAccess access)
        : this(httpClientFactory, options, () => access?.ApiKey)
    {
        ArgumentNullException.ThrowIfNull(access);
    }

    /// <summary>Uses an explicit key source (evaluation and smoke CLIs read it from the environment).</summary>
    public LlamaServerReranker(IHttpClientFactory httpClientFactory, IOptionsMonitor<RerankerOptions> options, Func<string?> apiKey)
    {
        this.httpClientFactory = httpClientFactory;
        this.options = options;
        this.apiKey = apiKey;
    }

    /// <inheritdoc />
    public string? UnavailableReason
    {
        get
        {
            var settings = this.options.CurrentValue;
            return RerankerEndpointGuard.Check(settings.Endpoint, this.apiKey(), settings.AllowedHosts());
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0)
        {
            return [];
        }

        var settings = this.options.CurrentValue;
        var key = this.apiKey();
        if (RerankerEndpointGuard.Check(settings.Endpoint, key, settings.AllowedHosts()) is { } reason)
        {
            throw new RerankerException("Text was not sent to the reranker: " + reason + ".");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.Endpoint.TrimEnd('/') + "/v1/rerank"))
        {
            Content = JsonContent.Create(new RerankRequest(settings.Model, query, documents, documents.Count)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var client = this.httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RerankerException("The reranker is unreachable.", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new RerankerException("The reranker rejected the API key.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new RerankerException($"The reranker answered HTTP {(int)response.StatusCode}.");
            }

            RerankResponse? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<RerankResponse>(cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new RerankerException("The reranker returned malformed JSON.", ex);
            }

            return Validate(body, documents.Count);
        }
    }

    private static List<RerankScore> Validate(RerankResponse? body, int count)
    {
        if (body?.Results is not { } results)
        {
            throw new RerankerException("The reranker response has no results.");
        }

        var seen = new HashSet<int>();
        var scores = new List<RerankScore>(results.Count);
        foreach (var result in results)
        {
            if (result.Index < 0 || result.Index >= count || !seen.Add(result.Index) || !double.IsFinite(result.RelevanceScore))
            {
                throw new RerankerException("The reranker response has an invalid or duplicate index or score.");
            }

            scores.Add(new RerankScore(result.Index, result.RelevanceScore));
        }

        return scores;
    }

    private sealed record RerankRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("documents")] IReadOnlyList<string> Documents,
        [property: JsonPropertyName("top_n")] int TopN);

    private sealed record RerankResponse([property: JsonPropertyName("results")] IReadOnlyList<RerankResult>? Results);

    private sealed record RerankResult(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("relevance_score")] double RelevanceScore);
}
````

- [ ] **Step 4: Run the tests and confirm they pass**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.LlamaServerRerankerTests"
```

Expected: PASS — `Passed: 5`.

- [ ] **Step 5: Commit** (never stage `version.json`)

```powershell
git add src/Cortex.Contained.Agent.Host/Reranking/IReranker.cs src/Cortex.Contained.Agent.Host/Reranking/LlamaServerReranker.cs tests/Cortex.Contained.Agent.Host.Tests/Reranking/LlamaServerRerankerTests.cs
git commit -m "feat(reranker): llama-server rerank client with bearer key and per-call exposure guard" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 4: Circuit breaker and rerank stage

**Model:** standard

I5-9, I5-11. `RerankCircuitBreaker` (consecutive failures open it for a break, then one half-open trial) and `RerankStage`/`IRerankStage`: `BudgetFor(surface)` (null until the Bridge enabled reranking, per-surface switch), and `RerankAsync` — fewer than two candidates, an unavailable reranker or an open circuit skip; the first `TopN` candidates become documents (Task 2) and are reordered by score with the rest in fused order; a timeout or `RerankerException` keeps the fused order. Every outcome carries the honest coverage note. `Snapshot()` is the `/health` block. `FakeReranker` is the test double used by every later task (no sidecar in tests).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Reranking/RerankCircuitBreaker.cs`
- Create: `src/Cortex.Contained.Agent.Host/Reranking/RerankStage.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Reranking/FakeReranker.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankCircuitBreakerTests.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankStageTests.cs`

- [ ] **Step 1: Write the failing test**

**Create** `tests/Cortex.Contained.Agent.Host.Tests/Reranking/FakeReranker.cs`:

````csharp
using Cortex.Contained.Agent.Host.Reranking;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary>Deterministic test reranker (no sidecar): scores each document with <see cref="Score"/>.</summary>
internal sealed class FakeReranker : IReranker
{
    public string? UnavailableReason { get; set; }

    /// <summary>Relevance of a document for a query; by default the number of query words it contains.</summary>
    public Func<string, string, double> Score { get; set; } = (query, document) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(word => document.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>When set, every call throws it.</summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, every call waits this long first (cancellable).</summary>
    public TimeSpan? Delay { get; set; }

    public List<(string Query, IReadOnlyList<string> Documents)> Calls { get; } = [];

    public async Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        this.Calls.Add((query, documents));
        if (this.Delay is { } delay)
        {
            await Task.Delay(delay, cancellationToken);
        }

        if (this.Failure is { } failure)
        {
            throw failure;
        }

        return documents.Select((document, index) => new RerankScore(index, this.Score(query, document))).ToList();
    }
}
````

**Create** `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankCircuitBreakerTests.cs`:

````csharp
using Cortex.Contained.Agent.Host.Reranking;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary>The consecutive-failure circuit breaker.</summary>
public sealed class RerankCircuitBreakerTests
{
    private static readonly TimeSpan Break = TimeSpan.FromSeconds(60);
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Failures_BelowThreshold_StayClosed_ThresholdOpens()
    {
        var breaker = new RerankCircuitBreaker(this.time);

        Assert.False(breaker.Failure(3, Break));
        Assert.False(breaker.Failure(3, Break));
        Assert.True(breaker.Failure(3, Break));

        Assert.Equal(RerankCircuitState.Open, breaker.State);
        Assert.False(breaker.TryEnter());
    }

    [Fact]
    public void BreakOver_AllowsOneTrial_SuccessCloses()
    {
        var breaker = new RerankCircuitBreaker(this.time);
        breaker.Failure(1, Break);
        this.time.Advance(Break);

        Assert.True(breaker.TryEnter());
        Assert.False(breaker.TryEnter());
        breaker.Success();

        Assert.Equal(RerankCircuitState.Closed, breaker.State);
        Assert.Equal(0, breaker.ConsecutiveFailures);
    }

    [Fact]
    public void FailedTrial_ReopensImmediately()
    {
        var breaker = new RerankCircuitBreaker(this.time);
        breaker.Failure(1, Break);
        this.time.Advance(Break);
        Assert.True(breaker.TryEnter());

        Assert.True(breaker.Failure(10, Break));
        Assert.Equal(RerankCircuitState.Open, breaker.State);
    }
}
````

**Create** `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankStageTests.cs`:

````csharp
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary>The reranking stage: budget, honest notes, timeout, circuit breaker, health.</summary>
public sealed class RerankStageTests
{
    private static readonly RerankBudget Budget = new(3, 600);
    private readonly FakeReranker reranker = new();
    private readonly RerankerOptions options = new() { TimeoutMs = 200, FailureThreshold = 2, BreakSeconds = 60 };
    private readonly RerankerAccess access = new();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly RerankStage stage;

    public RerankStageTests()
    {
        var monitor = Substitute.For<IOptionsMonitor<RerankerOptions>>();
        monitor.CurrentValue.Returns(this.options);
        this.access.Apply(new RerankerGrant { Enabled = true, ApiKey = "k" });
        this.stage = new RerankStage(this.reranker, monitor, this.access, NullLogger<RerankStage>.Instance, this.time);
    }

    private static List<IReadOnlyList<string>> Candidates(params string[] texts) => texts.Select(t => (IReadOnlyList<string>)[t]).ToList();

    [Fact]
    public async Task RerankAsync_ReordersOnlyTheTopN_RestKeepsFusedOrder()
    {
        var outcome = await this.stage.RerankAsync("train", Candidates("coffee", "budget", "train times", "train again"), Budget, CancellationToken.None);

        Assert.Equal(RerankStatus.Applied, outcome.Status);
        Assert.Equal([2, 0, 1, 3], outcome.Order);
        Assert.Equal(3, this.reranker.Calls[0].Documents.Count);
        Assert.StartsWith("on (top 3 reordered", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void BudgetFor_OffUntilTheBridgeEnablesIt_AndPerSurface()
    {
        this.access.Apply(new RerankerGrant { Enabled = false });
        Assert.Null(this.stage.BudgetFor(RerankSurface.History));

        this.access.Apply(new RerankerGrant { Enabled = true, ApiKey = "k" });
        this.options.Memory.Enabled = false;

        Assert.Equal(new RerankBudget(15, 1200), this.stage.BudgetFor(RerankSurface.History));
        Assert.Null(this.stage.BudgetFor(RerankSurface.Memory));
    }

    [Fact]
    public async Task RerankAsync_Timeout_KeepsFusedOrderAndSaysSo()
    {
        this.reranker.Delay = TimeSpan.FromSeconds(30);

        var outcome = await this.stage.RerankAsync("train", Candidates("a", "b", "c"), Budget, CancellationToken.None);

        Assert.Equal(RerankStatus.Failed, outcome.Status);
        Assert.Equal([0, 1, 2], outcome.Order);
        Assert.Equal("failed: timed out after 200 ms; fused order kept", outcome.Note);
    }

    [Fact]
    public async Task RerankAsync_RepeatedFailures_OpenTheCircuit_ThenSkipWithoutCalling()
    {
        this.reranker.Failure = new RerankerException("The reranker answered HTTP 500.");
        await this.stage.RerankAsync("q", Candidates("a", "b"), Budget, CancellationToken.None);
        await this.stage.RerankAsync("q", Candidates("a", "b"), Budget, CancellationToken.None);

        var outcome = await this.stage.RerankAsync("q", Candidates("a", "b"), Budget, CancellationToken.None);

        Assert.Equal(RerankStatus.Skipped, outcome.Status);
        Assert.StartsWith("skipped: circuit open after 2 failures", outcome.Note, StringComparison.Ordinal);
        Assert.Equal(2, this.reranker.Calls.Count);
        Assert.Equal("circuit-open", this.stage.Snapshot().State);
        Assert.Equal("The reranker answered HTTP 500", this.stage.Snapshot().LastError);
    }

    [Fact]
    public async Task RerankAsync_Unavailable_SkipsWithReason()
    {
        this.reranker.UnavailableReason = "no reranker API key";

        var outcome = await this.stage.RerankAsync("q", Candidates("a", "b"), Budget, CancellationToken.None);

        Assert.Equal("skipped: no reranker API key", outcome.Note);
        Assert.Empty(this.reranker.Calls);
    }

    [Fact]
    public async Task RerankAsync_SingleCandidate_NotSent()
    {
        var outcome = await this.stage.RerankAsync("q", Candidates("a"), Budget, CancellationToken.None);

        Assert.Equal(RerankStatus.Skipped, outcome.Status);
        Assert.Empty(this.reranker.Calls);
    }
}
````

- [ ] **Step 2: Run it and confirm it fails**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.RerankCircuitBreakerTests|FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.RerankStageTests"
```

Expected: FAIL — the build fails: `RerankCircuitBreaker`, `RerankStage`, `RerankOutcome` and `RerankStatus` do not exist yet.

- [ ] **Step 3: Implement**

**Create** `src/Cortex.Contained.Agent.Host/Reranking/RerankCircuitBreaker.cs`:

````csharp
namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>Circuit state of the reranker client.</summary>
public enum RerankCircuitState
{
    /// <summary>Calls go through.</summary>
    Closed,

    /// <summary>Too many consecutive failures: calls are skipped until the break ends.</summary>
    Open,

    /// <summary>The break ended: one trial call decides whether the circuit closes again.</summary>
    HalfOpen,
}

/// <summary>
/// Consecutive-failure circuit breaker: after <c>threshold</c> failures in a row reranking is skipped
/// for <c>breakDuration</c> (searches keep the fused order at no latency cost), then one trial call
/// closes or re-opens it. Thread-safe.
/// </summary>
public sealed class RerankCircuitBreaker
{
    private readonly TimeProvider time;
    private readonly object gate = new();
    private int consecutiveFailures;
    private DateTimeOffset? openUntil;
    private bool trialInFlight;

    public RerankCircuitBreaker(TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Current state.</summary>
    public RerankCircuitState State
    {
        get
        {
            lock (this.gate)
            {
                return this.openUntil is null ? RerankCircuitState.Closed
                    : this.time.GetUtcNow() < this.openUntil ? RerankCircuitState.Open
                    : RerankCircuitState.HalfOpen;
            }
        }
    }

    /// <summary>Failures since the last success.</summary>
    public int ConsecutiveFailures
    {
        get
        {
            lock (this.gate)
            {
                return this.consecutiveFailures;
            }
        }
    }

    /// <summary>End of the current break, or null when closed.</summary>
    public DateTimeOffset? OpenUntil
    {
        get
        {
            lock (this.gate)
            {
                return this.openUntil;
            }
        }
    }

    /// <summary>True when a call may go out now (closed, or the single half-open trial).</summary>
    public bool TryEnter()
    {
        lock (this.gate)
        {
            if (this.openUntil is null)
            {
                return true;
            }

            if (this.time.GetUtcNow() < this.openUntil || this.trialInFlight)
            {
                return false;
            }

            this.trialInFlight = true;
            return true;
        }
    }

    /// <summary>Records a successful call: the circuit closes.</summary>
    public void Success()
    {
        lock (this.gate)
        {
            this.consecutiveFailures = 0;
            this.openUntil = null;
            this.trialInFlight = false;
        }
    }

    /// <summary>Records a failed call; returns true when this failure opened (or re-opened) the circuit.</summary>
    public bool Failure(int threshold, TimeSpan breakDuration)
    {
        lock (this.gate)
        {
            this.consecutiveFailures++;
            var wasTrial = this.trialInFlight;
            this.trialInFlight = false;
            if (wasTrial || this.consecutiveFailures >= threshold)
            {
                this.openUntil = this.time.GetUtcNow() + breakDuration;
                return true;
            }

            return false;
        }
    }
}
````

**Create** `src/Cortex.Contained.Agent.Host/Reranking/RerankStage.cs`:

````csharp
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>The search surface a rerank belongs to (each has its own budget and switch).</summary>
public enum RerankSurface
{
    /// <summary><c>history_search</c>, hybrid relevance order.</summary>
    History,

    /// <summary><c>memory_search</c>, hybrid mode.</summary>
    Memory,
}

/// <summary>What happened to one rerank request.</summary>
public enum RerankStatus
{
    /// <summary>The first candidates were reordered by the reranker.</summary>
    Applied,

    /// <summary>The reranker was not called (nothing to reorder, unavailable, circuit open); fused order kept.</summary>
    Skipped,

    /// <summary>The call failed or timed out; fused order kept.</summary>
    Failed,
}

/// <summary>Result of <see cref="IRerankStage.RerankAsync"/>.</summary>
/// <param name="Order">A permutation of the candidate indices, best first.</param>
/// <param name="Status">Applied, skipped or failed.</param>
/// <param name="Note">The honest coverage text shown to the agent (<c>rerank: …</c>).</param>
/// <param name="Scores">Reranker score per reranked candidate index.</param>
/// <param name="LatencyMs">Time spent in the reranker call (0 when it was not called).</param>
public sealed record RerankOutcome(IReadOnlyList<int> Order, RerankStatus Status, string Note, IReadOnlyDictionary<int, double> Scores, double LatencyMs)
{
    /// <summary>The fused order unchanged, with a reason.</summary>
    public static RerankOutcome Unchanged(int count, RerankStatus status, string note, double latencyMs = 0) =>
        new(Enumerable.Range(0, count).ToList(), status, note, new Dictionary<int, double>(), latencyMs);
}

/// <summary>The <c>reranker</c> block of <c>/health</c>.</summary>
/// <param name="Enabled">The Bridge enabled reranking.</param>
/// <param name="State"><c>off</c>, <c>ok</c>, <c>circuit-open</c> or <c>unavailable: reason</c>.</param>
/// <param name="ConsecutiveFailures">Failures since the last success.</param>
/// <param name="CircuitOpenUntil">End of the current break, or null.</param>
/// <param name="LastError">Last failure (no text from searches), or null.</param>
/// <param name="LastLatencyMs">Latency of the last call, or null.</param>
/// <param name="LastCallAt">When the reranker was last called, or null.</param>
public sealed record RerankHealth(
    bool Enabled, string State, int ConsecutiveFailures, DateTimeOffset? CircuitOpenUntil, string? LastError, double? LastLatencyMs, DateTimeOffset? LastCallAt);

/// <summary>The reranking stage after fusion, shared by history and memory search.</summary>
public interface IRerankStage
{
    /// <summary>The configured budget of <paramref name="surface"/>, or null when reranking is off for it.</summary>
    RerankBudget? BudgetFor(RerankSurface surface);

    /// <summary>
    /// Reorders the first <see cref="RerankBudget.TopN"/> of <paramref name="candidates"/> (each given as its
    /// raw best-matching lines) by reranker relevance; never throws for reranker failures.
    /// </summary>
    Task<RerankOutcome> RerankAsync(string query, IReadOnlyList<IReadOnlyList<string>> candidates, RerankBudget budget, CancellationToken cancellationToken);
}

/// <summary>
/// The reranking stage: budget (top N candidates, raw best-matching lines capped per candidate),
/// exposure guard, per-call timeout and a consecutive-failure circuit breaker. A skipped or failed
/// rerank keeps the fused order and says so; it never fails the search.
/// </summary>
public sealed partial class RerankStage : IRerankStage
{
    private readonly IReranker reranker;
    private readonly IOptionsMonitor<RerankerOptions> options;
    private readonly RerankerAccess access;
    private readonly ILogger<RerankStage> logger;
    private readonly TimeProvider time;
    private readonly RerankCircuitBreaker breaker;
    private volatile LastCall? last;

    public RerankStage(IReranker reranker, IOptionsMonitor<RerankerOptions> options, RerankerAccess access, ILogger<RerankStage> logger, TimeProvider? time = null)
    {
        this.reranker = reranker;
        this.options = options;
        this.access = access;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
        this.breaker = new RerankCircuitBreaker(this.time);
    }

    /// <inheritdoc />
    public RerankBudget? BudgetFor(RerankSurface surface)
    {
        if (!this.access.Enabled)
        {
            return null;
        }

        var settings = this.options.CurrentValue;
        var surfaceOptions = surface == RerankSurface.History ? settings.History : settings.Memory;
        return surfaceOptions.Enabled ? surfaceOptions.Budget() : null;
    }

    /// <inheritdoc />
    public async Task<RerankOutcome> RerankAsync(
        string query, IReadOnlyList<IReadOnlyList<string>> candidates, RerankBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(budget);
        var count = candidates.Count;
        var take = Math.Min(budget.TopN, count);
        if (take < 2)
        {
            return RerankOutcome.Unchanged(count, RerankStatus.Skipped, "skipped: fewer than two candidates to reorder");
        }

        if (this.reranker.UnavailableReason is { } reason)
        {
            return RerankOutcome.Unchanged(count, RerankStatus.Skipped, "skipped: " + reason);
        }

        if (!this.breaker.TryEnter())
        {
            return RerankOutcome.Unchanged(count, RerankStatus.Skipped, string.Create(CultureInfo.InvariantCulture,
                $"skipped: circuit open after {this.breaker.ConsecutiveFailures} failures (next try after {this.breaker.OpenUntil:HH:mm:ss}Z)"));
        }

        var settings = this.options.CurrentValue;
        var documents = candidates.Take(take).Select(lines => RerankDocument.Build(query, lines, budget.MaxDocChars)).ToList();
        var started = this.time.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.TimeoutMs);
        try
        {
            var scores = await this.reranker.RerankAsync(query, documents, timeout.Token).ConfigureAwait(false);
            var latency = this.time.GetElapsedTime(started).TotalMilliseconds;
            this.breaker.Success();
            this.last = new LastCall(this.time.GetUtcNow(), latency, null);
            var byIndex = scores.ToDictionary(s => s.Index, s => s.Score);
            var head = Enumerable.Range(0, take)
                .OrderByDescending(i => byIndex.TryGetValue(i, out var score) ? score : double.NegativeInfinity)
                .ThenBy(i => i);
            return new RerankOutcome(
                [.. head, .. Enumerable.Range(take, count - take)],
                RerankStatus.Applied,
                string.Create(CultureInfo.InvariantCulture, $"on (top {take} reordered, ≤{budget.MaxDocChars} chars of matching lines each)"),
                byIndex,
                latency);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return this.Failed(count, started, string.Create(CultureInfo.InvariantCulture, $"timed out after {settings.TimeoutMs} ms"), settings);
        }
        catch (RerankerException ex)
        {
            return this.Failed(count, started, ex.Message.TrimEnd('.'), settings);
        }
    }

    /// <summary>The <c>/health</c> block.</summary>
    public RerankHealth Snapshot()
    {
        var last = this.last;
        var state = !this.access.Enabled ? "off"
            : this.reranker.UnavailableReason is { } reason ? "unavailable: " + reason
            : this.breaker.State == RerankCircuitState.Open ? "circuit-open"
            : "ok";
        return new RerankHealth(this.access.Enabled, state, this.breaker.ConsecutiveFailures, this.breaker.OpenUntil, last?.Error, last?.LatencyMs, last?.At);
    }

    private RerankOutcome Failed(int count, long started, string error, RerankerOptions settings)
    {
        var latency = this.time.GetElapsedTime(started).TotalMilliseconds;
        this.last = new LastCall(this.time.GetUtcNow(), latency, error);
        if (this.breaker.Failure(settings.FailureThreshold, TimeSpan.FromSeconds(settings.BreakSeconds)))
        {
            this.LogCircuitOpened(this.breaker.ConsecutiveFailures, settings.BreakSeconds, error);
        }
        else
        {
            this.LogRerankFailed(error);
        }

        return RerankOutcome.Unchanged(count, RerankStatus.Failed, "failed: " + error + "; fused order kept", latency);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rerank failed, fused order kept: {Error}")]
    private partial void LogRerankFailed(string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reranker circuit opened after {Failures} consecutive failures for {BreakSeconds}s: {Error}")]
    private partial void LogCircuitOpened(int failures, int breakSeconds, string error);

    private sealed record LastCall(DateTimeOffset At, double LatencyMs, string? Error);
}
````

- [ ] **Step 4: Run the tests and confirm they pass**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.RerankCircuitBreakerTests|FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.RerankStageTests"
```

Expected: PASS — `Passed: 9`.

- [ ] **Step 5: Commit** (never stage `version.json`)

```powershell
git add src/Cortex.Contained.Agent.Host/Reranking/RerankCircuitBreaker.cs src/Cortex.Contained.Agent.Host/Reranking/RerankStage.cs tests/Cortex.Contained.Agent.Host.Tests/Reranking/FakeReranker.cs tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankCircuitBreakerTests.cs tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankStageTests.cs
git commit -m "feat(reranker): rerank stage with budget, timeout, circuit breaker and honest outcome notes" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 5: Rerank stage after hybrid fusion in `history_search`

**Model:** standard

I5-7, I5-8, I5-9, I5-10. `HistoryRecallService` takes an optional `IRerankStage` (last constructor parameter, so every existing call site is unchanged). In hybrid relevance order the fused head is reranked using each segment's verified shown rows, **inside the read lease**; pages after the head make no call; hits carry `RerankScore`; the coverage gets `Rerank` (rendered as a `rerank:` line) and the result the `RerankOutcome` (evaluation latency). `HistorySearchRequest.Rerank` (`RerankRequest`: configured/off/explicit budget) lets evaluation runs choose the budget. The cursor binding includes the budget label only when reranking applies. Keyword and time order are never reranked.

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/History/HistoryCursorCodec.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/HistoryRecallModels.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/HistoryRecallService.Hybrid.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/HistoryRecallService.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/HistoryResultRenderer.cs`
- Create: `src/Cortex.Contained.Agent.Host/Reranking/RerankRequest.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Reranking/HistoryRerankTests.cs`

- [ ] **Step 1: Write the failing test**

**Create** `tests/Cortex.Contained.Agent.Host.Tests/Reranking/HistoryRerankTests.cs`:

````csharp
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Tests.History;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary><c>history_search</c> hybrid recall with the rerank stage after fusion (§4.3).</summary>
public sealed class HistoryRerankTests : IAsyncDisposable
{
    private readonly FakeHistoryEmbedder embedder = new();
    private readonly FakeReranker reranker = new();
    private readonly RerankerOptions rerankerOptions = new();
    private readonly RerankerAccess access = new();
    private readonly HistoryHarness h;
    private readonly HistoryRecallService recall;

    public HistoryRerankTests()
    {
        var options = new HistoryOptions { Enabled = true, TimeZone = "UTC" };
        options.Dense.Enabled = true;
        this.h = new HistoryHarness(options, this.embedder);
        this.rerankerOptions.History.TopN = 2;
        var monitor = Substitute.For<IOptionsMonitor<RerankerOptions>>();
        monitor.CurrentValue.Returns(this.rerankerOptions);
        this.access.Apply(new RerankerGrant { Enabled = true, ApiKey = "k" });
        var stage = new RerankStage(this.reranker, monitor, this.access, NullLogger<RerankStage>.Instance, this.h.Time);
        this.recall = new HistoryRecallService(this.h.Store, this.h.Projector, this.h.Gate, this.h.Status, this.h.Monitor, this.embedder, this.h.Time, stage);
    }

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    private async Task SeedAsync()
    {
        await this.h.SayAsync("trip budget: the railway journey costs 40", 0);
        await this.h.SayAsync("trip budget: hotel invoice is paid", 200);
        await this.h.SayAsync("trip budget: coffee with PersonA", 400);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        this.h.Time.Advance(TimeSpan.FromMinutes(3));
        while ((await this.h.Projector.EmbedPendingAsync(CancellationToken.None)).Published > 0)
        {
        }
    }

    private Task<HistorySearchResult> SearchAsync(HistorySearchMode mode = HistorySearchMode.Auto, int limit = 8, string? cursor = null) =>
        this.recall.SearchAsync(new HistorySearchRequest { Query = "trip budget", Mode = mode, Limit = limit, Cursor = cursor }, CancellationToken.None);

    [Fact]
    public async Task Hybrid_RerankOn_ReordersTheHead_WithRawLinesOnly()
    {
        await this.SeedAsync();
        var fused = await this.recall.SearchAsync(new HistorySearchRequest { Query = "trip budget", Rerank = RerankRequest.Off }, CancellationToken.None);
        var promoted = fused.Hits[1].Lines[0].Text;
        this.reranker.Score = (_, document) => document == promoted ? 1 : 0;

        var result = await this.SearchAsync();

        Assert.Equal(promoted, result.Hits[0].Lines[0].Text);
        Assert.Equal(1, result.Hits[0].RerankScore);
        Assert.StartsWith("on (top 2 reordered", result.Coverage.Rerank, StringComparison.Ordinal);
        Assert.Equal(fused.Hits[2].FirstMsgId, result.Hits[2].FirstMsgId);
        Assert.Null(result.Hits[2].RerankScore);
        Assert.All(this.reranker.Calls[0].Documents, d => Assert.StartsWith("trip budget:", d, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RerankFails_FusedOrderKept_AndCoverageSaysSo()
    {
        await this.SeedAsync();
        var fused = await this.recall.SearchAsync(new HistorySearchRequest { Query = "trip budget", Rerank = RerankRequest.Off }, CancellationToken.None);
        this.reranker.Failure = new RerankerException("The reranker is unreachable.");

        var result = await this.SearchAsync();

        Assert.Equal(fused.Hits.Select(x => x.FirstMsgId), result.Hits.Select(x => x.FirstMsgId));
        Assert.Equal("failed: The reranker is unreachable; fused order kept", result.Coverage.Rerank);
        var text = HistoryResultRenderer.RenderSearch(result, new HistorySearchRequest { Query = "trip budget" }, new HistoryRenderOptions(TimeZoneInfo.Utc, 1500, 200), "HBtest").Text;
        Assert.Contains("rerank: failed: The reranker is unreachable; fused order kept\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeywordMode_IsNotReranked_AndSaysWhy()
    {
        await this.SeedAsync();

        var result = await this.SearchAsync(HistorySearchMode.Keyword);

        Assert.Empty(this.reranker.Calls);
        Assert.Equal("not used: mode=keyword (reranking follows hybrid fusion)", result.Coverage.Rerank);
    }

    [Fact]
    public async Task BridgeDisabledReranking_NoCall_NoCoverageLine()
    {
        await this.SeedAsync();
        this.access.Apply(new RerankerGrant { Enabled = false });

        var result = await this.SearchAsync();

        Assert.Empty(this.reranker.Calls);
        Assert.Null(result.Coverage.Rerank);
    }

    [Fact]
    public async Task PageAfterTheRerankedHead_KeepsFusedOrderWithoutACall()
    {
        await this.SeedAsync();
        var first = await this.SearchAsync(limit: 2);

        var second = await this.SearchAsync(limit: 2, cursor: HistoryCursorCodec.Encode(first.Binding, first.Next));

        Assert.Single(this.reranker.Calls);
        Assert.Single(second.Hits);
        Assert.Equal("not used: this page starts after the reranked top 2", second.Coverage.Rerank);
    }

    [Fact]
    public async Task CursorFromAnUnrerankedSearch_IsStaleWhenRerankingApplies()
    {
        await this.SeedAsync();
        var plain = await this.recall.SearchAsync(new HistorySearchRequest { Query = "trip budget", Limit = 1, Rerank = RerankRequest.Off }, CancellationToken.None);

        var reranked = await this.SearchAsync(limit: 1, cursor: HistoryCursorCodec.Encode(plain.Binding, plain.Next));

        Assert.NotNull(reranked.Error);
    }
}
````

- [ ] **Step 2: Run it and confirm it fails**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.HistoryRerankTests"
```

Expected: FAIL — the build fails: `RerankRequest`, `HistorySearchRequest.Rerank`, `HistoryHit.RerankScore`, `HistoryCoverage.Rerank` and the new `HistoryRecallService` parameter do not exist yet.

- [ ] **Step 3: Implement**

**Modify** `src/Cortex.Contained.Agent.Host/History/HistoryCursorCodec.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/History/HistoryCursorCodec.cs b/src/Cortex.Contained.Agent.Host/History/HistoryCursorCodec.cs
index e6824a3..f5caa34 100644
--- a/src/Cortex.Contained.Agent.Host/History/HistoryCursorCodec.cs
+++ b/src/Cortex.Contained.Agent.Host/History/HistoryCursorCodec.cs
@@ -15,8 +15,12 @@ namespace Cortex.Contained.Agent.Host.History;
 /// </summary>
 public static class HistoryCursorCodec
 {
-    /// <summary>The binding of <paramref name="request"/> on an index generation.</summary>
-    public static string Binding(HistorySearchRequest request, long? generation)
+    /// <summary>
+    /// The binding of <paramref name="request"/> on an index generation. <paramref name="rerank"/> (the
+    /// effective rerank budget's label) is part of the binding only when reranking applies, so cursors of
+    /// searches without reranking are unchanged.
+    /// </summary>
+    public static string Binding(HistorySearchRequest request, long? generation, string? rerank = null)
     {
         ArgumentNullException.ThrowIfNull(request);
         var text = string.Join(
@@ -31,6 +35,11 @@ public static class HistoryCursorCodec
             request.Limit.ToString(CultureInfo.InvariantCulture),
             generation?.ToString(CultureInfo.InvariantCulture) ?? "none",
             request.Mode.ToString());
+        if (rerank is not null)
+        {
+            text += "\u001frerank=" + rerank;
+        }
+
         return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
     }
 
````

**Modify** `src/Cortex.Contained.Agent.Host/History/HistoryRecallModels.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/History/HistoryRecallModels.cs b/src/Cortex.Contained.Agent.Host/History/HistoryRecallModels.cs
index 7c059e9..a18594d 100644
--- a/src/Cortex.Contained.Agent.Host/History/HistoryRecallModels.cs
+++ b/src/Cortex.Contained.Agent.Host/History/HistoryRecallModels.cs
@@ -1,3 +1,4 @@
+using Cortex.Contained.Agent.Host.Reranking;
 using Cortex.Contained.Agent.Host.Storage;
 
 namespace Cortex.Contained.Agent.Host.History;
@@ -34,6 +35,9 @@ public sealed record HistorySearchRequest
 
     /// <summary>Retrieval mode for relevance order; time order is always keyword (exhaustive).</summary>
     public HistorySearchMode Mode { get; init; } = HistorySearchMode.Auto;
+
+    /// <summary>Reranking after hybrid fusion: the configured budget unless an evaluation run sets one.</summary>
+    public RerankRequest Rerank { get; init; } = RerankRequest.Configured;
 }
 
 /// <summary>Retrieval mode of <c>history_search</c> (§4.2).</summary>
@@ -111,6 +115,9 @@ public sealed record HistoryHit(
 {
     /// <summary>Which retriever located the hit.</summary>
     public HistoryMatchKind MatchedBy { get; init; } = HistoryMatchKind.Keyword;
+
+    /// <summary>The reranker's relevance score when this hit was among the reranked candidates, else null.</summary>
+    public double? RerankScore { get; init; }
 }
 
 /// <summary>Coverage block reported with every recall response (§10).</summary>
@@ -132,6 +139,9 @@ public sealed record HistoryCoverage(
     /// <summary>Embedding progress of the served generation (dense coverage and lag), or null.</summary>
     public ChunkCounts? Chunks { get; init; }
 
+    /// <summary>Reranking of this response (<c>on (…)</c>, <c>skipped: …</c>, <c>failed: …</c>, <c>not used: …</c>), or null when reranking is off.</summary>
+    public string? Rerank { get; init; }
+
     /// <summary>True when "no hits" may be stated without qualification beyond the listed ranges.</summary>
     public bool IsComplete => this.BuildComplete && !this.Halted && !this.UnindexedTailTruncated && !this.PurgeFenced;
 }
@@ -174,6 +184,9 @@ public sealed record HistorySearchResult
 
     /// <summary>Best verified dense similarity (hybrid only): the abstention confidence used by the evaluation.</summary>
     public double? TopSimilarity { get; init; }
+
+    /// <summary>The rerank stage's outcome on this page (evaluation latency), or null when it was not called.</summary>
+    public RerankOutcome? RerankOutcome { get; init; }
 }
 
 /// <summary>A parsed <c>history_read</c> call in recall mode.</summary>
````

**Modify** `src/Cortex.Contained.Agent.Host/History/HistoryRecallService.Hybrid.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/History/HistoryRecallService.Hybrid.cs b/src/Cortex.Contained.Agent.Host/History/HistoryRecallService.Hybrid.cs
index 38cbf59..2dcc3c3 100644
--- a/src/Cortex.Contained.Agent.Host/History/HistoryRecallService.Hybrid.cs
+++ b/src/Cortex.Contained.Agent.Host/History/HistoryRecallService.Hybrid.cs
@@ -1,3 +1,5 @@
+using System.Globalization;
+using Cortex.Contained.Agent.Host.Reranking;
 using Cortex.Contained.Agent.Host.Storage;
 
 namespace Cortex.Contained.Agent.Host.History;
@@ -88,6 +90,7 @@ public sealed partial class HistoryRecallService
         HistoryCursorPosition position,
         IReadOnlyList<MessageRecord> tailRows,
         float[] queryVector,
+        RerankBudget? rerank,
         CancellationToken cancellationToken)
     {
         var settings = this.options.CurrentValue;
@@ -162,14 +165,24 @@ public sealed partial class HistoryRecallService
             ranked = Rank(includeEchoes: true); // Only echoes match: show them rather than nothing.
         }
 
+        // Reranking (§4.3) reorders the head of the fused list; every page recomputes the same order.
+        var order = ranked.Order;
+        var rerankScores = new Dictionary<long, double>();
+        RerankOutcome? rerankOutcome = null;
+        string? rerankNote = null;
+        if (rerank is not null)
+        {
+            (order, rerankOutcome, rerankNote, rerankScores) = await this.RerankAsync(request.Query, ranked.Order, Rows, rerank, position, cancellationToken).ConfigureAwait(false);
+        }
+
         var hits = new List<HistoryHit>();
         var takenTail = Math.Min(request.Limit, Math.Max(0, tailHits.Count - position.TailSkip));
         hits.AddRange(tailHits.Skip(position.TailSkip).Take(takenTail));
         var tailNext = position.TailSkip + takenTail;
         var offset = position.IndexOffset;
-        while (hits.Count < request.Limit && offset < ranked.Order.Count)
+        while (hits.Count < request.Limit && offset < order.Count)
         {
-            var segId = ranked.Order[offset++];
+            var segId = order[offset++];
             var segment = segments[segId];
             var rows = Rows(segId);
             var echoOnly = EchoOnly(rows);
@@ -190,22 +203,57 @@ public sealed partial class HistoryRecallService
                 MatchedBy = keyword.ContainsKey(segId) && dense.ContainsKey(segId) ? HistoryMatchKind.Both
                     : keyword.ContainsKey(segId) ? HistoryMatchKind.Keyword
                     : HistoryMatchKind.Semantic,
+                RerankScore = rerankScores.TryGetValue(segId, out var rerankScore) ? rerankScore : null,
             });
         }
 
         return new HistorySearchResult
         {
             Hits = hits,
-            HasMore = offset < ranked.Order.Count || tailNext < tailHits.Count,
+            HasMore = offset < order.Count || tailNext < tailHits.Count,
             Next = new HistoryCursorPosition(offset, tailNext, null, 0),
             Unavailable = keywordMissing + denseMissing,
             CollapsedDuplicates = ranked.Collapsed,
             TopSimilarity = bestChunk.Values.Where(h => dense.ContainsKey(h.SegId)).Select(h => (double?)h.Score).Max() ?? 0,
-            Coverage = Empty(null),
+            Coverage = Empty(null) with { Rerank = rerankNote },
             Binding = string.Empty,
+            RerankOutcome = rerankOutcome,
         };
     }
 
+    /// <summary>
+    /// Reorders the first <see cref="RerankBudget.TopN"/> fused segments by reranker relevance, using each
+    /// segment's shown rows (the verified matching lines) as raw text. Runs inside the read lease on the
+    /// verified snapshot, bounded by the stage's timeout, so a purge waits at most that long and deleted
+    /// text is never sent. A page that starts after the reranked head keeps the fused order without a call.
+    /// </summary>
+    private async Task<(List<long> Order, RerankOutcome? Outcome, string Note, Dictionary<long, double> Scores)> RerankAsync(
+        string query,
+        List<long> fused,
+        Func<long, IReadOnlyList<MessageRecord>> rows,
+        RerankBudget budget,
+        HistoryCursorPosition position,
+        CancellationToken cancellationToken)
+    {
+        var window = Math.Min(budget.TopN, fused.Count);
+        if (this.reranker is null)
+        {
+            return (fused, null, "skipped: no reranker is configured", []);
+        }
+
+        if (window > 0 && position.IndexOffset >= window)
+        {
+            return (fused, null, string.Create(CultureInfo.InvariantCulture, $"not used: this page starts after the reranked top {window}"), []);
+        }
+
+        var head = fused.Take(window).ToList();
+        var candidates = head.Select(segId => (IReadOnlyList<string>)rows(segId).Select(HistoryRowText.DisplayContent).ToList()).ToList();
+        var outcome = await this.reranker.RerankAsync(query, candidates, budget, cancellationToken).ConfigureAwait(false);
+        var order = outcome.Order.Select(i => head[i]).Concat(fused.Skip(window)).ToList();
+        var scores = outcome.Scores.ToDictionary(s => head[s.Key], s => s.Value);
+        return (order, outcome, outcome.Note, scores);
+    }
+
     /// <summary>
     /// Re-reads the members of each dense candidate on the caller's snapshot. A segment whose rows changed
     /// since projection (fingerprint mismatch) is dropped and queued for recomputation: its vectors may
````

**Modify** `src/Cortex.Contained.Agent.Host/History/HistoryRecallService.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/History/HistoryRecallService.cs b/src/Cortex.Contained.Agent.Host/History/HistoryRecallService.cs
index 20a0b2c..8111ead 100644
--- a/src/Cortex.Contained.Agent.Host/History/HistoryRecallService.cs
+++ b/src/Cortex.Contained.Agent.Host/History/HistoryRecallService.cs
@@ -1,3 +1,4 @@
+using Cortex.Contained.Agent.Host.Reranking;
 using Cortex.Contained.Agent.Host.Storage;
 using Microsoft.Extensions.Options;
 
@@ -21,6 +22,7 @@ public sealed partial class HistoryRecallService
     private readonly IOptionsMonitor<HistoryOptions> options;
     private readonly IHistoryEmbedder? embedder;
     private readonly TimeProvider time;
+    private readonly IRerankStage? reranker;
 
     public HistoryRecallService(
         IHistorySource source,
@@ -29,7 +31,8 @@ public sealed partial class HistoryRecallService
         HistoryStatus status,
         IOptionsMonitor<HistoryOptions> options,
         IHistoryEmbedder? embedder = null,
-        TimeProvider? time = null)
+        TimeProvider? time = null,
+        IRerankStage? reranker = null)
     {
         this.source = source;
         this.projector = projector;
@@ -38,6 +41,7 @@ public sealed partial class HistoryRecallService
         this.options = options;
         this.embedder = embedder;
         this.time = time ?? TimeProvider.System;
+        this.reranker = reranker;
     }
 
     /// <summary>Test seam: awaited after the read lease and snapshot are taken (simulates a slow recall).</summary>
@@ -79,7 +83,9 @@ public sealed partial class HistoryRecallService
         }
 
         var mode = queryVector is null ? HistorySearchMode.Keyword : HistorySearchMode.Hybrid;
-        var binding = HistoryCursorCodec.Binding(request with { Mode = mode }, state?.Generation);
+        var rerank = request.Order == HistoryOrder.Relevance ? request.Rerank.Resolve(this.reranker, RerankSurface.History) : null;
+        var rerankApplies = rerank is not null && mode == HistorySearchMode.Hybrid;
+        var binding = HistoryCursorCodec.Binding(request with { Mode = mode }, state?.Generation, rerankApplies ? rerank!.Label : null);
 
         var position = HistoryCursorPosition.Start;
         if (request.Cursor is { } cursor)
@@ -125,12 +131,12 @@ public sealed partial class HistoryRecallService
         var result = request.Order == HistoryOrder.Time
             ? await this.SearchTimeOrderAsync(snapshot, index, parsed, request, filter, position, tailRows, cancellationToken).ConfigureAwait(false)
             : mode == HistorySearchMode.Hybrid
-                ? await this.SearchHybridAsync(snapshot, index!, parsed, request, filter, position, tailRows, queryVector!, cancellationToken).ConfigureAwait(false)
+                ? await this.SearchHybridAsync(snapshot, index!, parsed, request, filter, position, tailRows, queryVector!, rerank, cancellationToken).ConfigureAwait(false)
                 : await this.SearchRelevanceAsync(snapshot, index, parsed, request, filter, position, tailRows, cancellationToken).ConfigureAwait(false);
 
         return result with
         {
-            Coverage = coverage,
+            Coverage = coverage with { Rerank = rerank is null ? null : rerankApplies ? result.Coverage.Rerank : "not used: mode=keyword (reranking follows hybrid fusion)" },
             Binding = binding,
             Mode = mode,
             DroppedTerms = parsed.DroppedTerms.Select(t => string.Join(' ', t.Tokens)).ToList(),
````

**Modify** `src/Cortex.Contained.Agent.Host/History/HistoryResultRenderer.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/History/HistoryResultRenderer.cs b/src/Cortex.Contained.Agent.Host/History/HistoryResultRenderer.cs
index 40d4dbf..fd6337c 100644
--- a/src/Cortex.Contained.Agent.Host/History/HistoryResultRenderer.cs
+++ b/src/Cortex.Contained.Agent.Host/History/HistoryResultRenderer.cs
@@ -230,6 +230,11 @@ public static class HistoryResultRenderer
             builder.Append('\n');
         }
 
+        if (coverage.Rerank is { } rerank)
+        {
+            builder.Append("rerank: ").Append(rerank).Append('\n');
+        }
+
         return builder.ToString();
     }
 
@@ -248,6 +253,10 @@ public static class HistoryResultRenderer
     private static string RelevanceHit(int number, HistoryHit hit, HistoryRenderOptions options, string boundary, bool hybrid)
     {
         var match = hybrid ? " | match " + hit.MatchedBy.ToString().ToLowerInvariant() : string.Empty;
+        if (hit.RerankScore is { } rerankScore)
+        {
+            match += string.Create(CultureInfo.InvariantCulture, $" | rerank {rerankScore:F2}");
+        }
         var from = TimeZoneInfo.ConvertTime(hit.From, options.TimeZone);
         var to = TimeZoneInfo.ConvertTime(hit.To, options.TimeZone);
         var builder = new StringBuilder();
````

**Create** `src/Cortex.Contained.Agent.Host/Reranking/RerankRequest.cs`:

````csharp
namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>
/// Per-call reranking choice of a search: the configured budget (production), off, or an explicit
/// budget (evaluation runs compare budgets on the same corpus).
/// </summary>
/// <param name="UseConfigured">Use <see cref="IRerankStage.BudgetFor"/>.</param>
/// <param name="Budget">The explicit budget when <paramref name="UseConfigured"/> is false; null = off.</param>
public sealed record RerankRequest(bool UseConfigured, RerankBudget? Budget)
{
    /// <summary>The configured budget (null when reranking is off for the surface).</summary>
    public static RerankRequest Configured { get; } = new(true, null);

    /// <summary>No reranking.</summary>
    public static RerankRequest Off { get; } = new(false, null);

    /// <summary>Rerank with <paramref name="budget"/> regardless of the configuration.</summary>
    public static RerankRequest With(RerankBudget budget) => new(false, budget ?? throw new ArgumentNullException(nameof(budget)));

    /// <summary>The effective budget for <paramref name="surface"/>.</summary>
    public RerankBudget? Resolve(IRerankStage? stage, RerankSurface surface) =>
        this.UseConfigured ? stage?.BudgetFor(surface) : this.Budget;
}
````

- [ ] **Step 4: Run the tests and confirm they pass**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.HistoryRerankTests"
```

Expected: PASS — `Passed: 6`.

- [ ] **Step 5: Regression**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.History"
```

Expected: PASS — `Passed: 349`: the increment 1–4 history tests are unchanged and still pass (reranking is off without a stage).

- [ ] **Step 6: Commit** (never stage `version.json`)

```powershell
git add src/Cortex.Contained.Agent.Host/History/HistoryCursorCodec.cs src/Cortex.Contained.Agent.Host/History/HistoryRecallModels.cs src/Cortex.Contained.Agent.Host/History/HistoryRecallService.Hybrid.cs src/Cortex.Contained.Agent.Host/History/HistoryRecallService.cs src/Cortex.Contained.Agent.Host/History/HistoryResultRenderer.cs src/Cortex.Contained.Agent.Host/Reranking/RerankRequest.cs tests/Cortex.Contained.Agent.Host.Tests/Reranking/HistoryRerankTests.cs
git commit -m "feat(history): rerank the fused head of hybrid history search with an honest coverage line" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 6: Rerank stage after hybrid fusion in `memory_search`

**Model:** standard

I5-7, I5-12. `HybridMemoryReranking` widens the request to `max(limit, TopN)`, reranks the head of a **hybrid** result by each memory's title and content lines and cuts to `limit`; other modes (including a fallback) are only cut and say `not used`. `MemorySearchTool` takes an optional `IRerankStage` and adds a `rerank:` footer line only when a budget applies, so the default dense output stays byte-identical (the increment 4 characterisation tests are the proof).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Reranking/HybridMemoryReranking.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Tools/BuiltIn/MemorySearchTool.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Reranking/MemoryRerankTests.cs`

- [ ] **Step 1: Write the failing test**

**Create** `tests/Cortex.Contained.Agent.Host.Tests/Reranking/MemoryRerankTests.cs`:

````csharp
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Cortex.Contained.Contracts.Hub;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary><c>memory_search</c> with the rerank stage after hybrid fusion (§4.3).</summary>
public sealed class MemoryRerankTests
{
    private static readonly ToolExecutionContext Context = new() { ConversationId = "conv", ChannelId = "ch" };
    private static readonly DateTimeOffset Created = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly IHybridMemorySearch search = Substitute.For<IHybridMemorySearch>();
    private readonly FakeReranker reranker = new();
    private readonly RerankerOptions rerankerOptions = new();
    private readonly RerankerAccess access = new();
    private readonly RerankStage stage;

    public MemoryRerankTests()
    {
        var monitor = Substitute.For<IOptionsMonitor<RerankerOptions>>();
        monitor.CurrentValue.Returns(this.rerankerOptions);
        this.access.Apply(new RerankerGrant { Enabled = true, ApiKey = "k" });
        this.rerankerOptions.Memory.TopN = 3;
        this.stage = new RerankStage(this.reranker, monitor, this.access, NullLogger<RerankStage>.Instance);
    }

    private static HybridSearchHit Hit(string id, string content, string? title = null) => new(
        new SearchResult { MemoryId = id, Title = title, Content = content, Tags = [], CreatedAt = Created, UpdatedAt = Created, Score = 0 },
        MemoryMatchKind.Keyword, 0.03, 1, null, null);

    private void Returns(MemorySearchMode mode, params HybridSearchHit[] hits) =>
        this.search.SearchAsync(Arg.Any<HybridSearchRequest>(), Arg.Any<CancellationToken>()).Returns(new HybridSearchResult(hits, mode, null, null));

    private Task<AgentToolResult> RunAsync(string json, IRerankStage? reranker)
    {
        var monitor = Substitute.For<IOptionsMonitor<MemoryMcpOptions>>();
        monitor.CurrentValue.Returns(new MemoryMcpOptions());
        return new MemorySearchTool(this.search, Substitute.For<IEmbeddingService>(), monitor, reranker).ExecuteAsync(json, Context, CancellationToken.None);
    }

    [Fact]
    public async Task Hybrid_RerankOn_AsksForTopNCandidates_ReordersAndCutsToLimit()
    {
        this.Returns(MemorySearchMode.Hybrid, Hit("m1", "coffee"), Hit("m2", "budget"), Hit("m3", "night train to TownB", "Trip"));

        var result = await this.RunAsync("""{"query":"night train","mode":"hybrid","limit":2}""", this.stage);

        await this.search.Received(1).SearchAsync(Arg.Is<HybridSearchRequest>(r => r.Limit == 3), Arg.Any<CancellationToken>());
        Assert.StartsWith("Found 2 matching memories", result.Content, StringComparison.Ordinal);
        Assert.True(result.Content.IndexOf("m3", StringComparison.Ordinal) < result.Content.IndexOf("m1", StringComparison.Ordinal));
        Assert.DoesNotContain("m2", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("rerank: on (top 3 reordered, ≤1200 chars of matching lines each)", result.Content, StringComparison.Ordinal);
        Assert.Equal(["Trip\nnight train to TownB", "coffee", "budget"], this.reranker.Calls[0].Documents.OrderByDescending(d => d.Length));
    }

    [Fact]
    public async Task DenseMode_NotReranked_OutputUnchanged()
    {
        this.Returns(MemorySearchMode.Dense, Hit("m1", "coffee"), Hit("m2", "budget"));

        var reranked = await this.RunAsync("""{"query":"night train","mode":"dense"}""", this.stage);
        var plain = await this.RunAsync("""{"query":"night train","mode":"dense"}""", null);

        Assert.Empty(this.reranker.Calls);
        Assert.Equal(plain.Content, reranked.Content);
    }

    [Fact]
    public async Task HybridFellBackToKeyword_CutToLimit_AndSaysRerankWasNotUsed()
    {
        this.Returns(MemorySearchMode.Keyword, Hit("m1", "coffee"), Hit("m2", "budget"), Hit("m3", "train"));

        var result = await this.RunAsync("""{"query":"train","mode":"hybrid","limit":1}""", this.stage);

        Assert.Empty(this.reranker.Calls);
        Assert.StartsWith("Found 1 matching memories", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("rerank: not used: mode=keyword (reranking follows hybrid fusion)", result.Content, StringComparison.Ordinal);
    }
}
````

- [ ] **Step 2: Run it and confirm it fails**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.MemoryRerankTests|FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Tools.MemorySearchToolTests"
```

Expected: FAIL — the build fails: `HybridMemoryReranking` and the `MemorySearchTool` reranker parameter do not exist yet.

- [ ] **Step 3: Implement**

**Create** `src/Cortex.Contained.Agent.Host/Reranking/HybridMemoryReranking.cs`:

````csharp
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>
/// The rerank stage for curated-memory search (§4.3): applied after hybrid fusion only, to the first
/// <see cref="RerankBudget.TopN"/> fused memories, each given as its title and content lines (raw text,
/// best-matching lines kept first within the budget). Callers ask the search for
/// <see cref="CandidateLimit"/> memories and get <c>limit</c> back. Shared by <c>memory_search</c> and
/// the memory evaluation.
/// </summary>
public static class HybridMemoryReranking
{
    /// <summary>How many memories to request so the reranker sees its whole budget.</summary>
    public static int CandidateLimit(int limit, RerankBudget? budget) => budget is null ? limit : Math.Max(limit, budget.TopN);

    /// <summary>The raw lines of one memory: its title, then its non-empty content lines.</summary>
    public static IReadOnlyList<string> Lines(SearchResult memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(memory.Title))
        {
            lines.Add(memory.Title);
        }

        lines.AddRange(memory.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return lines;
    }

    /// <summary>
    /// Reorders the head of a hybrid <paramref name="result"/> and cuts it to <paramref name="limit"/>.
    /// Results of other modes (including a fallback) are only cut, with a "not used" note.
    /// </summary>
    public static async Task<(HybridSearchResult Result, RerankOutcome? Outcome, string Note)> ApplyAsync(
        IRerankStage? stage, string query, HybridSearchResult result, int limit, RerankBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(budget);
        if (result.EffectiveMode != MemorySearchMode.Hybrid)
        {
            return (result with { Hits = result.Hits.Take(limit).ToList() }, null,
                $"not used: mode={result.EffectiveMode.ToString().ToLowerInvariant()} (reranking follows hybrid fusion)");
        }

        if (stage is null)
        {
            return (result with { Hits = result.Hits.Take(limit).ToList() }, null, "skipped: no reranker is configured");
        }

        var window = Math.Min(budget.TopN, result.Hits.Count);
        var candidates = result.Hits.Take(window).Select(h => Lines(h.Memory)).ToList();
        var outcome = await stage.RerankAsync(query, candidates, budget, cancellationToken).ConfigureAwait(false);
        var hits = outcome.Order.Select(i => result.Hits[i]).Concat(result.Hits.Skip(window)).Take(limit).ToList();
        return (result with { Hits = hits }, outcome, outcome.Note);
    }
}
````

**Modify** `src/Cortex.Contained.Agent.Host/Tools/BuiltIn/MemorySearchTool.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/Tools/BuiltIn/MemorySearchTool.cs b/src/Cortex.Contained.Agent.Host/Tools/BuiltIn/MemorySearchTool.cs
index 8606562..8988cf6 100644
--- a/src/Cortex.Contained.Agent.Host/Tools/BuiltIn/MemorySearchTool.cs
+++ b/src/Cortex.Contained.Agent.Host/Tools/BuiltIn/MemorySearchTool.cs
@@ -1,6 +1,7 @@
 using System.Globalization;
 using System.Text;
 using System.Text.Json;
+using Cortex.Contained.Agent.Host.Reranking;
 using MemoryMcp.Core.Configuration;
 using MemoryMcp.Core.Services;
 using MemoryMcp.Core.Storage;
@@ -19,12 +20,14 @@ internal sealed class MemorySearchTool : IAgentTool
     private readonly IHybridMemorySearch search;
     private readonly IEmbeddingService embeddingService;
     private readonly IOptionsMonitor<MemoryMcpOptions> options;
+    private readonly IRerankStage? reranker;
 
-    public MemorySearchTool(IHybridMemorySearch search, IEmbeddingService embeddingService, IOptionsMonitor<MemoryMcpOptions> options)
+    public MemorySearchTool(IHybridMemorySearch search, IEmbeddingService embeddingService, IOptionsMonitor<MemoryMcpOptions> options, IRerankStage? reranker = null)
     {
         this.search = search;
         this.embeddingService = embeddingService;
         this.options = options;
+        this.reranker = reranker;
     }
 
     public string Name => "memory_search";
@@ -124,18 +127,25 @@ internal sealed class MemorySearchTool : IAgentTool
                 }
             }
 
+            var rerank = mode == MemorySearchMode.Hybrid ? this.reranker?.BudgetFor(RerankSurface.Memory) : null;
             var result = await this.search.SearchAsync(
                 new HybridSearchRequest
                 {
                     Query = query,
                     Mode = mode,
-                    Limit = limit,
+                    Limit = HybridMemoryReranking.CandidateLimit(limit, rerank),
                     MinScore = minScore,
                     Filter = new MemorySearchFilter { Tags = tags },
                 },
                 cancellationToken).ConfigureAwait(false);
 
-            return AgentToolResult.Ok(Render(result, fallback ?? result.Fallback, settings.SearchMaxContentLength));
+            string? rerankNote = null;
+            if (rerank is not null)
+            {
+                (result, _, rerankNote) = await HybridMemoryReranking.ApplyAsync(this.reranker, query, result, limit, rerank, cancellationToken).ConfigureAwait(false);
+            }
+
+            return AgentToolResult.Ok(Render(result, fallback ?? result.Fallback, settings.SearchMaxContentLength, rerankNote));
         }
         catch (InvalidOperationException ex) when (ex.InnerException is HttpRequestException)
         {
@@ -173,7 +183,7 @@ internal sealed class MemorySearchTool : IAgentTool
         }
     }
 
-    private static string Footer(MemorySearchMode mode, string? fallback)
+    private static string Footer(MemorySearchMode mode, string? fallback, string? rerankNote)
     {
         var label = mode switch
         {
@@ -181,7 +191,8 @@ internal sealed class MemorySearchTool : IAgentTool
             MemorySearchMode.Keyword => "keyword",
             _ => "semantic",
         };
-        return fallback is null ? $"mode: {label}" : $"mode: {label}; fallback: {fallback}";
+        var footer = fallback is null ? $"mode: {label}" : $"mode: {label}; fallback: {fallback}";
+        return rerankNote is null ? footer : footer + "\nrerank: " + rerankNote;
     }
 
     private static string MatchLabel(MemoryMatchKind match) => match switch
@@ -191,14 +202,14 @@ internal sealed class MemorySearchTool : IAgentTool
         _ => "semantic",
     };
 
-    private static string Render(HybridSearchResult result, string? fallback, int maxContentLength)
+    private static string Render(HybridSearchResult result, string? fallback, int maxContentLength, string? rerankNote)
     {
         // A plain semantic search renders exactly as memory_search always did.
-        var annotated = result.EffectiveMode != MemorySearchMode.Dense || fallback is not null;
+        var annotated = result.EffectiveMode != MemorySearchMode.Dense || fallback is not null || rerankNote is not null;
         if (result.Hits.Count == 0)
         {
             return annotated
-                ? "No matching memories found.\n" + Footer(result.EffectiveMode, fallback)
+                ? "No matching memories found.\n" + Footer(result.EffectiveMode, fallback, rerankNote)
                 : "No matching memories found.";
         }
 
@@ -244,7 +255,7 @@ internal sealed class MemorySearchTool : IAgentTool
 
         if (annotated)
         {
-            sb.AppendLine(Footer(result.EffectiveMode, fallback));
+            sb.AppendLine(Footer(result.EffectiveMode, fallback, rerankNote));
         }
 
         return sb.ToString().TrimEnd();
````

- [ ] **Step 4: Run the tests and confirm they pass**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.MemoryRerankTests|FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Tools.MemorySearchToolTests"
```

Expected: PASS — `Passed: 14`.

- [ ] **Step 5: Commit** (never stage `version.json`)

```powershell
git add src/Cortex.Contained.Agent.Host/Reranking/HybridMemoryReranking.cs src/Cortex.Contained.Agent.Host/Tools/BuiltIn/MemorySearchTool.cs tests/Cortex.Contained.Agent.Host.Tests/Reranking/MemoryRerankTests.cs
git commit -m "feat(memory): rerank the fused head of hybrid memory search with an honest footer line" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 7: Agent wiring: hub grant, DI, validation and `/health`

**Model:** standard

I5-5, I5-6, I5-11. `ISystemHub.ProvideRerankerGrant` and `AgentHub.ProvideRerankerGrant` (applies the grant to `RerankerAccess`, logs only whether a key arrived); `Program.cs` binds `Reranker`, registers `RerankerAccess`, the `reranker` HttpClient **without request logging** (`RemoveAllLoggers`), `IReranker`, `RerankStage`/`IRerankStage` (picked up by `HistoryRecallService`'s optional parameter), passes the stage to `MemorySearchTool`, validates `RerankerOptions` at startup and adds `/health → subsystems.reranker` (never affects `healthy`). Wiring only: covered by the build and the Task 1–6 tests.

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Program.cs`
- Modify: `src/Cortex.Contained.Contracts/Hub/ISystemHub.cs`

- [ ] **Step 1: Implement**

**Modify** `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs b/src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs
index f141853..b057790 100644
--- a/src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs
+++ b/src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs
@@ -44,6 +44,7 @@ public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
     private readonly Cortex.Contained.Agent.Host.Events.KnownSecrets? knownSecrets;
     private readonly Cortex.Contained.Agent.Host.Events.EventBackfillService? eventBackfill;
     private readonly Storage.StorageKeyRing? storageKeys;
+    private readonly Cortex.Contained.Agent.Host.Reranking.RerankerAccess? rerankerAccess;
     private readonly Cortex.Contained.Agent.Host.Mcp.McpToolStore mcpToolStore;
     private readonly IHttpClientFactory httpClientFactory;
     private readonly AgentMetrics metrics;
@@ -79,7 +80,8 @@ public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
         Cortex.Contained.Agent.Host.SpeakerId.EnrollmentOrchestrator? enrollmentOrchestrator = null,
         Cortex.Contained.Agent.Host.Events.KnownSecrets? knownSecrets = null,
         Cortex.Contained.Agent.Host.Events.EventBackfillService? eventBackfill = null,
-        Storage.StorageKeyRing? storageKeys = null)
+        Storage.StorageKeyRing? storageKeys = null,
+        Cortex.Contained.Agent.Host.Reranking.RerankerAccess? rerankerAccess = null)
     {
         this.runtime = runtime;
         this.llmClient = llmClient;
@@ -103,6 +105,7 @@ public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
         this.knownSecrets = knownSecrets;
         this.eventBackfill = eventBackfill;
         this.storageKeys = storageKeys;
+        this.rerankerAccess = rerankerAccess;
         this.mcpToolStore = mcpToolStore;
         this.httpClientFactory = httpClientFactory;
         this.metrics = metrics;
@@ -213,6 +216,16 @@ public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
         return Task.FromResult(outcome != Storage.StorageKeyAcceptance.Rejected);
     }
 
+    /// <inheritdoc />
+    public Task ProvideRerankerGrant(RerankerGrant grant)
+    {
+        ArgumentNullException.ThrowIfNull(grant);
+        this.rerankerAccess?.Apply(grant);
+        var hasKey = !string.IsNullOrWhiteSpace(grant.ApiKey);
+        this.LogRerankerGrantReceived(grant.Enabled, hasKey);
+        return Task.CompletedTask;
+    }
+
     /// <inheritdoc />
     public Task ProvideCredentials(LlmCredentials credentials)
     {
@@ -1052,6 +1065,9 @@ public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
     [LoggerMessage(Level = LogLevel.Information, Message = "Storage key received: {Outcome}")]
     private partial void LogStorageKeyReceived(Storage.StorageKeyAcceptance outcome);
 
+    [LoggerMessage(Level = LogLevel.Information, Message = "Reranker grant received: enabled={Enabled}, key={HasKey}")]
+    private partial void LogRerankerGrantReceived(bool enabled, bool hasKey);
+
     [LoggerMessage(Level = LogLevel.Information, Message = "Seeding history for channel {ChannelId}: {MessageCount} messages")]
     private partial void LogSeedHistory(string channelId, int messageCount);
 
````

**Modify** `src/Cortex.Contained.Agent.Host/Program.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/Program.cs b/src/Cortex.Contained.Agent.Host/Program.cs
index 9eb0f7e..bee7460 100644
--- a/src/Cortex.Contained.Agent.Host/Program.cs
+++ b/src/Cortex.Contained.Agent.Host/Program.cs
@@ -172,6 +172,21 @@ builder.Services.AddHostedService<HistoryProjectorService>();
 builder.Services.AddHostedService<HistoryMaintenanceService>();
 builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Tools.IConversationToolGate, HistoryToolsGate>();
 
+// --- Reranker (increment 5): cortex-reranker sidecar after hybrid fusion; off until the Bridge grants it ---
+builder.Services.Configure<Cortex.Contained.Agent.Host.Reranking.RerankerOptions>(
+    builder.Configuration.GetSection(Cortex.Contained.Agent.Host.Reranking.RerankerOptions.SectionName));
+builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Reranking.RerankerAccess>();
+// Request logging off: the handler pipeline logs nothing about rerank calls (they carry history text).
+builder.Services.AddHttpClient(Cortex.Contained.Agent.Host.Reranking.LlamaServerReranker.HttpClientName).RemoveAllLoggers();
+builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Reranking.IReranker>(sp =>
+    new Cortex.Contained.Agent.Host.Reranking.LlamaServerReranker(
+        sp.GetRequiredService<IHttpClientFactory>(),
+        sp.GetRequiredService<IOptionsMonitor<Cortex.Contained.Agent.Host.Reranking.RerankerOptions>>(),
+        sp.GetRequiredService<Cortex.Contained.Agent.Host.Reranking.RerankerAccess>()));
+builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Reranking.RerankStage>();
+builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Reranking.IRerankStage>(
+    sp => sp.GetRequiredService<Cortex.Contained.Agent.Host.Reranking.RerankStage>());
+
 // --- Event log and provenance (increment 2): events in messages.db, tool payload policy ---
 builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Events.KnownSecrets>();
 builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Storage.StorageKeyRing>();
@@ -542,7 +557,8 @@ builder.Services.AddSingleton<IAgentTool>(sp =>
     new MemorySearchTool(
         sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.QuarantineAwareHybridSearch>(),
         sp.GetRequiredService<IEmbeddingService>(),
-        sp.GetRequiredService<IOptionsMonitor<MemoryMcp.Core.Configuration.MemoryMcpOptions>>()));
+        sp.GetRequiredService<IOptionsMonitor<MemoryMcp.Core.Configuration.MemoryMcpOptions>>(),
+        sp.GetRequiredService<Cortex.Contained.Agent.Host.Reranking.IRerankStage>()));
 
 // --- Memory Management (wraps IMemoryService + raw SQLite listing for web UI) ---
 builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Memory.MemoryManagementService>();
@@ -887,6 +903,11 @@ if (app.Services.GetRequiredService<IOptions<MemoryMcpOptions>>().Value.Encrypti
         {
             throw new InvalidOperationException("Invalid memory search configuration: " + string.Join(" ", memorySearchErrors));
         }
+
+if (app.Services.GetRequiredService<IOptions<Cortex.Contained.Agent.Host.Reranking.RerankerOptions>>().Value.Validate() is { Count: > 0 } rerankerErrors)
+{
+    throw new InvalidOperationException("Invalid reranker configuration: " + string.Join(" ", rerankerErrors));
+}
 // Queued/recovered subagent tasks are now dispatched by SubagentExecutionCoordinator (a hosted
 // service) once Bridge + credentials + MCP-catalog readiness are all signaled — no startup call here.
 
@@ -943,12 +964,13 @@ _ = Task.Run(async () =>
 });
 
 // --- Health endpoint (unauthenticated) ---
-app.MapGet("/health", async (ToolRegistry toolRegistry, AgentSessionStore sessionStore, HistoryHealthReporter historyHealth, CancellationToken cancellationToken) =>
+app.MapGet("/health", async (ToolRegistry toolRegistry, AgentSessionStore sessionStore, HistoryHealthReporter historyHealth, Cortex.Contained.Agent.Host.Reranking.RerankStage rerankStage, CancellationToken cancellationToken) =>
 {
     var toolCount = toolRegistry.Count;
     var sessionCount = sessionStore.GetAll().Count;
     var healthy = toolCount > 0;
     var history = await historyHealth.SnapshotAsync(cancellationToken).ConfigureAwait(false);
+    var reranker = rerankStage.Snapshot();
 
     return Results.Ok(new
     {
@@ -978,6 +1000,18 @@ app.MapGet("/health", async (ToolRegistry toolRegistry, AgentSessionStore sessio
                 history.MaintenancePendingTasks,
                 dense = history.Dense,
             },
+            // Optional subsystem: a degraded reranker never makes the agent unhealthy (searches keep the fused order).
+            reranker = new
+            {
+                status = !reranker.Enabled ? "off" : reranker.State == "ok" ? "ok" : "degraded",
+                reranker.Enabled,
+                reranker.State,
+                reranker.ConsecutiveFailures,
+                reranker.CircuitOpenUntil,
+                reranker.LastError,
+                reranker.LastLatencyMs,
+                reranker.LastCallAt,
+            },
         },
     });
 });
````

**Modify** `src/Cortex.Contained.Contracts/Hub/ISystemHub.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Contracts/Hub/ISystemHub.cs b/src/Cortex.Contained.Contracts/Hub/ISystemHub.cs
index cb469ff..ab1dabf 100644
--- a/src/Cortex.Contained.Contracts/Hub/ISystemHub.cs
+++ b/src/Cortex.Contained.Contracts/Hub/ISystemHub.cs
@@ -27,6 +27,12 @@ public interface ISystemHub
     /// </summary>
     Task<bool> ProvideStorageKey(StorageKeyGrant grant);
 
+    /// <summary>
+    /// Push the reranker switch and the <c>cortex-reranker</c> API key (increment 5). Called on every
+    /// connect after the storage key. Held in memory only; until it arrives reranking is off.
+    /// </summary>
+    Task ProvideRerankerGrant(RerankerGrant grant);
+
     /// <summary>
     /// Tell the agent which channels are currently active on the Bridge.
     /// Tools use this list to validate channel targets and to inform the LLM
````

- [ ] **Step 2: Build and run the reranking tests**

```powershell
dotnet build cortex-contained.sln -p:NuGetAudit=false
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking"
```

Expected: `Build succeeded.` with `0 Warning(s)` (CA1873 rejects computing the log argument inline — it is a local first); reranking tests PASS — `Passed: 33`.

- [ ] **Step 3: Commit** (never stage `version.json`)

```powershell
git add src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs src/Cortex.Contained.Agent.Host/Program.cs src/Cortex.Contained.Contracts/Hub/ISystemHub.cs
git commit -m "feat(reranker): wire the rerank stage, the Bridge grant hub call and the /health block" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 8: `cortex-reranker` sidecar image and compose service

**Model:** standard

I5-1–I5-4. `docker/reranker`: the Dockerfile pins `llama.cpp` `server-cuda-b12602` and `server-b12602` by digest (`VARIANT=cuda` default, `cpu` optional) and bakes Qwen3-Reranker-0.6B Q8_0 with `ADD --checksum=sha256:…` (bge-reranker-v2-m3 documented as the fallback); the entrypoint refuses an empty `LLAMA_API_KEY`, then runs `--reranking --no-webui --no-slots --log-verbosity 2`. Compose service `reranker` (profile `reranker`): internal network, **no ports**, `pull_policy: never`, read-only root, GPU reservation, key from `${CORTEX_RERANKER_API_KEY:-}`; the agent gets `Reranker__Endpoint`. The static test pins all of this (CI needs no Docker); building and running the image are optional manual steps.

**Files:**
- Modify: `docker-compose.yml`
- Create: `docker/reranker/Dockerfile`
- Create: `docker/reranker/entrypoint.sh`
- Create: `docker/reranker/version.json`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankerSidecarExposureTests.cs`

- [ ] **Step 1: Write the failing test**

**Create** `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankerSidecarExposureTests.cs`:

````csharp
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary>
/// Reranker exposure (§4.3): the sidecar that receives history and memory text publishes no port, needs
/// an API key, logs no requests, is never pulled from a registry, and its server and model are pinned.
/// </summary>
public sealed partial class RerankerSidecarExposureTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "cortex-contained.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private static YamlMappingNode Service(string name)
    {
        var stream = new YamlStream();
        using (var reader = new StreamReader(Path.Combine(Root, "docker-compose.yml")))
        {
            stream.Load(reader);
        }

        var services = (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode).Children[new YamlScalarNode("services")];
        return (YamlMappingNode)services.Children[new YamlScalarNode(name)];
    }

    private static string Scalar(YamlMappingNode node, string key) => ((YamlScalarNode)node.Children[new YamlScalarNode(key)]).Value!;

    private static List<string> Sequence(YamlMappingNode node, string key) =>
        ((YamlSequenceNode)node.Children[new YamlScalarNode(key)]).Children.Select(n => ((YamlScalarNode)n).Value!).ToList();

    [Fact]
    public void Reranker_PublishesNoPort_TakesTheKeyFromTheBridge_NeverPulls()
    {
        var reranker = Service("reranker");

        Assert.False(reranker.Children.ContainsKey(new YamlScalarNode("ports")));
        Assert.Equal(["LLAMA_API_KEY=${CORTEX_RERANKER_API_KEY:-}"], Sequence(reranker, "environment"));
        Assert.Equal(["reranker"], Sequence(reranker, "profiles"));
        Assert.Equal(["cortex-internal"], Sequence(reranker, "networks"));
        Assert.Equal("never", Scalar(reranker, "pull_policy"));
        Assert.Equal("cortex-reranker", Scalar(reranker, "container_name"));
    }

    [Fact]
    public void Agent_UsesTheInComposeReranker()
    {
        Assert.Contains("Reranker__Endpoint=http://reranker:8080", Sequence(Service("cortex-agent"), "environment"));
    }

    [Fact]
    public void Image_PinsEveryServerByDigest_AndTheModelBySha256()
    {
        var dockerfile = File.ReadAllText(Path.Combine(Root, "docker", "reranker", "Dockerfile"));

        var bases = dockerfile.Split('\n').Where(l => l.StartsWith("FROM ghcr.io/", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, bases.Count);
        Assert.All(bases, line => Assert.Matches(PinnedImage(), line));
        Assert.Matches(PinnedModel(), dockerfile);
    }

    [Fact]
    public void Entrypoint_RefusesAnEmptyKey_AndServesRerankingOnlyWithoutRequestLogs()
    {
        var entrypoint = File.ReadAllText(Path.Combine(Root, "docker", "reranker", "entrypoint.sh"));

        Assert.Contains("if [ -z \"${LLAMA_API_KEY:-}\" ]", entrypoint, StringComparison.Ordinal);
        Assert.Contains("exit 64", entrypoint, StringComparison.Ordinal);
        foreach (var flag in new[] { "--reranking", "--no-webui", "--no-slots", "--log-verbosity 2" })
        {
            Assert.Contains(flag, entrypoint, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("--metrics", entrypoint, StringComparison.Ordinal);
        Assert.DoesNotContain("--verbose", entrypoint, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^FROM ghcr\.io/ggml-org/llama\.cpp:server(-cuda)?-b\d+@sha256:[0-9a-f]{64} AS server-(cuda|cpu)$")]
    private static partial Regex PinnedImage();

    [GeneratedRegex(@"ADD --chmod=0555 --checksum=sha256:[0-9a-f]{64} \\\n\s+https://huggingface\.co/[^\s]+/resolve/[0-9a-f]{40}/[^\s]+\.gguf")]
    private static partial Regex PinnedModel();
}
````

- [ ] **Step 2: Run it and confirm it fails**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.RerankerSidecarExposureTests|FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.History.ComposeExposureTests"
```

Expected: FAIL — `Service("reranker")` throws (no such compose service) and the Dockerfile/entrypoint files are missing — 4 failures; the 2 `ComposeExposureTests` pass.

- [ ] **Step 3: Implement**

**Modify** `docker-compose.yml` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/docker-compose.yml b/docker-compose.yml
index 6397489..fa4e895 100644
--- a/docker-compose.yml
+++ b/docker-compose.yml
@@ -13,6 +13,9 @@ services:
       # NOTE: No API keys here. LLM calls are proxied through the Bridge.
       # Memory service: embeddings (Ollama) endpoint inside the Docker network
       - MemoryMcp__Ollama__Endpoint=http://embeddings:11434
+      # Reranker sidecar inside the Docker network (used only when the Bridge enables it; the
+      # Bridge pushes the switch and the API key over the hub, never through this file).
+      - Reranker__Endpoint=http://reranker:8080
       # Speaker-ID remote sidecar
       - SpeakerId__Backend=Remote
       - SpeakerId__RemoteEndpoint=http://voice-id:5200
@@ -142,6 +145,47 @@ services:
     # embedder. For a host-side debug session add a compose override that publishes
     # 127.0.0.1:11434, and remove it again afterwards.
 
+  # Cross-encoder reranker for hybrid history and memory search (episodic history,
+  # increment 5). Built locally from docker/reranker by scripts/Build-RerankerImage.ps1
+  # (llama.cpp server pinned by digest + Qwen3-Reranker-0.6B GGUF pinned by sha256, baked
+  # in; Self-Update records and verifies the image id). The Bridge starts it only when
+  # Reranker:Enabled is true, passing the API key it keeps in DPAPI storage through the
+  # process environment; the entrypoint refuses to start without a key.
+  reranker:
+    image: cortex-reranker:latest
+    build:
+      context: ./docker/reranker
+    # Local-only image: never pull a same-named image from a registry.
+    pull_policy: never
+    container_name: cortex-reranker
+    profiles: [reranker]
+    environment:
+      - LLAMA_API_KEY=${CORTEX_RERANKER_API_KEY:-}
+    networks:
+      - cortex-internal
+    restart: unless-stopped
+    read_only: true
+    tmpfs:
+      - /tmp
+    deploy:
+      resources:
+        limits:
+          memory: 2g
+        # GPU: the Gate R latency budget is out of reach on CPU (docs/reranker.md). On a host
+        # without an NVIDIA GPU, build -Variant cpu and drop this block in a compose override.
+        reservations:
+          devices:
+            - driver: nvidia
+              count: all
+              capabilities: [gpu]
+    logging:
+      driver: json-file
+      options:
+        max-size: "10m"
+        max-file: "3"
+    # No published port and no request logging (--log-verbosity 2): history and memory text
+    # reach it only from cortex-internal and never land in its logs.
+
 networks:
   cortex-internal:
     driver: bridge
````

**Create** `docker/reranker/Dockerfile`:

````dockerfile
# syntax=docker/dockerfile:1.7
#
# cortex-reranker: the episodic-history reranker sidecar (increment 5).
#
# Stock llama.cpp `llama-server` with a cross-encoder reranker model baked in, so the
# container needs no network at runtime and no model volume. Everything is pinned:
#   - the server image by digest (multi-arch index of tag server[-cuda]-b12602),
#   - the GGUF by sha256 (BuildKit verifies the download before it enters the image).
# Bumping a pin is a reviewed change; bump version.json with it.
#
# VARIANT=cuda (default) runs the model on the GPU and falls back to the CPU when none is
# visible; VARIANT=cpu is the small CPU-only image. Measured on CPU (20 cores): ~365 prompt
# tokens/s, far too slow for the Gate R p95 at the default budget — see docs/reranker.md.
#
# Fallback model (multilingual, bidirectional encoder): replace the ADD below with
#   https://huggingface.co/gpustack/bge-reranker-v2-m3-GGUF/resolve/3093af03b1a635e67b084b1d8c03c5f5e020fd05/bge-reranker-v2-m3-Q8_0.gguf
#   sha256 a43c7c9b11a4c1517e5bf95151960e1621d1b72f7a493364b01e386cf1aaa1d3
# and set RERANKER_MODEL_ALIAS=bge-reranker-v2-m3 (and Reranker:Model in the agent).
ARG VARIANT=cuda

FROM ghcr.io/ggml-org/llama.cpp:server-cuda-b12602@sha256:26f89ebd3bf17a5ecf6caee62bdabfa55abea3058150879f6f07b1fc786baa2d AS server-cuda
FROM ghcr.io/ggml-org/llama.cpp:server-b12602@sha256:ae28f5c2046de51dfd00c9116a36fe44b5e8703ef730d0aea69082781eecb1f7 AS server-cpu

FROM server-${VARIANT}

# 0555, not 0444: ADD applies the mode to the /models directory it creates, which needs +x to be entered.
ADD --chmod=0555 --checksum=sha256:22c9979ce4fbcdc5acdc310c6641c32797eff1aa980b8f7a2db8a8ea23429a48 \
    https://huggingface.co/ggml-org/Qwen3-Reranker-0.6B-Q8_0-GGUF/resolve/a02f48bb4f057028298c21fa033da2b30d7742d5/qwen3-reranker-0.6b-q8_0.gguf \
    /models/reranker.gguf

COPY --chmod=0555 entrypoint.sh /usr/local/bin/cortex-reranker

LABEL org.opencontainers.image.title="cortex-reranker" \
      org.opencontainers.image.description="llama.cpp reranking server with a baked Qwen3-Reranker-0.6B (Q8_0)"

ENV RERANKER_MODEL_ALIAS=qwen3-reranker-0.6b

# Unprivileged: the model is world-readable and the server writes nothing.
USER 65532:65532

HEALTHCHECK --interval=10s --timeout=3s --start-period=60s --retries=3 \
    CMD ["curl", "-fsS", "http://127.0.0.1:8080/health"]

ENTRYPOINT ["/usr/local/bin/cortex-reranker"]````

**Create** `docker/reranker/entrypoint.sh`:

````sh
#!/bin/sh
# cortex-reranker entrypoint: refuses to serve without authentication, then runs llama-server
# with the reranking endpoint only (no web UI, no slot monitoring) and without request logging.
set -eu

if [ -z "${LLAMA_API_KEY:-}" ]; then
    echo "cortex-reranker: LLAMA_API_KEY is empty; refusing to start without authentication (the Bridge passes the key)." >&2
    exit 64
fi

# The base image sets these; the explicit flags below are the single source of truth.
unset LLAMA_ARG_HOST LLAMA_ARG_PORT

# --log-verbosity 2 keeps warnings and errors only: request lines (info) and request bodies (debug)
# are never logged. /health stays public (llama-server exempts it from the API key).
exec /app/llama-server \
    --model /models/reranker.gguf \
    --alias "${RERANKER_MODEL_ALIAS}" \
    --reranking \
    --host 0.0.0.0 \
    --port 8080 \
    --ctx-size 8192 \
    --batch-size 2048 \
    --ubatch-size 2048 \
    --no-webui \
    --no-slots \
    --log-verbosity 2 \
    "$@"````

**Create** `docker/reranker/version.json`:

````json
{
  "major": 0,
  "minor": 1,
  "patch": 0
}
````

- [ ] **Step 4: Run the tests and confirm they pass**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.RerankerSidecarExposureTests|FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.History.ComposeExposureTests"
```

Expected: PASS — `Passed: 6`.

- [ ] **Step 5 (optional, manual — needs Docker and ~4 GB): build and probe the image**

```powershell
cd docker/reranker
docker run --rm (docker build -q .)   # no key: prints 'refusing to start without authentication', exit 64
docker build -t cortex-reranker:try .                       # VARIANT=cuda; add --build-arg VARIANT=cpu for CPU
docker run -d --name rr-try --gpus all -e LLAMA_API_KEY=k -p 127.0.0.1:18089:8080 cortex-reranker:try
Invoke-RestMethod http://127.0.0.1:18089/health             # {"status":"ok"} after ~1-2 s
$body = @{ model='qwen3-reranker-0.6b'; query='night train'; documents=@('coffee budget','the night train leaves at 22:10') } | ConvertTo-Json
Invoke-RestMethod -Method Post http://127.0.0.1:18089/v1/rerank -ContentType application/json -Body $body   # 401
Invoke-RestMethod -Method Post http://127.0.0.1:18089/v1/rerank -ContentType application/json -Body $body -Headers @{Authorization='Bearer k'}
docker logs rr-try    # warnings only, no request lines or bodies
docker rm -f rr-try; docker image rm cortex-reranker:try
```

Expected (verified while writing this plan; the port is published only for this probe): exit 64 without a key; healthy in ~1.6 s; 401 without the key; with it `results` puts index 1 first (≈0.99); `/slots` answers 501; logs show only `unauthorized: Invalid API Key` warnings. On the compose network (`$env:CORTEX_RERANKER_API_KEY='k'; docker compose --profile reranker up -d reranker`) `docker port cortex-reranker` is empty, a second `up` with the same key is a no-op, and a new key or a retagged `cortex-reranker:latest` recreates the container.

- [ ] **Step 6: Commit** (never stage `version.json`)

```powershell
git add docker-compose.yml docker/reranker/Dockerfile docker/reranker/entrypoint.sh docker/reranker/version.json tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankerSidecarExposureTests.cs
git commit -m "feat(reranker): cortex-reranker sidecar image (pinned server and model) and unpublished compose service" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 9: Bridge: DPAPI key, sidecar lifecycle and grant push

**Model:** standard

I5-5, I5-6, I5-13 (upgrade path). `BridgeConfig.Reranker.Enabled` (default false); `SecretManager.GetOrCreateRerankerKey` (64 hex, DPAPI, regenerated if undecryptable); `IRerankerComposeRunner` on `DockerComposeCommandRunner` — `up -d reranker` with the key **only in the child process environment**; `RerankerSidecarLifecycle.ReconcileAsync` always runs `up` when enabled (compose recreates the container after a key or image change, a no-op otherwise) and stops it when disabled; `CredentialsPusher` pushes `RerankerGrant` to every tenant after the storage key (`IRerankerGrantPushTarget` seam); `HubClient.ProvideRerankerGrantAsync`; startup reconcile in `Program.cs`.

**Files:**
- Modify: `src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs`
- Modify: `src/Cortex.Contained.Bridge/Hub/HubClient.cs`
- Modify: `src/Cortex.Contained.Bridge/Program.cs`
- Modify: `src/Cortex.Contained.Bridge/Security/SecretManager.cs`
- Modify: `src/Cortex.Contained.Bridge/Speech/DockerComposeCommandRunner.cs`
- Create: `src/Cortex.Contained.Bridge/Speech/IRerankerComposeRunner.cs`
- Create: `src/Cortex.Contained.Bridge/Speech/RerankerSidecarLifecycle.cs`
- Modify: `src/Cortex.Contained.Contracts/Config/BridgeConfig.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/Speech/RerankerSidecarLifecycleTests.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/Hosting/CredentialsPusherRerankerGrantTests.cs`

- [ ] **Step 1: Write the failing test**

**Create** `tests/Cortex.Contained.Bridge.Tests/Speech/RerankerSidecarLifecycleTests.cs`:

````csharp
using Cortex.Contained.Bridge.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class RerankerSidecarLifecycleTests
{
    private readonly IRerankerComposeRunner runner = Substitute.For<IRerankerComposeRunner>();

    private RerankerSidecarLifecycle Sut(Func<string>? key = null) =>
        new(this.runner, key ?? (() => "k1"), NullLogger<RerankerSidecarLifecycle>.Instance);

    [Fact]
    public async Task Enabled_AlwaysComposesUpWithTheKey_EvenWhenRunning()
    {
        this.runner.IsRerankerRunningAsync(Arg.Any<CancellationToken>()).Returns(true);

        await this.Sut().ReconcileAsync(enabled: true, CancellationToken.None);

        await this.runner.Received(1).StartRerankerAsync("k1", Arg.Any<CancellationToken>());
        await this.runner.DidNotReceive().StopRerankerAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_Running_Stops()
    {
        this.runner.IsRerankerRunningAsync(Arg.Any<CancellationToken>()).Returns(true);

        await this.Sut().ReconcileAsync(enabled: false, CancellationToken.None);

        await this.runner.Received(1).StopRerankerAsync(Arg.Any<CancellationToken>());
        await this.runner.DidNotReceive().StartRerankerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_NotRunning_NoOp()
    {
        await this.Sut().ReconcileAsync(enabled: false, CancellationToken.None);

        await this.runner.DidNotReceive().StopRerankerAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KeyUnavailable_DoesNotPropagate_AndDoesNotStart()
    {
        await this.Sut(() => throw new InvalidOperationException("dpapi")).ReconcileAsync(enabled: true, CancellationToken.None);

        await this.runner.DidNotReceive().StartRerankerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
````

**Create** `tests/Cortex.Contained.Bridge.Tests/Hosting/CredentialsPusherRerankerGrantTests.cs`:

````csharp
using Cortex.Contained.Bridge.Hosting;
using Cortex.Contained.Bridge.RemoteServices;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Hosting;

/// <summary>The reranker switch and sidecar key (DPAPI) pushed to every tenant's agent.</summary>
public sealed class CredentialsPusherRerankerGrantTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"cortex-test-{Guid.NewGuid():N}");
    private readonly SecretManager secretManager;

    public CredentialsPusherRerankerGrantTests()
    {
        Directory.CreateDirectory(this.tempDir);
        this.secretManager = new SecretManager(new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* cleanup */ }
    }

    private CredentialsPusher Pusher(bool enabled) => new(
        tenantRouter: null!,
        config: new BridgeConfig { Reranker = new RerankerSettingsConfig { Enabled = enabled } },
        httpClientFactory: null!,
        secretManager: this.secretManager,
        modelCatalog: null!,
        resolver: new RemoteServiceResolver(),
        tokenRefreshService: null!,
        logger: NullLogger<CredentialsPusher>.Instance);

    [Fact]
    public void GetOrCreateRerankerKey_IsStable256BitHex_AndPersisted()
    {
        var key = this.secretManager.GetOrCreateRerankerKey();
        var again = new SecretManager(new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.tempDir).GetOrCreateRerankerKey();

        Assert.Matches("^[0-9a-f]{64}$", key);
        Assert.Equal(key, again);
    }

    [Fact]
    public void BuildRerankerGrant_Disabled_CarriesNoKey()
    {
        var grant = this.Pusher(enabled: false).BuildRerankerGrant();

        Assert.False(grant.Enabled);
        Assert.Null(grant.ApiKey);
    }

    [Fact]
    public async Task PushRerankerGrantToTargets_EnabledGrantReachesEveryTenant_FailuresIsolated()
    {
        var pusher = this.Pusher(enabled: true);
        var grant = pusher.BuildRerankerGrant();
        var ok = new FakeTarget("default");
        var failing = new FakeTarget("tenant-a") { Throw = true };
        var ok2 = new FakeTarget("tenant-b");

        await pusher.PushRerankerGrantToTargetsAsync(grant, [ok, failing, ok2], CancellationToken.None);

        Assert.Equal(this.secretManager.GetOrCreateRerankerKey(), ok.Received!.ApiKey);
        Assert.True(ok2.Received!.Enabled);
        Assert.NotNull(failing.Received);
    }

    private sealed class FakeTarget(string tenantId) : IRerankerGrantPushTarget
    {
        public string TenantId { get; } = tenantId;

        public bool Throw { get; init; }

        public RerankerGrant? Received { get; private set; }

        public Task ProvideRerankerGrantAsync(RerankerGrant grant, CancellationToken cancellationToken)
        {
            this.Received = grant;
            return this.Throw ? throw new InvalidOperationException("tenant down") : Task.CompletedTask;
        }
    }
}
````

- [ ] **Step 2: Run it and confirm it fails**

```powershell
dotnet test tests/Cortex.Contained.Bridge.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~RerankerSidecarLifecycleTests|FullyQualifiedName~CredentialsPusherRerankerGrantTests"
```

Expected: FAIL — the build fails: `IRerankerComposeRunner`, `RerankerSidecarLifecycle`, `RerankerSettingsConfig`, `GetOrCreateRerankerKey` and the push seam do not exist yet.

- [ ] **Step 3: Implement**

**Modify** `src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs b/src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs
index 0869558..35dbcd1 100644
--- a/src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs
+++ b/src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs
@@ -56,6 +56,7 @@ public sealed partial class CredentialsPusher : ICredentialReplisher
     {
         // The storage key goes first: the agent's encrypted stores open only once it arrives.
         await this.PushStorageKeysAsync(cancellationToken).ConfigureAwait(false);
+        await this.PushRerankerGrantAsync(cancellationToken).ConfigureAwait(false);
 
         var providers = await this.BuildProviderCredentialsAsync(cancellationToken).ConfigureAwait(false);
 
@@ -153,6 +154,58 @@ public sealed partial class CredentialsPusher : ICredentialReplisher
         }
     }
 
+    /// <summary>
+    /// Pushes the reranker switch (<c>Reranker:Enabled</c>) and, when enabled, the sidecar's DPAPI-held API
+    /// key to every connected tenant's agent. A disabled switch is pushed too, so a running agent turns
+    /// reranking off when the owner disables it.
+    /// </summary>
+    private async Task PushRerankerGrantAsync(CancellationToken cancellationToken)
+    {
+        RerankerGrant grant;
+        try
+        {
+            grant = this.BuildRerankerGrant();
+        }
+#pragma warning disable CA1031 // A secrets-file problem must not block the credential push
+        catch (Exception ex)
+        {
+            this.LogRerankerGrantPushFailed("*", ex.Message);
+            return;
+        }
+#pragma warning restore CA1031
+
+        var targets = this.tenantRouter.GetConnectedTenantIds()
+            .Select(tenantId => (tenantId, client: this.tenantRouter.GetClient(tenantId)))
+            .Where(t => t.client?.IsConnected == true)
+            .Select(t => new RerankerGrantPushTarget(t.tenantId, t.client!))
+            .ToList();
+        await this.PushRerankerGrantToTargetsAsync(grant, targets, cancellationToken).ConfigureAwait(false);
+    }
+
+    /// <summary>The grant for the current configuration: the key only when reranking is enabled.</summary>
+    internal RerankerGrant BuildRerankerGrant() => this.config.Reranker.Enabled
+        ? new RerankerGrant { Enabled = true, ApiKey = this.secretManager.GetOrCreateRerankerKey() }
+        : new RerankerGrant { Enabled = false };
+
+    /// <summary>Pushes <paramref name="grant"/> to each target, isolating per-tenant failures (testable seam).</summary>
+    internal async Task PushRerankerGrantToTargetsAsync(
+        RerankerGrant grant, IReadOnlyList<IRerankerGrantPushTarget> targets, CancellationToken cancellationToken)
+    {
+        foreach (var target in targets)
+        {
+            try
+            {
+                await target.ProvideRerankerGrantAsync(grant, cancellationToken).ConfigureAwait(false);
+            }
+#pragma warning disable CA1031 // Individual tenant failures should not block others
+            catch (Exception ex)
+            {
+                this.LogRerankerGrantPushFailed(target.TenantId, ex.Message);
+            }
+#pragma warning restore CA1031
+        }
+    }
+
     /// <summary>
     /// Builds the per-provider <see cref="LlmProviderCredential"/> list pushed to agents from
     /// the current config. For a Copilot provider, mints a short-lived bearer via
@@ -569,6 +622,9 @@ public sealed partial class CredentialsPusher : ICredentialReplisher
     [LoggerMessage(Level = LogLevel.Error, Message = "Could not push the storage key to tenant {TenantId}: {Error}")]
     private partial void LogStorageKeyPushFailed(string tenantId, string error);
 
+    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not push the reranker grant to tenant {TenantId}: {Error}")]
+    private partial void LogRerankerGrantPushFailed(string tenantId, string error);
+
     [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to push credentials to tenant '{TenantId}': {Error}")]
     private partial void LogCredentialPushToTenantFailed(string tenantId, string error);
 
@@ -609,6 +665,36 @@ public sealed partial class CredentialsPusher : ICredentialReplisher
     private partial void LogAgentConfigPushToTenantFailed(string tenantId, string error);
 }
 
+/// <summary>
+/// One connected tenant the reranker grant is pushed to. Abstracted so the multi-tenant push loop can
+/// be unit-tested without the sealed <c>TenantRouter</c>/<c>HubClient</c>.
+/// </summary>
+internal interface IRerankerGrantPushTarget
+{
+    /// <summary>The tenant ID, used for per-tenant failure logging.</summary>
+    string TenantId { get; }
+
+    /// <summary>Pushes the grant to this tenant's agent.</summary>
+    Task ProvideRerankerGrantAsync(RerankerGrant grant, CancellationToken cancellationToken);
+}
+
+/// <summary>Adapts a connected <see cref="Hub.HubClient"/> to <see cref="IRerankerGrantPushTarget"/>.</summary>
+internal sealed class RerankerGrantPushTarget : IRerankerGrantPushTarget
+{
+    private readonly Hub.HubClient client;
+
+    public RerankerGrantPushTarget(string tenantId, Hub.HubClient client)
+    {
+        this.TenantId = tenantId;
+        this.client = client;
+    }
+
+    public string TenantId { get; }
+
+    public Task ProvideRerankerGrantAsync(RerankerGrant grant, CancellationToken cancellationToken)
+        => this.client.ProvideRerankerGrantAsync(grant, cancellationToken);
+}
+
 /// <summary>
 /// One connected tenant the memory config is pushed to. Abstracted so the multi-tenant
 /// push loop can be unit-tested without the sealed <c>TenantRouter</c>/<c>HubClient</c>.
````

**Modify** `src/Cortex.Contained.Bridge/Hub/HubClient.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Bridge/Hub/HubClient.cs b/src/Cortex.Contained.Bridge/Hub/HubClient.cs
index 62db15c..91877f4 100644
--- a/src/Cortex.Contained.Bridge/Hub/HubClient.cs
+++ b/src/Cortex.Contained.Bridge/Hub/HubClient.cs
@@ -299,6 +299,16 @@ public sealed partial class HubClient : IAsyncDisposable
             cancellationToken).ConfigureAwait(false);
     }
 
+    /// <summary>Push the reranker switch and the sidecar API key (increment 5).</summary>
+    public async Task ProvideRerankerGrantAsync(RerankerGrant grant, CancellationToken cancellationToken)
+    {
+        EnsureConnected();
+        await this.connection!.InvokeAsync(
+            nameof(IAgentHub.ProvideRerankerGrant),
+            grant,
+            cancellationToken).ConfigureAwait(false);
+    }
+
     /// <summary>Push LLM provider credentials to the agent.</summary>
     public async Task ProvideCredentialsAsync(LlmCredentials credentials, CancellationToken cancellationToken)
     {
````

**Modify** `src/Cortex.Contained.Bridge/Program.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Bridge/Program.cs b/src/Cortex.Contained.Bridge/Program.cs
index ae03b19..ae868b4 100644
--- a/src/Cortex.Contained.Bridge/Program.cs
+++ b/src/Cortex.Contained.Bridge/Program.cs
@@ -1087,6 +1087,8 @@ builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.IEmbeddingsComposeR
     sp => sp.GetRequiredService<Cortex.Contained.Bridge.Speech.DockerComposeCommandRunner>());
 builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.IVoiceIdComposeRunner>(
     sp => sp.GetRequiredService<Cortex.Contained.Bridge.Speech.DockerComposeCommandRunner>());
+builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.IRerankerComposeRunner>(
+    sp => sp.GetRequiredService<Cortex.Contained.Bridge.Speech.DockerComposeCommandRunner>());
 builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.DanishTtsLifecycle>();
 // Restarts uni-voices / whisper-stt when they report healthy but fail every request
 // (poisoned CUDA context) — otherwise voice stays dead until someone notices by ear.
@@ -1094,6 +1096,10 @@ builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.SpeechSidecarFaultR
 builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.SttSidecarLifecycle>();
 builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.EmbeddingsSidecarLifecycle>();
 builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.VoiceIdSidecarLifecycle>();
+builder.Services.AddSingleton(sp => new Cortex.Contained.Bridge.Speech.RerankerSidecarLifecycle(
+    sp.GetRequiredService<Cortex.Contained.Bridge.Speech.IRerankerComposeRunner>(),
+    secretManager.GetOrCreateRerankerKey,
+    sp.GetRequiredService<ILogger<Cortex.Contained.Bridge.Speech.RerankerSidecarLifecycle>>()));
 
 var app = builder.Build();
 
@@ -1138,6 +1144,13 @@ _ = modelCatalog.InitializeAsync(); // Fire-and-forget; data will be ready befor
     _ = Task.Run(() => embeddingsLifecycle.ReconcileAsync(memEnabled, CancellationToken.None));
 }
 
+// --- Reranker sidecar lifecycle: converge with Reranker:Enabled at startup (also picks up a new image) ---
+{
+    var rerankerLifecycle = app.Services.GetRequiredService<Cortex.Contained.Bridge.Speech.RerankerSidecarLifecycle>();
+    var rerankerEnabled = app.Services.GetRequiredService<BridgeConfig>().Reranker.Enabled;
+    _ = Task.Run(() => rerankerLifecycle.ReconcileAsync(rerankerEnabled, CancellationToken.None));
+}
+
 // --- Voice-id sidecar lifecycle: converge with effective voice-id flag at startup ---
 {
     var voiceIdLifecycle = app.Services.GetRequiredService<Cortex.Contained.Bridge.Speech.VoiceIdSidecarLifecycle>();
````

**Modify** `src/Cortex.Contained.Bridge/Security/SecretManager.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Bridge/Security/SecretManager.cs b/src/Cortex.Contained.Bridge/Security/SecretManager.cs
index 01c5c10..dcecaa9 100644
--- a/src/Cortex.Contained.Bridge/Security/SecretManager.cs
+++ b/src/Cortex.Contained.Bridge/Security/SecretManager.cs
@@ -303,6 +303,40 @@ public sealed partial class SecretManager
         }
     }
 
+    /// <summary>
+    /// The API key of the <c>cortex-reranker</c> sidecar (64 hex characters, 256-bit), created on first use
+    /// and kept DPAPI-encrypted. The Bridge passes it to the sidecar when starting it and pushes it to the
+    /// agents. If it cannot be decrypted a new one is generated: the next sidecar start recreates the
+    /// container with it (the key is part of its configuration) and the next push updates the agents.
+    /// </summary>
+    public string GetOrCreateRerankerKey()
+    {
+        lock (this.fileLock)
+        {
+            EnsureSecretsDirectory();
+
+            var filePath = Path.Combine(this.secretsDir, SecretsFileName);
+            var secrets = LoadSecrets(filePath);
+            if (secrets.RerankerKey is not null)
+            {
+                try
+                {
+                    return this.store.Unprotect(secrets.RerankerKey);
+                }
+                catch (Exception ex)
+                {
+                    this.LogRerankerKeyDecryptionFailed(ex.Message);
+                }
+            }
+
+            var key = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
+            secrets.RerankerKey = this.store.Protect(key);
+            SaveSecrets(filePath, secrets);
+            this.LogRerankerKeyGenerated();
+            return key;
+        }
+    }
+
     /// <summary>
     /// Regenerates the hub token, encrypts and saves the new one. Returns the plaintext.
     /// </summary>
@@ -558,6 +592,12 @@ public sealed partial class SecretManager
     [LoggerMessage(Level = LogLevel.Information, Message = "Discord bot token stored in encrypted storage")]
     private partial void LogDiscordBotTokenStored();
 
+    [LoggerMessage(Level = LogLevel.Information, Message = "Reranker API key generated and saved to encrypted storage")]
+    private partial void LogRerankerKeyGenerated();
+
+    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to decrypt the reranker API key: {ErrorMessage}. Regenerating...")]
+    private partial void LogRerankerKeyDecryptionFailed(string errorMessage);
+
     [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to decrypt Discord bot token: {ErrorMessage}")]
     private partial void LogDiscordBotTokenDecryptionFailed(string errorMessage);
 
@@ -578,5 +618,7 @@ public sealed partial class SecretManager
         public string? DiscordBotToken { get; set; }
         /// <summary>DPAPI-encrypted per-tenant storage master keys (Base64-encoded 256-bit), keyed by tenant ID.</summary>
         public Dictionary<string, string>? TenantStorageKeys { get; set; }
+        /// <summary>DPAPI-encrypted API key of the cortex-reranker sidecar (hex, 256-bit).</summary>
+        public string? RerankerKey { get; set; }
     }
 }
````

**Modify** `src/Cortex.Contained.Bridge/Speech/DockerComposeCommandRunner.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Bridge/Speech/DockerComposeCommandRunner.cs b/src/Cortex.Contained.Bridge/Speech/DockerComposeCommandRunner.cs
index 0cc9057..c057ea7 100644
--- a/src/Cortex.Contained.Bridge/Speech/DockerComposeCommandRunner.cs
+++ b/src/Cortex.Contained.Bridge/Speech/DockerComposeCommandRunner.cs
@@ -9,12 +9,16 @@ namespace Cortex.Contained.Bridge.Speech;
 /// <see cref="Tenants.DockerContainerManager"/>. The compose file is resolved to
 /// <c>%LOCALAPPDATA%\Cortex\docker-compose.yml</c>.
 /// </summary>
-public sealed partial class DockerComposeCommandRunner : IComposeCommandRunner, ISttComposeRunner, IEmbeddingsComposeRunner, IVoiceIdComposeRunner
+public sealed partial class DockerComposeCommandRunner : IComposeCommandRunner, ISttComposeRunner, IEmbeddingsComposeRunner, IVoiceIdComposeRunner, IRerankerComposeRunner
 {
     private const string DanishContainerName = "cortex-uni-voices";
     private const string SttContainerName = "cortex-stt";
     private const string EmbeddingsContainerName = "cortex-embeddings";
     private const string VoiceIdContainerName = "cortex-voice-id";
+    private const string RerankerContainerName = "cortex-reranker";
+
+    /// <summary>Environment variable compose interpolates into the reranker's <c>LLAMA_API_KEY</c>.</summary>
+    internal const string RerankerKeyVariable = "CORTEX_RERANKER_API_KEY";
 
     /// <summary>Image pull/start can be slow on first run, so allow a generous window.</summary>
     private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
@@ -128,6 +132,28 @@ public sealed partial class DockerComposeCommandRunner : IComposeCommandRunner,
     public Task<bool> IsVoiceIdRunningAsync(CancellationToken cancellationToken)
         => this.IsContainerRunningAsync(VoiceIdContainerName, cancellationToken);
 
+    /// <inheritdoc />
+    public Task<bool> StartRerankerAsync(string apiKey, CancellationToken cancellationToken)
+    {
+        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
+        return this.RunCommandAsync(
+            $"compose -f \"{this.composeFilePath}\" --profile reranker up -d reranker",
+            StartTimeout,
+            cancellationToken,
+            new Dictionary<string, string> { [RerankerKeyVariable] = apiKey });
+    }
+
+    /// <inheritdoc />
+    public Task<bool> StopRerankerAsync(CancellationToken cancellationToken)
+        => this.RunCommandAsync(
+            $"compose -f \"{this.composeFilePath}\" --profile reranker stop reranker",
+            ShortTimeout,
+            cancellationToken);
+
+    /// <inheritdoc />
+    public Task<bool> IsRerankerRunningAsync(CancellationToken cancellationToken)
+        => this.IsContainerRunningAsync(RerankerContainerName, cancellationToken);
+
     private async Task<bool> IsContainerRunningAsync(string containerName, CancellationToken cancellationToken)
     {
         try
@@ -167,7 +193,8 @@ public sealed partial class DockerComposeCommandRunner : IComposeCommandRunner,
         }
     }
 
-    private async Task<bool> RunCommandAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken)
+    private async Task<bool> RunCommandAsync(
+        string arguments, TimeSpan timeout, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null)
     {
         try
         {
@@ -186,6 +213,12 @@ public sealed partial class DockerComposeCommandRunner : IComposeCommandRunner,
                 },
             };
 
+            // Secrets travel in the child's environment only: never on the command line, never logged.
+            foreach (var (name, value) in environment ?? new Dictionary<string, string>())
+            {
+                process.StartInfo.Environment[name] = value;
+            }
+
             process.Start();
 
             using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
````

**Create** `src/Cortex.Contained.Bridge/Speech/IRerankerComposeRunner.cs`:

````csharp
namespace Cortex.Contained.Bridge.Speech;

/// <summary>Narrow seam over the docker-compose CLI for the cortex-reranker sidecar.</summary>
public interface IRerankerComposeRunner
{
    /// <summary>
    /// `docker compose --profile reranker up -d reranker` with <paramref name="apiKey"/> in the process
    /// environment (<c>CORTEX_RERANKER_API_KEY</c>, never on the command line). Compose recreates the
    /// container when the key or the <c>cortex-reranker:latest</c> image changed. Returns true on exit 0.
    /// </summary>
    Task<bool> StartRerankerAsync(string apiKey, CancellationToken cancellationToken);

    /// <summary>`docker compose --profile reranker stop reranker`. Returns true on exit 0.</summary>
    Task<bool> StopRerankerAsync(CancellationToken cancellationToken);

    /// <summary>True if the `cortex-reranker` container is currently running.</summary>
    Task<bool> IsRerankerRunningAsync(CancellationToken cancellationToken);
}
````

**Create** `src/Cortex.Contained.Bridge/Speech/RerankerSidecarLifecycle.cs`:

````csharp
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Converges the cortex-reranker sidecar with <c>Reranker:Enabled</c>: when enabled it always runs
/// `compose up` with the DPAPI-held API key (idempotent; compose recreates the container after a key
/// or image change, which is how a Self-Update image upgrade or rollback reaches the sidecar); when
/// disabled it stops a running sidecar. Failures are logged, never propagated.
/// </summary>
public sealed partial class RerankerSidecarLifecycle
{
    private readonly IRerankerComposeRunner runner;
    private readonly Func<string> apiKey;
    private readonly ILogger<RerankerSidecarLifecycle> logger;

    public RerankerSidecarLifecycle(IRerankerComposeRunner runner, Func<string> apiKey, ILogger<RerankerSidecarLifecycle> logger)
    {
        this.runner = runner;
        this.apiKey = apiKey;
        this.logger = logger;
    }

    public async Task ReconcileAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            if (enabled)
            {
                this.LogConverging();
                if (!await this.runner.StartRerankerAsync(this.apiKey(), cancellationToken).ConfigureAwait(false))
                {
                    this.LogStartFailed();
                }
            }
            else if (await this.runner.IsRerankerRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                this.LogStopping();
                await this.runner.StopRerankerAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            this.LogReconcileFailed(ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "reranker enabled — converging cortex-reranker (compose up)")]
    private partial void LogConverging();

    [LoggerMessage(Level = LogLevel.Warning, Message = "cortex-reranker did not start; searches keep the fused order until it does")]
    private partial void LogStartFailed();

    [LoggerMessage(Level = LogLevel.Information, Message = "reranker disabled — stopping cortex-reranker")]
    private partial void LogStopping();

    [LoggerMessage(Level = LogLevel.Warning, Message = "cortex-reranker reconcile failed: {Error}")]
    private partial void LogReconcileFailed(string error);
}
````

**Modify** `src/Cortex.Contained.Contracts/Config/BridgeConfig.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Contracts/Config/BridgeConfig.cs b/src/Cortex.Contained.Contracts/Config/BridgeConfig.cs
index 64564fb..897ab94 100644
--- a/src/Cortex.Contained.Contracts/Config/BridgeConfig.cs
+++ b/src/Cortex.Contained.Contracts/Config/BridgeConfig.cs
@@ -31,6 +31,9 @@ public sealed class BridgeConfig
     /// <summary>Memory service settings (thresholds, compaction).</summary>
     public MemorySettingsConfig Memory { get; set; } = new();
 
+    /// <summary>Reranker sidecar for history and memory search (episodic history, increment 5).</summary>
+    public RerankerSettingsConfig Reranker { get; set; } = new();
+
     /// <summary>
     /// Maximum tool-call rounds for sub-agents (0 = default 200).
     /// The real termination signals are context window and doom loop detection;
@@ -57,6 +60,17 @@ public sealed class BridgeConfig
     public ConnectorSettingsConfig Connectors { get; set; } = new();
 }
 
+/// <summary>Reranker settings persisted on the Bridge host.</summary>
+public sealed class RerankerSettingsConfig
+{
+    /// <summary>
+    /// Master reranker switch (<c>Reranker:Enabled</c>, default false). When true the Bridge starts the
+    /// <c>cortex-reranker</c> sidecar with its DPAPI-held API key and pushes the switch and the key to the
+    /// agents, which then rerank hybrid history and memory results. Enable only after Gate R passed.
+    /// </summary>
+    public bool Enabled { get; set; }
+}
+
 /// <summary>Memory service settings persisted on the Bridge host.</summary>
 public sealed class MemorySettingsConfig
 {
````

- [ ] **Step 4: Run the tests and confirm they pass**

```powershell
dotnet test tests/Cortex.Contained.Bridge.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~RerankerSidecarLifecycleTests|FullyQualifiedName~CredentialsPusherRerankerGrantTests"
```

Expected: PASS — `Passed: 7`.

- [ ] **Step 5: Commit** (never stage `version.json`)

```powershell
git add src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs src/Cortex.Contained.Bridge/Hub/HubClient.cs src/Cortex.Contained.Bridge/Program.cs src/Cortex.Contained.Bridge/Security/SecretManager.cs src/Cortex.Contained.Bridge/Speech/DockerComposeCommandRunner.cs src/Cortex.Contained.Bridge/Speech/IRerankerComposeRunner.cs src/Cortex.Contained.Bridge/Speech/RerankerSidecarLifecycle.cs src/Cortex.Contained.Contracts/Config/BridgeConfig.cs tests/Cortex.Contained.Bridge.Tests/Hosting/CredentialsPusherRerankerGrantTests.cs tests/Cortex.Contained.Bridge.Tests/Speech/RerankerSidecarLifecycleTests.cs
git commit -m "feat(bridge): DPAPI reranker key, sidecar lifecycle and reranker grant push" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 10: Self-Update coverage: build, pin, health gate, rollback

**Model:** standard

I5-13. `Build-RerankerImage.ps1` (variant auto-detect, `:<version>` + `:latest`, keep 2); `Build-All` rebuilds it once the machine has built it (or `-WithReranker`); `Build-Launcher` adds `images.cortex-reranker` and an `imageIds` map (image id per tag) to the manifest; `Self-Update` verifies every pin before deploying, snapshots the running reranker as `cortex-reranker:rollback`, gates on the recreated sidecar running the pinned id with a passing healthcheck (`Test-RerankerAt`: healthy/stopped/absent pass, unhealthy fails), and restores the tag on rollback. Docker probes are wrapped in `try` because Windows PowerShell turns redirected native stderr into a terminating error under `$ErrorActionPreference='Stop'`. `docs/self-update.md` documents upgrade, downgrade, rollback and the drill.

**Files:**
- Modify: `docs/self-update.md`
- Modify: `scripts/Build-All.ps1`
- Modify: `scripts/Build-Launcher.ps1`
- Create: `scripts/Build-RerankerImage.ps1`
- Modify: `scripts/Self-Update.ps1`

- [ ] **Step 1: Implement**

**Modify** `docs/self-update.md` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/docs/self-update.md b/docs/self-update.md
index 30c044f..98f945f 100644
--- a/docs/self-update.md
+++ b/docs/self-update.md
@@ -46,17 +46,47 @@ automatically.
 
 ## What the deploy does (detached, automatic)
 
-1. re-verify the pinned manifest (sha256 + signature) — refuse on any mismatch;
+1. re-verify the pinned manifest (sha256 + signature, and the image pins: every tag in `images` must
+   still point at the image id recorded in `imageIds` at build time) — refuse on any mismatch;
 2. `docker compose up -d --force-recreate cortex-agent voice-id` (new images);
 3. `Add-AppxPackage -ForceUpdateFromAnyVersion -ForceApplicationShutdown` (installs the MSIX; stops
    the old Bridge — this is what kills the coding session);
-4. relaunch the Bridge and poll `/health` until it reports the target version, healthy;
-5. **auto-rollback** to the last-known-good MSIX + previous image if it doesn't come back healthy;
+4. relaunch the Bridge and poll `/health` until it reports the target version, healthy; if the
+   reranker sidecar was running, also wait until the new Bridge has recreated it on the pinned
+   image and its healthcheck passes (see below);
+5. **auto-rollback** to the last-known-good MSIX + previous images (agent, and reranker when it was
+   running) if it doesn't come back healthy;
 6. write `artifacts/update-status.json` with the result.
 
 Check the outcome afterward: `Invoke-RestMethod http://localhost:5080/health` (new `version`) and
 `artifacts/update-status.json`.
 
+## The reranker sidecar (opt-in)
+
+`cortex-reranker` (episodic history increment 5, [docs/reranker.md](reranker.md)) is covered once this
+machine has built it:
+
+- **Build and pin.** `scripts/Build-RerankerImage.ps1` builds `docker/reranker` (llama.cpp server
+  pinned by digest, Qwen3-Reranker GGUF pinned by sha256) as `cortex-reranker:<version>` + `:latest`.
+  After that first build, `Build-All` (`-WithReranker` forces it) rebuilds it on every run — a cache hit
+  unless a pin changed — and `Build-Launcher` records `images.cortex-reranker` and its image id in
+  `imageIds`, which the deploy verifies.
+- **Upgrade.** The deploy does not touch the container itself: the new Bridge runs
+  `docker compose --profile reranker up -d reranker` with the DPAPI-held key on startup, and compose
+  recreates the container because `cortex-reranker:latest` changed. The health gate then waits until the
+  container runs the pinned image id and its healthcheck (`/health`) passes; a reranker that never gets
+  there fails the gate and triggers the rollback. If the Bridge has `Reranker:Enabled=false` it stops the
+  sidecar instead, and there is nothing to gate.
+- **Rollback.** Before deploying, the image of the *running* sidecar is tagged `cortex-reranker:rollback`;
+  a rollback restores it as `:latest`, and the last-known-good Bridge recreates the sidecar from it.
+- **Downgrade.** Check out the older commit in the working tree and run `Self-Update.ps1 -Schedule
+  -SkipPull`: its Dockerfile pins the older server and model, the build produces that image (a cache hit
+  if it still exists), and the manifest pins it. A quick manual downgrade between deploys:
+  `docker tag cortex-reranker:<older version> cortex-reranker:latest`, then restart the Bridge (the build
+  keeps the two newest versioned tags).
+- **Drill.** `Self-Update.ps1 -RollbackDrill -SkipBuild -TargetVersion <installed>` exercises the
+  reranker snapshot/restore together with the rest of the rollback.
+
 ## Why detached (do NOT deploy inline from coda)
 
 coda `serve` is a child of the Bridge, in a Windows Job Object with `KILL_ON_JOB_CLOSE`. Stopping the
@@ -84,5 +114,6 @@ let itself be replaced. Do not run `Self-Update.ps1 -Apply` directly from a coda
 - Runs per-user and needs an interactive logged-on session (per-user `Add-AppxPackage`, the AUMID
   relaunch, and Docker Desktop all require it). The Scheduled Task is registered "run when logged on".
 - Concurrency-guarded: it refuses to schedule/run if an update is already in progress.
-- Only the **MSIX** (Bridge/Launcher/bundled coda) and **Docker images** (agent/voice) are updated;
-  there is no separate step needed — `Build-All` + the deploy cover both.
+- Only the **MSIX** (Bridge/Launcher/bundled coda) and **Docker images** (agent/voice, and the
+  reranker sidecar once this machine built it) are updated; there is no separate step needed —
+  `Build-All` + the deploy cover them.
````

**Modify** `scripts/Build-All.ps1` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/scripts/Build-All.ps1 b/scripts/Build-All.ps1
index f308f36..2a31043 100644
--- a/scripts/Build-All.ps1
+++ b/scripts/Build-All.ps1
@@ -18,6 +18,10 @@
 .PARAMETER NoPrune
     Skip old Docker image cleanup.
 
+.PARAMETER WithReranker
+    Also build the cortex-reranker sidecar image even if this machine has not built it before
+    (once built, it is rebuilt on every run so Self-Update keeps pinning it).
+
 .PARAMETER CertThumbprint
     Optional exact thumbprint for MSIX signing. Overrides subject lookup. Defaults
     to the CORTEX_SIGNING_THUMBPRINT environment variable. When empty, Build-Launcher
@@ -36,6 +40,7 @@ param(
     [switch]$SkipDocker,
     [switch]$SkipMsix,
     [switch]$NoPrune,
+    [switch]$WithReranker,
     [string]$CertThumbprint = $env:CORTEX_SIGNING_THUMBPRINT,
     [string]$CertSubject = $env:CORTEX_SIGNING_SUBJECT
 )
@@ -62,6 +67,18 @@ if (-not $SkipDocker) {
     if ($LASTEXITCODE -ne 0) { throw "voice-id build failed" }
     Write-Host ""
 
+    # The reranker sidecar is opt-in (~4 GB of base image and model on first build): it is rebuilt
+    # (a cache hit unless its pins changed) once this machine has built it, so Self-Update keeps
+    # covering it, or when -WithReranker asks for it.
+    $hasReranker = $false
+    try { & docker image inspect cortex-reranker:latest *> $null; $hasReranker = ($LASTEXITCODE -eq 0) } catch { $hasReranker = $false }
+    if ($WithReranker -or $hasReranker) {
+        Write-Host "=== Building cortex-reranker Docker image ===" -ForegroundColor Cyan
+        & (Join-Path $scriptDir "Build-RerankerImage.ps1") @buildArgs
+        if ($LASTEXITCODE -ne 0) { throw "cortex-reranker build failed" }
+        Write-Host ""
+    }
+
     # NOTE: The unified TTS sidecar (uni-voices) is NOT built here. Its image is
     # large (all weights baked in) and is published to GHCR / pulled via
     # `docker compose --profile tts build uni-voices`. See lib/uni-voices.
````

**Modify** `scripts/Build-Launcher.ps1` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/scripts/Build-Launcher.ps1 b/scripts/Build-Launcher.ps1
index 72040fa..987d415 100644
--- a/scripts/Build-Launcher.ps1
+++ b/scripts/Build-Launcher.ps1
@@ -222,5 +222,22 @@ $manifest = [ordered]@{
     }
     builtAt   = (Get-Date).ToUniversalTime().ToString('o')
 }
+
+# The reranker sidecar is opt-in: record it only on machines that built it.
+try {
+    & docker image inspect cortex-reranker:latest *> $null
+    if ($LASTEXITCODE -eq 0) { $manifest.images['cortex-reranker'] = 'cortex-reranker:latest' }
+} catch { }
+
+# Image pins: the id (sha256 of the image config) each tag had at build time. Self-Update refuses to
+# deploy when a tag no longer points at its pinned image, and gates the reranker on running exactly it.
+$imageIds = [ordered]@{}
+foreach ($entry in $manifest.images.GetEnumerator()) {
+    try {
+        $id = (& docker image inspect $entry.Value --format '{{.Id}}' 2>$null)
+        if ($LASTEXITCODE -eq 0 -and $id) { $imageIds[$entry.Key] = "$id".Trim() }
+    } catch { }
+}
+if ($imageIds.Count -gt 0) { $manifest['imageIds'] = $imageIds }
 $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding UTF8
 Write-Host "Build manifest: $manifestPath" -ForegroundColor Green
````

**Create** `scripts/Build-RerankerImage.ps1`:

````powershell
<#
.SYNOPSIS
    Builds the cortex-reranker sidecar image (llama.cpp server + baked reranker model, all pinned).

.DESCRIPTION
    Reads the version from docker/reranker/version.json and builds docker/reranker/Dockerfile with two
    tags: cortex-reranker:<version> + cortex-reranker:latest. The Dockerfile pins the llama.cpp server
    image by digest and the GGUF model by sha256, so a rebuild of the same version yields the same
    content (and, with the build cache, the same image id). Prunes old versioned images, keeping 2.

    Building it once opts this machine in: from then on Build-All rebuilds it on every Self-Update and
    the manifest pins its image id. The Bridge only runs it when Reranker:Enabled is true.

.PARAMETER Variant
    cuda (GPU, falls back to CPU without one) or cpu (small CPU-only image). Default: cuda when
    nvidia-smi is available, else cpu. The Gate R latency budget needs the GPU (docs/reranker.md).

.PARAMETER NoPrune
    Skip the old image cleanup step.

.EXAMPLE
    .\Build-RerankerImage.ps1
    .\Build-RerankerImage.ps1 -Variant cpu -NoPrune
#>
param(
    [ValidateSet('', 'cuda', 'cpu')]
    [string]$Variant = '',
    [switch]$NoPrune
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$contextDir = Join-Path $repoRoot "docker\reranker"

if (-not $Variant) {
    $Variant = if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) { 'cuda' } else { 'cpu' }
}

$ver = Get-Content (Join-Path $contextDir "version.json") -Raw | ConvertFrom-Json
$version = "$($ver.major).$($ver.minor).$($ver.patch)"

Write-Host "Building cortex-reranker:$version ($Variant)" -ForegroundColor Cyan

docker build `
    --build-arg "VARIANT=$Variant" `
    --label "cortex.reranker.variant=$Variant" `
    -t "cortex-reranker:$version" `
    -t "cortex-reranker:latest" `
    $contextDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker build failed"
    exit 1
}

$imageId = docker image inspect cortex-reranker:latest --format '{{.Id}}'
Write-Host ""
Write-Host "Built cortex-reranker:$version ($Variant), image $imageId" -ForegroundColor Green

if (-not $NoPrune) {
    Write-Host ""
    Write-Host "Pruning old cortex-reranker images..." -ForegroundColor Yellow

    $allTags = docker images cortex-reranker --format "{{.Tag}}" |
        Where-Object { $_ -match '^\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_ } -Descending

    foreach ($tag in ($allTags | Select-Object -Skip 2)) {
        Write-Host "  Removing cortex-reranker:$tag"
        docker rmi "cortex-reranker:$tag" 2>$null
    }

    docker image prune -f 2>$null | Out-Null
    Write-Host "Cleanup complete." -ForegroundColor Green
}

Write-Host ""
Write-Host "Version: $version" -ForegroundColor Cyan
````

**Modify** `scripts/Self-Update.ps1` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/scripts/Self-Update.ps1 b/scripts/Self-Update.ps1
index 74808c8..366e8b5 100644
--- a/scripts/Self-Update.ps1
+++ b/scripts/Self-Update.ps1
@@ -97,6 +97,7 @@ $statusPath = Join-Path $artifacts 'update-status.json'
 $lockPath = Join-Path $artifacts '.self-update.lock'
 $lkgMsix = Join-Path $artifacts 'last-known-good.msix'
 $rollbackImageTag = 'cortex-agent:rollback'
+$rerankerRollbackImageTag = 'cortex-reranker:rollback'
 $log = [System.Collections.Generic.List[string]]::new()
 
 function Say([string]$msg, [string]$color = 'Cyan') {
@@ -136,6 +137,23 @@ function Test-HealthAt([string]$expectVersion) {
     return $false
 }
 
+function Test-RerankerAt([string]$expectImageId) {
+    # The Bridge reconciles cortex-reranker on startup: a changed image or key makes compose recreate it.
+    # Wait until it runs $expectImageId and its healthcheck passes. 'stopped'/'absent' mean the Bridge
+    # has reranking disabled (nothing to gate); 'unhealthy' means it never got there within the budget.
+    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
+    while ((Get-Date) -lt $deadline) {
+        $state = $null
+        try { $state = (& docker inspect cortex-reranker --format '{{.State.Status}}|{{.Image}}|{{if .State.Health}}{{.State.Health.Status}}{{end}}' 2>$null) } catch { }
+        if (-not $state) { return 'absent' }
+        $status, $image, $health = "$state".Trim() -split '\|'
+        if ($status -eq 'exited') { return 'stopped' }
+        if ($image -eq $expectImageId -and $health -eq 'healthy') { return 'healthy' }
+        Start-Sleep -Seconds 3
+    }
+    return 'unhealthy'
+}
+
 function Register-DeployTask([string]$version) {
     # Register a ONE-SHOT Scheduled Task that runs THIS script with -Apply after a short delay.
     # The Task Scheduler service owns the task's process — it is NOT in coda's / the Bridge's Job
@@ -320,6 +338,18 @@ try {
     if ($sigThumb -ne $m.msix.certThumbprint) { throw "MSIX signer thumbprint $sigThumb != manifest $($m.msix.certThumbprint) — refusing." }
     Say "manifest verified: sha256 OK, signature Valid, thumbprint OK" 'Green'
 
+    # Image pins (older manifests have none): every tag must still point at the image id recorded at
+    # build time, so the deploy ships exactly the images that were built and test-gated.
+    if ($m.imageIds) {
+        foreach ($pin in $m.imageIds.PSObject.Properties) {
+            $tag = $m.images.($pin.Name)
+            $actualId = $null
+            try { $actualId = (& docker image inspect $tag --format '{{.Id}}' 2>$null) } catch { }
+            if ("$actualId".Trim() -ne $pin.Value) { throw "Image $tag is '$actualId' but the manifest pins $($pin.Value) — refusing (rebuilt or retagged since the build?)." }
+        }
+        Say "image pins verified: $((@($m.imageIds.PSObject.Properties) | ForEach-Object Name) -join ', ')" 'Green'
+    }
+
     # --- 5. Gate: schedule (detached, for coda) / dry-run / deploy inline -----
     if ($Schedule) {
         Register-DeployTask $resolvedVersion
@@ -358,6 +388,20 @@ try {
         }
     } catch { Say "could not snapshot agent image for rollback: $_" 'Yellow' }
 
+    # The reranker sidecar (opt-in) is recreated by the new Bridge on startup (compose recreates it when
+    # cortex-reranker:latest changed). Snapshot the image the RUNNING sidecar uses, for rollback.
+    $rerankerWasRunning = $false
+    $rerankerRollbackSaved = $false
+    try {
+        $runningReranker = (& docker inspect cortex-reranker --format '{{if .State.Running}}{{.Image}}{{end}}' 2>$null)
+        if ($runningReranker) {
+            $rerankerWasRunning = $true
+            & docker tag $runningReranker $rerankerRollbackImageTag 2>$null
+            $rerankerRollbackSaved = $true
+            Say "snapshotted running reranker image ($runningReranker) as $rerankerRollbackImageTag"
+        }
+    } catch { Say "could not snapshot reranker image for rollback: $_" 'Yellow' }
+
     # --- 7. Deploy: containers, then MSIX ------------------------------------
     Say "recreating containers ($($m.images.'cortex-agent'), $($m.images.'voice-id'))..."
     & docker compose -f (Join-Path $repoRoot 'docker-compose.yml') up -d --force-recreate --no-deps cortex-agent voice-id 2>&1 | ForEach-Object { Say "docker: $_" 'Gray' }
@@ -372,6 +416,12 @@ try {
     # A rollback drill deploys the (known-good) target, then forces this gate to fail so the rollback
     # path below runs for real. Everything else is a genuine /health check.
     $healthy = if ($RollbackDrill) { Say "DRILL: forcing health-gate failure to exercise the rollback path" 'Yellow'; $false } else { Test-HealthAt $resolvedVersion }
+    if ($healthy -and $rerankerWasRunning -and $m.imageIds.'cortex-reranker') {
+        Say "verifying cortex-reranker runs the pinned image $($m.imageIds.'cortex-reranker') and is healthy..."
+        $rerankerState = Test-RerankerAt $m.imageIds.'cortex-reranker'
+        Say "reranker: $rerankerState" $(if ($rerankerState -eq 'unhealthy') { 'Red' } else { 'Green' })
+        $healthy = $rerankerState -ne 'unhealthy'
+    }
     if ($healthy) {
         Say "HEALTHY on $resolvedVersion — update complete" 'Green'
         Write-Status -ok $true -from $fromVersion -to $resolvedVersion -rolledBack $false -reason 'deployed'
@@ -384,10 +434,16 @@ try {
     if ($lkgSaved) {
         try {
             & docker tag $rollbackImageTag cortex-agent:latest 2>$null
+            # The last-known-good Bridge recreates the reranker from the restored tag when it starts.
+            if ($rerankerRollbackSaved) { & docker tag $rerankerRollbackImageTag cortex-reranker:latest 2>$null }
             & docker compose -f (Join-Path $repoRoot 'docker-compose.yml') up -d --force-recreate --no-deps cortex-agent 2>&1 | Out-Null
             Add-AppxPackage -Path $lkgMsix -ForceUpdateFromAnyVersion -ForceApplicationShutdown
             Start-Process "shell:AppsFolder\$Aumid"
             $rolledBack = Test-HealthAt $fromVersion
+            if ($rolledBack -and $rerankerRollbackSaved) {
+                $rollbackId = (& docker image inspect $rerankerRollbackImageTag --format '{{.Id}}' 2>$null)
+                Say "reranker after rollback: $(Test-RerankerAt "$rollbackId".Trim())"
+            }
             Say ("rollback " + $(if ($rolledBack) { 'HEALTHY on ' + $fromVersion } else { 'did NOT come healthy — MANUAL INTERVENTION NEEDED' })) $(if ($rolledBack) { 'Yellow' } else { 'Red' })
         } catch { Say "rollback error: $_" 'Red' }
     } else {
````

- [ ] **Step 2: Parse-check the scripts**

```powershell
foreach ($f in 'Self-Update.ps1', 'Build-All.ps1', 'Build-Launcher.ps1', 'Build-RerankerImage.ps1') {
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path "scripts\$f"), [ref]$null, [ref]$errors)
    "$f parse errors: $($errors.Count)"
}
```

Expected: `parse errors: 0` for all four.

- [ ] **Step 3 (optional, manual — needs Docker): exercise `Test-RerankerAt`**

```powershell
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path scripts\Self-Update.ps1), [ref]$null, [ref]$null)
$fn = $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $args[0].Name -eq 'Test-RerankerAt' }, $true) | Select-Object -First 1
$HealthTimeoutSeconds = 30; Invoke-Expression $fn.Extent.Text
Test-RerankerAt 'sha256:none'                                   # absent
docker run -d --name cortex-reranker --gpus all -e LLAMA_API_KEY=k cortex-reranker:latest
Test-RerankerAt (docker image inspect cortex-reranker:latest --format '{{.Id}}')   # healthy
$HealthTimeoutSeconds = 6; Test-RerankerAt 'sha256:other'       # unhealthy
docker stop cortex-reranker; Test-RerankerAt 'sha256:other'     # stopped
docker rm -f cortex-reranker
```

Expected (verified while writing this plan): `absent`, `healthy`, `unhealthy`, `stopped`. `.\scripts\Build-RerankerImage.ps1 -NoPrune` printed `Built cortex-reranker:0.1.0 (cuda)`. Building opts the machine in (Build-All then rebuilds it): on a machine that should not run the reranker, remove the tags afterwards (`docker image rm cortex-reranker:latest cortex-reranker:0.1.0`). The full drill is an owner task: `Self-Update.ps1 -RollbackDrill -SkipBuild -TargetVersion <installed>` with the sidecar running.

- [ ] **Step 4: Commit** (never stage `version.json`)

```powershell
git add docs/self-update.md scripts/Build-All.ps1 scripts/Build-Launcher.ps1 scripts/Build-RerankerImage.ps1 scripts/Self-Update.ps1
git commit -m "feat(self-update): build, pin, health-gate and roll back the reranker sidecar" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 11: Shared rerank evaluation: grid, curve, Gate R, embedder load

**Model:** standard

I5-14. `RerankGrid.Cells()` (TopN {10,15,30} × MaxDocChars {600,1200,2000}), `RerankConditions` (normal, embedder-busy, cold), `RerankEvaluation.CurveAsync` (no-rerank baseline + every cell, paired ΔnDCG@10 CI/p from the shared `AblationReporting`, rerank p50/p95, first-page p95, applied/failed/skipped), `RerankEvaluation.Gate` (CI low > 0, p < 0.05, p95 ≤ 2.5 s normal **and** embedder busy), Markdown/verdict text, `BackgroundLoad` (embedder-busy loops), and `RerankEvalSetup` (live client from `Reranker__*`, a stage with a 30 s curve timeout and a breaker that never opens). `LlamaServerReranker.ProbeHealthAsync` (public `/health`, no key) is added for the cold condition and the smoke check.

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Reranking/Evaluation/RerankEvaluation.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Reranking/LlamaServerReranker.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankEvaluationTests.cs`

- [ ] **Step 1: Write the failing test**

**Create** `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankEvaluationTests.cs`:

````csharp
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary>The latency/quality curve, the Gate R verdict and the embedder-busy load (§4.3).</summary>
public sealed class RerankEvaluationTests
{
    private static AblationScore Score(string config, string query, double ndcg, double latencyMs) =>
        new(config, query, "lookup", "synthetic", ndcg, 0, 0, 1, null, null, 0, false, latencyMs);

    [Fact]
    public void Grid_IsTheNineCellsOfTheSpec()
    {
        Assert.Equal(
            ["top10-chars600", "top10-chars1200", "top10-chars2000", "top15-chars600", "top15-chars1200", "top15-chars2000", "top30-chars600", "top30-chars1200", "top30-chars2000"],
            RerankGrid.Cells().Select(c => c.Label));
    }

    [Fact]
    public async Task CurveAsync_ComparesEachCellWithNoRerank_AndCountsOutcomes()
    {
        string[] queries = ["q1", "q2", "q3", "q4"];
        RerankBudget[] cells = [new(10, 600), new(30, 2000)];

        var (rows, report) = await RerankEvaluation.CurveAsync(
            queries,
            cells,
            RerankConditions.EmbedderBusy,
            (name, budget, query, _) =>
            {
                var outcome = budget is null ? null
                    : budget.TopN == 30 && query == "q4" ? RerankOutcome.Unchanged(3, RerankStatus.Failed, "failed", 2000)
                    : RerankOutcome.Unchanged(3, RerankStatus.Applied, "on", budget.TopN * 10);
                var ndcg = budget is null ? 0.5 : 0.7;
                return Task.FromResult(new RerankRun(Score(name, query, ndcg, 100 + (outcome?.LatencyMs ?? 0)), outcome));
            },
            CancellationToken.None);

        Assert.Equal(2, rows.Count);
        var big = rows[1];
        Assert.Equal(("embedder-busy", "top30-chars2000", 4), (big.Condition, big.Cell, big.Queries));
        Assert.Equal(0.2, big.DeltaNdcg, 6);
        Assert.Equal((3, 1, 0), (big.Applied, big.Failed, big.Skipped));
        Assert.Equal(100, rows[0].P50RerankMs);
        Assert.Contains(report.Summaries, s => s.Config == RerankEvaluation.NoRerank);
    }

    [Fact]
    public void Gate_SignificantGainWithinLatency_Passes()
    {
        var verdict = RerankEvaluation.Gate(new PairedComparison("hybrid-rerank", "hybrid-rerank-no-rerank", "all", 300, 0.04, 0.01, 0.07, 0.003), 1800, 2400);

        Assert.True(verdict.Passed);
    }

    [Fact]
    public void Gate_EmbedderBusyP95AboveBudget_Fails()
    {
        var verdict = RerankEvaluation.Gate(new PairedComparison("c", "b", "all", 300, 0.04, 0.01, 0.07, 0.003), 1800, 2600);

        Assert.True(verdict.SignificantGain);
        Assert.False(verdict.LatencyOk);
        Assert.False(verdict.Passed);
    }

    [Fact]
    public void Gate_CiTouchingZero_IsNotASignificantGain()
    {
        var verdict = RerankEvaluation.Gate(new PairedComparison("c", "b", "all", 300, 0.02, -0.001, 0.05, 0.04), 900, 900);

        Assert.False(verdict.SignificantGain);
        Assert.Contains("does not pass", RerankEvaluation.Describe(verdict), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackgroundLoad_RunsUntilDisposed_CountsFailures()
    {
        var calls = 0;
        var load = BackgroundLoad.Start(_ => Interlocked.Increment(ref calls) % 2 == 0 ? throw new HttpRequestException("busy") : Task.CompletedTask);
        while (Volatile.Read(ref calls) < 6)
        {
            await Task.Delay(10);
        }

        await load.DisposeAsync();
        var after = Volatile.Read(ref calls);
        await Task.Delay(50);

        Assert.Equal(after, Volatile.Read(ref calls));
        Assert.True(load.Completed > 0);
        Assert.True(load.Failed > 0);
    }
}
````

- [ ] **Step 2: Run it and confirm it fails**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.RerankEvaluationTests"
```

Expected: FAIL — the build fails: `Cortex.Contained.Agent.Host.Reranking.Evaluation` does not exist yet.

- [ ] **Step 3: Implement**

**Create** `src/Cortex.Contained.Agent.Host/Reranking/Evaluation/RerankEvaluation.cs`:

````csharp
using System.Globalization;
using System.Text;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Reranking.Evaluation;

/// <summary>The §4.3 latency/quality grid: TopN ∈ {10, 15, 30} × MaxDocChars ∈ {600, 1200, 2000}.</summary>
public static class RerankGrid
{
    /// <summary>Candidate counts of the curve.</summary>
    public static IReadOnlyList<int> TopNs { get; } = [10, 15, 30];

    /// <summary>Characters per candidate of the curve.</summary>
    public static IReadOnlyList<int> MaxDocChars { get; } = [600, 1200, 2000];

    /// <summary>Every cell of the grid, smallest first.</summary>
    public static IReadOnlyList<RerankBudget> Cells() =>
        [.. TopNs.SelectMany(n => MaxDocChars.Select(c => new RerankBudget(n, c)))];
}

/// <summary>Measurement conditions of the curve (§4.3).</summary>
public static class RerankConditions
{
    /// <summary>Warm model, idle embedder.</summary>
    public const string Normal = "normal";

    /// <summary>The embeddings sidecar is kept busy with document batches while measuring.</summary>
    public const string EmbedderBusy = "embedder-busy";

    /// <summary>Measured right after the reranker (re)started: readiness time and first-call latency are reported.</summary>
    public const string Cold = "cold";

    /// <summary>True for a known condition.</summary>
    public static bool IsValid(string condition) => condition is Normal or EmbedderBusy or Cold;
}

/// <summary>One query of one configuration: its metrics and the rerank stage's outcome (null when not reranked).</summary>
public sealed record RerankRun(AblationScore Score, RerankOutcome? Outcome);

/// <summary>One cell of the curve under one condition (numbers only).</summary>
/// <param name="Condition">normal, embedder-busy or cold.</param>
/// <param name="Cell">Budget label (<c>topN-charsC</c>).</param>
/// <param name="Queries">Queries scored.</param>
/// <param name="DeltaNdcg">Mean paired nDCG@10 gain over no reranking.</param>
/// <param name="Low">Bootstrap 95% CI lower bound.</param>
/// <param name="High">Bootstrap 95% CI upper bound.</param>
/// <param name="PValue">Paired randomisation p.</param>
/// <param name="P50RerankMs">Median latency of the rerank call.</param>
/// <param name="P95RerankMs">95th percentile latency of the rerank call.</param>
/// <param name="P95EndToEndMs">95th percentile first-page search latency with reranking.</param>
/// <param name="Applied">Queries the reranker reordered.</param>
/// <param name="Failed">Queries whose rerank failed or timed out (fused order kept).</param>
/// <param name="Skipped">Queries not sent (fewer than two candidates, not hybrid).</param>
public sealed record RerankCurveRow(
    string Condition, string Cell, int Queries, double DeltaNdcg, double Low, double High, double? PValue,
    double P50RerankMs, double P95RerankMs, double P95EndToEndMs, int Applied, int Failed, int Skipped);

/// <summary>Gate R (§4 roadmap row 5): a significant paired gain and p95 ≤ 2.5 s, also with the embedder busy.</summary>
public sealed record RerankGateVerdict(
    string Config, string Baseline, int Queries, double Mean, double Low, double High, double? PValue,
    double P95NormalMs, double P95EmbedderBusyMs, bool SignificantGain, bool LatencyOk, bool Passed);

/// <summary>Builds the latency/quality curve and the Gate R verdict from per-query runs.</summary>
public static class RerankEvaluation
{
    /// <summary>Name of the configuration without reranking in curve reports.</summary>
    public const string NoRerank = "no-rerank";

    /// <summary>Gate R latency bound on the first-page p95.</summary>
    public const double MaxP95Ms = 2500;

    /// <summary>Gate R significance level of the paired randomisation test.</summary>
    public const double Alpha = 0.05;

    /// <summary>
    /// Runs every query without reranking and with each budget of <paramref name="cells"/> under one
    /// condition. <paramref name="run"/> gets the configuration name to score under and the budget (null = off).
    /// </summary>
    public static async Task<(IReadOnlyList<RerankCurveRow> Rows, AblationReport Report)> CurveAsync<TQuery>(
        IReadOnlyList<TQuery> queries,
        IReadOnlyList<RerankBudget> cells,
        string condition,
        Func<string, RerankBudget?, TQuery, CancellationToken, Task<RerankRun>> run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(run);
        var scores = new List<AblationScore>();
        var outcomes = new Dictionary<string, List<RerankRun>>(StringComparer.Ordinal);
        foreach (var (name, budget) in new[] { (NoRerank, (RerankBudget?)null) }.Concat(cells.Select(c => (c.Label, (RerankBudget?)c))))
        {
            var runs = outcomes[name] = [];
            foreach (var query in queries)
            {
                var result = await run(name, budget, query, cancellationToken).ConfigureAwait(false);
                runs.Add(result);
                scores.Add(result.Score);
            }
        }

        var report = AblationReporting.Build(scores, NoRerank);
        var rows = cells.Select(cell =>
        {
            var runs = outcomes[cell.Label];
            var overall = report.Comparisons.SingleOrDefault(c => c.Config == cell.Label && c.Slice == "all");
            var called = runs.Where(r => r.Outcome is { Status: not RerankStatus.Skipped }).Select(r => r.Outcome!.LatencyMs).ToList();
            return new RerankCurveRow(
                condition,
                cell.Label,
                runs.Count,
                overall?.Mean ?? 0,
                overall?.Low ?? 0,
                overall?.High ?? 0,
                overall?.PValue,
                called.Count == 0 ? 0 : EvalStatistics.Percentile(called, 50),
                called.Count == 0 ? 0 : EvalStatistics.Percentile(called, 95),
                runs.Count == 0 ? 0 : EvalStatistics.Percentile(runs.Select(r => r.Score.LatencyMs).ToList(), 95),
                runs.Count(r => r.Outcome?.Status == RerankStatus.Applied),
                runs.Count(r => r.Outcome?.Status == RerankStatus.Failed),
                runs.Count(r => r.Outcome is null || r.Outcome.Status == RerankStatus.Skipped));
        }).ToList();
        return (rows, report);
    }

    /// <summary>Gate R from the paired comparison on the test split and the p95 under both conditions.</summary>
    public static RerankGateVerdict Gate(PairedComparison overall, double p95NormalMs, double p95EmbedderBusyMs)
    {
        ArgumentNullException.ThrowIfNull(overall);
        var significant = overall.Low > 0 && overall.PValue is < Alpha;
        var latencyOk = Math.Max(p95NormalMs, p95EmbedderBusyMs) <= MaxP95Ms;
        return new RerankGateVerdict(
            overall.Config, overall.Baseline, overall.Queries, overall.Mean, overall.Low, overall.High, overall.PValue,
            p95NormalMs, p95EmbedderBusyMs, significant, latencyOk, significant && latencyOk);
    }

    /// <summary>A Markdown table of curve rows (numbers only).</summary>
    public static string Markdown(IReadOnlyList<RerankCurveRow> rows, string title, string? coldNote = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# {title}");
        text.AppendLine();
        if (coldNote is not null)
        {
            text.AppendLine(coldNote);
            text.AppendLine();
        }

        text.AppendLine("| condition | cell | n | Δ nDCG@10 | 95% CI | p | rerank p50 ms | rerank p95 ms | first page p95 ms | applied | failed | skipped |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Condition} | {r.Cell} | {r.Queries} | {r.DeltaNdcg:+0.000;-0.000;0.000} | [{r.Low:+0.000;-0.000;0.000}, {r.High:+0.000;-0.000;0.000}] | {r.PValue?.ToString("F4", CultureInfo.InvariantCulture) ?? "–"} | {r.P50RerankMs:F0} | {r.P95RerankMs:F0} | {r.P95EndToEndMs:F0} | {r.Applied} | {r.Failed} | {r.Skipped} |");
        }

        text.AppendLine();
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Gate R needs a significant paired gain (CI above 0, p < {Alpha}) and first-page p95 ≤ {MaxP95Ms:F0} ms, also with the embedder busy; it is the owner's decision."));
        return text.ToString();
    }

    /// <summary>The verdict as one line.</summary>
    public static string Describe(RerankGateVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return string.Create(CultureInfo.InvariantCulture,
            $"Gate R {verdict.Config} vs {verdict.Baseline} ({verdict.Queries} queries): Δ nDCG@10 {verdict.Mean:+0.000;-0.000} [{verdict.Low:+0.000;-0.000}, {verdict.High:+0.000;-0.000}], p={verdict.PValue?.ToString("F4", CultureInfo.InvariantCulture) ?? "–"} → significant gain: {(verdict.SignificantGain ? "yes" : "no")}; first-page p95 {verdict.P95NormalMs:F0} ms (embedder busy {verdict.P95EmbedderBusyMs:F0} ms) ≤ {MaxP95Ms:F0} ms: {(verdict.LatencyOk ? "yes" : "no")}. Gate R {(verdict.Passed ? "PASSES" : "does not pass")}; enabling the reranker is the owner's decision.");
    }
}

/// <summary>Keeps a worker loop running while measuring (the "embedder busy" condition). Failures are counted, never thrown.</summary>
public sealed class BackgroundLoad : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task[] workers;
    private int completed;
    private int failed;

    private BackgroundLoad(Func<CancellationToken, Task> work, int concurrency)
    {
        this.workers = [.. Enumerable.Range(0, concurrency).Select(_ => Task.Run(() => this.LoopAsync(work)))];
    }

    /// <summary>Calls that completed.</summary>
    public int Completed => Volatile.Read(ref this.completed);

    /// <summary>Calls that failed.</summary>
    public int Failed => Volatile.Read(ref this.failed);

    /// <summary>Starts <paramref name="concurrency"/> loops of <paramref name="work"/>.</summary>
    public static BackgroundLoad Start(Func<CancellationToken, Task> work, int concurrency = 2)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        return new BackgroundLoad(work, concurrency);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(this.workers).ConfigureAwait(false);
        this.stop.Dispose();
    }

    private async Task LoopAsync(Func<CancellationToken, Task> work)
    {
        while (!this.stop.IsCancellationRequested)
        {
            try
            {
                await work(this.stop.Token).ConfigureAwait(false);
                Interlocked.Increment(ref this.completed);

                // A short breath between calls keeps a fast (fake or cached) worker from spinning a core.
                await Task.Delay(5, this.stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (this.stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref this.failed);
            }
        }
    }
}

/// <summary>
/// The live reranker for the evaluation and smoke CLIs (run with <c>docker exec</c>, so the Bridge-held
/// key is read from <c>Reranker__ApiKey</c>; see <c>docs/reranker.md</c>). The stage uses a generous
/// timeout by default so the curve sees true latency, and a breaker that never opens.
/// </summary>
public static class RerankEvalSetup
{
    /// <summary>Default per-call timeout of curve runs (the true latency is the measurement).</summary>
    public const int CurveTimeoutMs = 30_000;

    /// <summary>Reranker options from the environment (<c>Reranker__*</c>) with an optional endpoint override.</summary>
    public static RerankerOptions Options(string? endpoint, int timeoutMs)
    {
        var options = new RerankerOptions
        {
            Endpoint = endpoint ?? Environment.GetEnvironmentVariable("Reranker__Endpoint") ?? "http://reranker:8080",
            Model = Environment.GetEnvironmentVariable("Reranker__Model") ?? "qwen3-reranker-0.6b",
            TimeoutMs = timeoutMs,
            FailureThreshold = 1_000_000,
        };
        if (Environment.GetEnvironmentVariable("Reranker__AllowedEndpointHosts") is { Length: > 0 } hosts)
        {
            options.AllowedEndpointHosts = hosts;
        }

        return options;
    }

    /// <summary>The live llama-server client with the key from <c>Reranker__ApiKey</c>.</summary>
    public static LlamaServerReranker LiveReranker(RerankerOptions options) => LiveReranker(options, SharedClient);

    /// <summary>The llama-server client over <paramref name="client"/> with the key from <c>Reranker__ApiKey</c>.</summary>
    public static LlamaServerReranker LiveReranker(RerankerOptions options, HttpClient client) =>
        new(new SingleClientFactory(client), new FixedOptions(options), () => Environment.GetEnvironmentVariable("Reranker__ApiKey"));

    /// <summary>A rerank stage over <paramref name="reranker"/> for evaluation runs.</summary>
    public static RerankStage Stage(IReranker reranker, RerankerOptions options)
    {
        var access = new RerankerAccess();
        access.Apply(new RerankerGrant { Enabled = true, ApiKey = "evaluation" });
        return new RerankStage(reranker, new FixedOptions(options), access, NullLogger<RerankStage>.Instance);
    }

    /// <summary>Parses a positive integer option, or returns <paramref name="fallback"/>.</summary>
    public static int IntOption(string? text, int fallback) =>
        text is not null && int.TryParse(text, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

    // One client for the whole command: the reranker never disposes what the factory returns.
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FixedOptions(RerankerOptions value) : IOptionsMonitor<RerankerOptions>
    {
        public RerankerOptions CurrentValue => value;

        public RerankerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<RerankerOptions, string?> listener) => null;
    }
}
````

**Modify** `src/Cortex.Contained.Agent.Host/Reranking/LlamaServerReranker.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/Reranking/LlamaServerReranker.cs b/src/Cortex.Contained.Agent.Host/Reranking/LlamaServerReranker.cs
index 738905a..78eb438 100644
--- a/src/Cortex.Contained.Agent.Host/Reranking/LlamaServerReranker.cs
+++ b/src/Cortex.Contained.Agent.Host/Reranking/LlamaServerReranker.cs
@@ -106,6 +106,25 @@ public sealed class LlamaServerReranker : IReranker
         }
     }
 
+    /// <summary>
+    /// <c>GET /health</c> (public in llama-server, no key sent): true once the model is loaded. Never throws
+    /// for transport errors.
+    /// </summary>
+    public async Task<bool> ProbeHealthAsync(CancellationToken cancellationToken)
+    {
+        var settings = this.options.CurrentValue;
+        try
+        {
+            using var response = await this.httpClientFactory.CreateClient(HttpClientName)
+                .GetAsync(new Uri(settings.Endpoint.TrimEnd('/') + "/health"), cancellationToken).ConfigureAwait(false);
+            return response.IsSuccessStatusCode;
+        }
+        catch (HttpRequestException)
+        {
+            return false;
+        }
+    }
+
     private static List<RerankScore> Validate(RerankResponse? body, int count)
     {
         if (body?.Results is not { } results)
````

- [ ] **Step 4: Run the tests and confirm they pass**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.RerankEvaluationTests"
```

Expected: PASS — `Passed: 6`.

- [ ] **Step 5: Commit** (never stage `version.json`)

```powershell
git add src/Cortex.Contained.Agent.Host/Reranking/Evaluation/RerankEvaluation.cs src/Cortex.Contained.Agent.Host/Reranking/LlamaServerReranker.cs tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankEvaluationTests.cs
git commit -m "feat(reranker-eval): latency/quality grid, Gate R verdict and embedder-busy load" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 12: `history-eval rerank-curve` and `gate-r`

**Model:** standard

I5-14 for history. `AblationConfig` gains `RerankTopN` (0 = off) and `RerankMaxDocChars`; `EvalCorpus` takes an optional stage, passes `RerankRequest.With(budget)`/`Off`, returns the first page's outcome in `RetrievalRun.Rerank`, and **fails loudly** when a reranked configuration runs without a reranker (so `ablate`/`holdout` never score a reranked name without reranking). `HistoryRerankEval` implements `rerank-curve` (base = `--base`, the holdout choice or `hybrid-rrf60`; conditions; curve.md/json) and `gate-r` (registered reranked config vs the same config without reranking on the test split, then latency with the embedder busy); `HistoryEvalCli` gets two `case`s and an optional reranker factory.

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/History/Evaluation/AblationGrid.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/Evaluation/AblationRunner.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs`
- Create: `src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryRerankEval.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Reranking/HistoryRerankEvalTests.cs`

- [ ] **Step 1: Write the failing test**

**Create** `tests/Cortex.Contained.Agent.Host.Tests/Reranking/HistoryRerankEvalTests.cs`:

````csharp
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tests.History;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary><c>history-eval rerank-curve</c> and <c>gate-r</c> on a synthetic corpus with fake embedder and reranker.</summary>
public sealed class HistoryRerankEvalTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"history-rerank-eval-{Guid.NewGuid():N}");
    private readonly string workspace;
    private readonly string source;
    private readonly StringWriter output = new();
    private readonly FakeReranker reranker = new();

    public HistoryRerankEvalTests()
    {
        this.workspace = Path.Combine(this.root, "eval");
        this.source = Path.Combine(this.root, "messages.db");
    }

    public ValueTask DisposeAsync()
    {
        this.output.Dispose();
        try { Directory.Delete(this.root, recursive: true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    private Task<int> RunAsync(params string[] args) =>
        HistoryEvalCli.RunAsync(
            [.. args, "--dir", this.workspace, "--source", this.source, "--tz", "UTC"],
            this.output,
            _ => new FakeHistoryEmbedder(new Dictionary<string, string> { ["train"] = "railway", ["trip"] = "journey" }),
            CancellationToken.None,
            new HistoryOptions { EvalArtifactsDirectory = this.workspace },
            _ => this.reranker);

    private async Task SeedAsync()
    {
        Directory.CreateDirectory(this.root);
        long railway, invoice;
        await using (var store = new MessageStore(this.source, NullLogger<MessageStore>.Instance))
        {
            railway = await store.SaveMessageAsync("u", "ch", "user", "we booked the railway journey to TownB", T0);
            await store.SaveMessageAsync("u", "ch", "user", "the railway strike moved the journey", T0.AddHours(3));
            invoice = await store.SaveMessageAsync("u", "ch", "user", "invoice INV2041 is paid", T0.AddHours(6));
        }

        Assert.Equal(0, await this.RunAsync("init"));
        File.WriteAllLines(Path.Combine(this.workspace, "dataset.jsonl"), Enumerable.Range(1, 8).Select(i => EvalDataset.ToLine(i % 2 == 0
            ? new EvalQuery { Id = $"e{i:000}", Text = "train trip", Category = "paraphrase", Judgments = [new EvalJudgment(railway, 2)] }
            : new EvalQuery { Id = $"e{i:000}", Text = "invoice paid", Category = "identifier", Judgments = [new EvalJudgment(invoice, 2)] })));
    }

    private IEnumerable<string> RunFiles(string name) =>
        Directory.EnumerateFiles(Path.Combine(this.workspace, "runs"), name, SearchOption.AllDirectories);

    [Fact]
    public async Task RerankCurve_WritesTheNineCellsForTheCondition_NumbersOnly()
    {
        await this.SeedAsync();

        Assert.Equal(0, await this.RunAsync("rerank-curve", "--split", "all", "--condition", "embedder-busy"));

        var curve = await File.ReadAllTextAsync(Assert.Single(this.RunFiles("curve.md")));
        Assert.Equal(9, curve.Split('\n').Count(l => l.StartsWith("| embedder-busy | top", StringComparison.Ordinal)));
        Assert.DoesNotContain("TownB", curve, StringComparison.Ordinal);
        Assert.NotEmpty(this.reranker.Calls);
    }

    [Fact]
    public async Task RerankCurve_UnknownCondition_Refuses()
    {
        await this.SeedAsync();

        Assert.Equal(2, await this.RunAsync("rerank-curve", "--condition", "sunny"));
        Assert.Contains("--condition", this.output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GateR_Unregistered_Refuses()
    {
        await this.SeedAsync();

        Assert.Equal(2, await this.RunAsync("gate-r", "--config", "hybrid-rerank"));
        Assert.Contains("register", this.output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GateR_Registered_ComparesWithTheSameConfigWithoutReranking()
    {
        await this.SeedAsync();
        var ws = new EvalWorkspace(this.workspace);
        ws.WritePlan(ws.ReadPlan() with
        {
            Configs = [.. ws.ReadPlan().Configs, new AblationConfig { Name = "hybrid-rerank", RerankTopN = 10 }],
            Holdout = new HoldoutPlan { Config = "hybrid-rerank" },
        });
        Assert.Equal(0, await this.RunAsync("register"));

        Assert.Equal(0, await this.RunAsync("gate-r", "--config", "hybrid-rerank"));

        Assert.Contains("Gate R hybrid-rerank vs hybrid-rerank-no-rerank", this.output.ToString(), StringComparison.Ordinal);
        Assert.Single(this.RunFiles("verdict.json"));
    }

    [Fact]
    public async Task Ablate_RerankConfigWithoutAReranker_FailsLoudly()
    {
        await this.SeedAsync();
        var ws = new EvalWorkspace(this.workspace);
        ws.WritePlan(new AblationPlan { Baseline = "hybrid-rrf60", Configs = [new AblationConfig { Name = "hybrid-rrf60" }, new AblationConfig { Name = "hybrid-rerank", RerankTopN = 10 }] });

        Assert.Equal(2, await this.RunAsync("ablate", "--split", "all"));
        Assert.Contains("rerank-curve", this.output.ToString(), StringComparison.Ordinal);
    }
}
````

- [ ] **Step 2: Run it and confirm it fails**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.HistoryRerankEvalTests|FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.History.HistoryEvalCliTests"
```

Expected: FAIL — the build fails: `AblationConfig.RerankTopN` and the `HistoryEvalCli.RunAsync` reranker factory parameter do not exist yet.

- [ ] **Step 3: Implement**

**Modify** `src/Cortex.Contained.Agent.Host/History/Evaluation/AblationGrid.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/History/Evaluation/AblationGrid.cs b/src/Cortex.Contained.Agent.Host/History/Evaluation/AblationGrid.cs
index 57163e2..a800659 100644
--- a/src/Cortex.Contained.Agent.Host/History/Evaluation/AblationGrid.cs
+++ b/src/Cortex.Contained.Agent.Host/History/Evaluation/AblationGrid.cs
@@ -1,4 +1,5 @@
 using System.Text.Json;
+using Cortex.Contained.Agent.Host.Reranking;
 
 namespace Cortex.Contained.Agent.Host.History.Evaluation;
 
@@ -38,6 +39,15 @@ public sealed record AblationConfig
     /// <summary>Use the model-written query and filters instead of the oracle text and gold filters.</summary>
     public bool UseModelQuery { get; init; }
 
+    /// <summary>Fused candidates reranked (§4.3); 0 = no reranking. Evaluated with <c>rerank-curve</c> and <c>gate-r</c>.</summary>
+    public int RerankTopN { get; init; }
+
+    /// <summary>Characters of raw best-matching lines per reranked candidate.</summary>
+    public int RerankMaxDocChars { get; init; } = 1200;
+
+    /// <summary>The rerank budget of this configuration, or null when it does not rerank.</summary>
+    public RerankBudget? RerankBudget() => this.RerankTopN > 0 ? new RerankBudget(this.RerankTopN, this.RerankMaxDocChars) : null;
+
     /// <summary>A copy of <paramref name="baseline"/> with this configuration applied.</summary>
     public HistoryOptions Apply(HistoryOptions baseline)
     {
````

**Modify** `src/Cortex.Contained.Agent.Host/History/Evaluation/AblationRunner.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/History/Evaluation/AblationRunner.cs b/src/Cortex.Contained.Agent.Host/History/Evaluation/AblationRunner.cs
index 966e204..1f1fbd3 100644
--- a/src/Cortex.Contained.Agent.Host/History/Evaluation/AblationRunner.cs
+++ b/src/Cortex.Contained.Agent.Host/History/Evaluation/AblationRunner.cs
@@ -1,10 +1,16 @@
+using Cortex.Contained.Agent.Host.Reranking;
+
 namespace Cortex.Contained.Agent.Host.History.Evaluation;
 
 /// <summary>What one system returned for one query.</summary>
 /// <param name="Hits">The hits in reading order.</param>
 /// <param name="Confidence">Confidence that an answer exists (the best dense similarity; 0 for keyword-only).</param>
 /// <param name="LatencyMs">First-page latency in milliseconds.</param>
-public sealed record RetrievalRun(IReadOnlyList<ReturnedHit> Hits, double Confidence, double LatencyMs);
+public sealed record RetrievalRun(IReadOnlyList<ReturnedHit> Hits, double Confidence, double LatencyMs)
+{
+    /// <summary>The rerank stage's outcome on the first page, or null when the configuration does not rerank.</summary>
+    public RerankOutcome? Rerank { get; init; }
+}
 
 /// <summary>
 /// Runs every configuration on every query, scores message-level metrics and compares each
````

**Modify** `src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs b/src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs
index eb7caad..2097c62 100644
--- a/src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs
+++ b/src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs
@@ -1,4 +1,5 @@
 using System.Diagnostics;
+using Cortex.Contained.Agent.Host.Reranking;
 using Cortex.Contained.Agent.Host.Storage;
 using Microsoft.Data.Sqlite;
 using Microsoft.Extensions.Logging.Abstractions;
@@ -21,18 +22,21 @@ public sealed class EvalCorpus : IAsyncDisposable
 
     private readonly MessageStore store;
     private readonly MutableOptionsMonitor monitor;
+    private readonly IRerankStage? reranker;
     private readonly Dictionary<HistoryKeywordVariant, (HistoryProjector Projector, HistoryRecallService Recall)> variants = [];
 
-    private EvalCorpus(MessageStore store, MutableOptionsMonitor monitor)
+    private EvalCorpus(MessageStore store, MutableOptionsMonitor monitor, IRerankStage? reranker)
     {
         this.store = store;
         this.monitor = monitor;
+        this.reranker = reranker;
     }
 
     /// <summary>
     /// Copies <paramref name="sourceDb"/> and builds an embedded generation for every keyword variant in
     /// <paramref name="configs"/>. <paramref name="embedderFactory"/> receives the options monitor the
     /// corpus switches per configuration (so the query-instruction ablation reaches the embedder).
+    /// <paramref name="reranker"/> serves configurations with a rerank budget (§4.3).
     /// </summary>
     public static async Task<EvalCorpus> CreateAsync(
         string sourceDb,
@@ -40,7 +44,8 @@ public sealed class EvalCorpus : IAsyncDisposable
         HistoryOptions baseline,
         IReadOnlyList<AblationConfig> configs,
         Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory,
-        CancellationToken cancellationToken)
+        CancellationToken cancellationToken,
+        IRerankStage? reranker = null)
     {
         ArgumentNullException.ThrowIfNull(baseline);
         ArgumentNullException.ThrowIfNull(configs);
@@ -60,7 +65,7 @@ public sealed class EvalCorpus : IAsyncDisposable
 
         var monitor = new MutableOptionsMonitor(baseline);
         var embedder = embedderFactory(monitor);
-        var corpus = new EvalCorpus(new MessageStore(copyPath, NullLogger<MessageStore>.Instance), monitor);
+        var corpus = new EvalCorpus(new MessageStore(copyPath, NullLogger<MessageStore>.Instance), monitor, reranker);
         foreach (var variant in configs.Select(c => c.KeywordVariant).Distinct())
         {
             monitor.CurrentValue = new AblationConfig { Name = "build", KeywordVariant = variant }.Apply(baseline);
@@ -77,7 +82,7 @@ public sealed class EvalCorpus : IAsyncDisposable
             {
             }
 
-            corpus.variants[variant] = (projector, new HistoryRecallService(corpus.store, projector, gate, status, monitor, embedder, clock));
+            corpus.variants[variant] = (projector, new HistoryRecallService(corpus.store, projector, gate, status, monitor, embedder, clock, reranker));
         }
 
         return corpus;
@@ -90,9 +95,16 @@ public sealed class EvalCorpus : IAsyncDisposable
         ArgumentNullException.ThrowIfNull(query);
         var (_, recall) = this.variants[config.KeywordVariant];
         this.monitor.CurrentValue = config.Apply(this.monitor.Baseline);
+        var rerank = config.RerankBudget();
+        if (rerank is not null && this.reranker is null)
+        {
+            throw new InvalidOperationException($"Configuration '{config.Name}' reranks, but this run has no reranker: evaluate rerank configurations with 'rerank-curve' or 'gate-r'.");
+        }
+
         var model = config.UseModelQuery ? query.ModelQuery : null;
         var filters = model is not null ? model.Filters : query.GoldFilters;
         var hits = new List<ReturnedHit>();
+        RerankOutcome? rerankOutcome = null;
         var words = 0;
         string? cursor = null;
         double confidence = 0;
@@ -110,12 +122,14 @@ public sealed class EvalCorpus : IAsyncDisposable
                     Mode = config.Mode,
                     Limit = PageLimit,
                     Cursor = cursor,
+                    Rerank = rerank is null ? RerankRequest.Off : RerankRequest.With(rerank),
                 },
                 cancellationToken).ConfigureAwait(false);
             if (cursor is null)
             {
                 latency = watch.Elapsed.TotalMilliseconds;
                 confidence = result.TopSimilarity ?? 0;
+                rerankOutcome = result.RerankOutcome;
                 if (config.Mode != HistorySearchMode.Keyword && result.Error is null && result.Mode != HistorySearchMode.Hybrid)
                 {
                     // A degraded run would score a different configuration than the one named in the report.
@@ -140,7 +154,7 @@ public sealed class EvalCorpus : IAsyncDisposable
         }
         while (cursor is not null && words < MaxWords);
 
-        return new RetrievalRun(hits, confidence, latency);
+        return new RetrievalRun(hits, confidence, latency) { Rerank = rerankOutcome };
     }
 
     /// <summary>Texts of the given rows (paraphrase guard only; never written out).</summary>
````

**Modify** `src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs b/src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs
index dc8a33a..ff04867 100644
--- a/src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs
+++ b/src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs
@@ -1,4 +1,6 @@
 using System.Globalization;
+using Cortex.Contained.Agent.Host.Reranking;
+using Cortex.Contained.Agent.Host.Reranking.Evaluation;
 using MemoryMcp.Core.Configuration;
 using MemoryMcp.Core.Services;
 using Microsoft.Extensions.Logging.Abstractions;
@@ -9,14 +11,16 @@ namespace Cortex.Contained.Agent.Host.History.Evaluation;
 /// <summary>
 /// Evaluation-protocol CLI for hybrid history search (§4.2), run inside the agent container on a copy
 /// of the raw <c>messages.db</c> (D6): <c>dotnet Cortex.Contained.Agent.Host.dll history-eval
-/// &lt;init|validate|guard|register|ablate|holdout|kappa&gt; [--dir D] [--source DB] [--tz ZONE] [--split dev|test|all] [--embedder URL]</c>.
+/// &lt;init|validate|guard|register|ablate|holdout|kappa|rerank-curve|gate-r&gt; [--dir D] [--source DB] [--tz ZONE] [--split dev|test|all] [--embedder URL]</c>.
 /// Outputs hold ids and numbers only and stay under the workspace. The Gate H decision is the owner's.
 /// </summary>
 public static class HistoryEvalCli
 {
     private const string Usage =
-        "usage: history-eval <init|validate|guard|register|ablate|holdout|kappa> [--dir /app/state/eval/hybrid] [--source /app/state/messages.db] " +
-        "[--tz Europe/Copenhagen] [--split dev|test|all] [--embedder http://embeddings:11434] [--labels judge-agreement.csv]";
+        "usage: history-eval <init|validate|guard|register|ablate|holdout|kappa|rerank-curve|gate-r> [--dir /app/state/eval/hybrid] [--source /app/state/messages.db] " +
+        "[--tz Europe/Copenhagen] [--split dev|test|all] [--embedder http://embeddings:11434] [--labels judge-agreement.csv]\n" +
+        "rerank-curve [--condition normal|embedder-busy|cold] [--base CONFIG] [--reranker http://reranker:8080] [--timeout-ms 30000]; " +
+        "gate-r --config CONFIG [--timeout-ms 2000]; both read the reranker key from Reranker__ApiKey.";
 
     /// <summary>Minimum LLM-vs-human agreement for LLM relevance labels (§4.2).</summary>
     private const double MinKappa = 0.6;
@@ -64,7 +68,8 @@ public static class HistoryEvalCli
         TextWriter output,
         Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory,
         CancellationToken cancellationToken,
-        HistoryOptions? configured = null)
+        HistoryOptions? configured = null,
+        Func<RerankerOptions, IReranker>? rerankerFactory = null)
     {
         ArgumentNullException.ThrowIfNull(args);
         ArgumentNullException.ThrowIfNull(output);
@@ -75,6 +80,7 @@ public static class HistoryEvalCli
         }
 
         configured ??= HistoryCliConfiguration.Load();
+        rerankerFactory ??= RerankEvalSetup.LiveReranker;
         var workspace = new EvalWorkspace(Option(args, "--dir") ?? configured.EvalArtifactsDirectory);
         var source = Option(args, "--source") ?? "/app/state/messages.db";
         var baseline = new HistoryOptions { Enabled = true, TimeZone = Option(args, "--tz") ?? "Europe/Copenhagen" };
@@ -109,6 +115,10 @@ public static class HistoryEvalCli
                     return await HoldoutAsync(workspace, source, baseline, embedderFactory, output, cancellationToken).ConfigureAwait(false);
                 case "kappa":
                     return await KappaAsync(Option(args, "--labels") ?? Path.Combine(workspace.Directory, "judge-agreement.csv"), output).ConfigureAwait(false);
+                case "rerank-curve":
+                    return await HistoryRerankEval.CurveAsync(workspace, source, baseline, embedderFactory, rerankerFactory, args, output, cancellationToken).ConfigureAwait(false);
+                case "gate-r":
+                    return await HistoryRerankEval.GateAsync(workspace, source, baseline, embedderFactory, rerankerFactory, args, output, cancellationToken).ConfigureAwait(false);
                 default:
                     await output.WriteLineAsync(Usage).ConfigureAwait(false);
                     return 2;
````

**Create** `src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryRerankEval.cs`:

````csharp
using System.Diagnostics;
using System.Globalization;
using Cortex.Contained.Agent.Host.History.Evaluation.Smoke;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History.Evaluation;

/// <summary>
/// The reranker evaluation of <c>history-eval</c> (§4.3, Gate R): <c>rerank-curve</c> measures quality
/// and latency over the TopN × MaxDocChars grid under one condition (normal, embedder busy, cold model);
/// <c>gate-r</c> runs the pre-registered reranked configuration against the same configuration without
/// reranking on the test split. Outputs hold ids and numbers only; the decision is the owner's.
/// </summary>
internal static class HistoryRerankEval
{
    /// <summary>The hybrid configuration the curve reranks when neither <c>--base</c> nor a holdout choice is given.</summary>
    public const string DefaultBase = "hybrid-rrf60";

    private const int ColdReadyTimeoutSeconds = 180;

    /// <summary><c>rerank-curve [--split dev] [--condition normal|embedder-busy|cold] [--base CONFIG] [--reranker URL] [--timeout-ms N]</c>.</summary>
    public static async Task<int> CurveAsync(
        EvalWorkspace workspace,
        string source,
        HistoryOptions baseline,
        Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory,
        Func<RerankerOptions, IReranker> rerankerFactory,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var condition = Option(args, "--condition") ?? RerankConditions.Normal;
        if (!RerankConditions.IsValid(condition))
        {
            throw new InvalidOperationException($"--condition must be {RerankConditions.Normal}, {RerankConditions.EmbedderBusy} or {RerankConditions.Cold}.");
        }

        var split = Option(args, "--split") ?? EvalSplits.Dev;
        var plan = workspace.ReadPlan();
        var baseName = Option(args, "--base") ?? plan.Holdout?.Config ?? DefaultBase;
        var baseConfig = plan.Configs.FirstOrDefault(c => c.Name == baseName && c.Mode != HistorySearchMode.Keyword && c.RerankBudget() is null)
            ?? throw new InvalidOperationException($"ablation.json has no hybrid configuration '{baseName}' without reranking to use as the curve's base (--base).");
        var queries = ReadQueries(workspace, split);
        var (reranker, stage) = CreateStage(rerankerFactory, Option(args, "--reranker"), RerankEvalSetup.IntOption(Option(args, "--timeout-ms"), RerankEvalSetup.CurveTimeoutMs));

        var runDir = NewRunDirectory(workspace, $"rerank-curve-{condition}-{split}");
        await using var corpus = await EvalCorpus.CreateAsync(source, runDir, baseline, [baseConfig], embedderFactory, cancellationToken, stage).ConfigureAwait(false);
        string? coldNote = null;
        double? readyMs = null;
        if (condition == RerankConditions.Cold)
        {
            readyMs = await WaitReadyAsync(reranker, cancellationToken).ConfigureAwait(false);
        }

        double? firstCallMs = null;
        await using var load = condition == RerankConditions.EmbedderBusy ? StartEmbedderLoad(embedderFactory, baseline) : null;
        var (rows, report) = await RerankEvaluation.CurveAsync(
            queries,
            RerankGrid.Cells(),
            condition,
            async (name, budget, query, ct) =>
            {
                var config = baseConfig with { Name = name, RerankTopN = budget?.TopN ?? 0, RerankMaxDocChars = budget?.MaxDocChars ?? baseConfig.RerankMaxDocChars };
                var run = await corpus.SearchAsync(config, query, ct).ConfigureAwait(false);
                firstCallMs ??= run.Rerank is { Status: not RerankStatus.Skipped } called ? called.LatencyMs : null;
                return new RerankRun(AblationRunner.Score(name, query, run), run.Rerank);
            },
            cancellationToken).ConfigureAwait(false);

        if (condition == RerankConditions.Cold)
        {
            coldNote = string.Create(CultureInfo.InvariantCulture,
                $"Cold model: the reranker answered /health after {readyMs:F0} ms; the first rerank call took {firstCallMs ?? 0:F0} ms (later calls are warm).");
        }

        EvalWorkspace.WriteJson(Path.Combine(runDir, "results.json"), report);
        EvalWorkspace.WriteJson(Path.Combine(runDir, "curve.json"), new { condition, split, Base = baseName, rows, readyMs, firstCallMs, loadCompleted = load?.Completed, loadFailed = load?.Failed });
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "curve.md"),
            RerankEvaluation.Markdown(rows, string.Create(CultureInfo.InvariantCulture, $"history rerank curve: {condition} on {split} ({queries.Count} queries, base {baseName})"), coldNote),
            cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Curve: {Path.Combine(runDir, "curve.md")}").ConfigureAwait(false);
        return 0;
    }

    /// <summary><c>gate-r --config CONFIG [--reranker URL] [--timeout-ms N]</c> on the test split (registration required).</summary>
    public static async Task<int> GateAsync(
        EvalWorkspace workspace,
        string source,
        HistoryOptions baseline,
        Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory,
        Func<RerankerOptions, IReranker> rerankerFactory,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        workspace.VerifyRegistration();
        var plan = workspace.ReadPlan();
        var name = Option(args, "--config") ?? throw new InvalidOperationException("gate-r needs --config: the registered reranked configuration.");
        var candidate = plan.Configs.FirstOrDefault(c => c.Name == name && c.RerankBudget() is not null)
            ?? throw new InvalidOperationException($"ablation.json has no reranked configuration '{name}' (rerankTopN > 0).");
        var unreranked = candidate with { Name = name + "-no-rerank", RerankTopN = 0 };
        var queries = ReadQueries(workspace, EvalSplits.Test);
        var (_, stage) = CreateStage(rerankerFactory, Option(args, "--reranker"), RerankEvalSetup.IntOption(Option(args, "--timeout-ms"), new RerankerOptions().TimeoutMs));

        var runDir = NewRunDirectory(workspace, "gate-r");
        await using var corpus = await EvalCorpus.CreateAsync(source, runDir, baseline, [candidate], embedderFactory, cancellationToken, stage).ConfigureAwait(false);
        var report = await AblationRunner.RunAsync([unreranked, candidate], queries, corpus.SearchAsync, unreranked.Name, cancellationToken).ConfigureAwait(false);
        var busy = new List<double>();
        await using (StartEmbedderLoad(embedderFactory, baseline))
        {
            foreach (var query in queries)
            {
                busy.Add((await corpus.SearchAsync(candidate, query, cancellationToken).ConfigureAwait(false)).LatencyMs);
            }
        }

        var verdict = RerankEvaluation.Gate(
            report.Comparisons.Single(c => c.Config == name && c.Slice == "all"),
            report.Summaries.Single(s => s.Config == name).P95LatencyMs,
            busy.Count == 0 ? 0 : EvalStatistics.Percentile(busy, 95));
        EvalWorkspace.WriteJson(Path.Combine(runDir, "results.json"), report);
        EvalWorkspace.WriteJson(Path.Combine(runDir, "verdict.json"), verdict);
        await File.WriteAllTextAsync(Path.Combine(runDir, "report.md"), AblationRunner.Markdown(report, $"Gate R: {name} on test ({queries.Count} queries)"), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(RerankEvaluation.Describe(verdict)).ConfigureAwait(false);
        return 0;
    }

    private static List<EvalQuery> ReadQueries(EvalWorkspace workspace, string split)
    {
        var all = EvalDataset.Read(workspace.DatasetPath);
        if (EvalDataset.Validate(all) is { Count: > 0 } errors)
        {
            throw new InvalidOperationException($"dataset.jsonl has {errors.Count} problems; run 'validate'.");
        }

        return split == "all" ? [.. all] : [.. all.Where(q => EvalSplits.Assign(q.Id) == split)];
    }

    private static (IReranker Reranker, RerankStage Stage) CreateStage(Func<RerankerOptions, IReranker> factory, string? endpoint, int timeoutMs)
    {
        var options = RerankEvalSetup.Options(endpoint, timeoutMs);
        var reranker = factory(options);
        if (reranker.UnavailableReason is { } reason)
        {
            throw new InvalidOperationException("the reranker cannot be used: " + reason + " (pass the key as Reranker__ApiKey; see docs/reranker.md).");
        }

        return (reranker, RerankEvalSetup.Stage(reranker, options));
    }

    private static async Task<double> WaitReadyAsync(IReranker reranker, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        if (reranker is not LlamaServerReranker live)
        {
            return 0;
        }

        while (watch.Elapsed < TimeSpan.FromSeconds(ColdReadyTimeoutSeconds))
        {
            if (await live.ProbeHealthAsync(cancellationToken).ConfigureAwait(false))
            {
                return watch.Elapsed.TotalMilliseconds;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"the reranker did not become ready within {ColdReadyTimeoutSeconds} s.");
    }

    /// <summary>Keeps the embedder busy with batches of maximum-size synthetic chunks (no history text).</summary>
    private static BackgroundLoad StartEmbedderLoad(Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory, HistoryOptions baseline)
    {
        var embedder = embedderFactory(new FixedOptions(baseline));
        IReadOnlyList<string> batch = [.. Enumerable.Repeat(EmbedderCheck.Synthetic(baseline.Dense.MaxChunkChars), baseline.Dense.EmbedBatchSize)];
        return BackgroundLoad.Start(ct => embedder.EmbedDocumentsAsync(batch, ct));
    }

    private static string NewRunDirectory(EvalWorkspace workspace, string kind) =>
        Path.Combine(workspace.RunsDirectory, string.Create(CultureInfo.InvariantCulture, $"{kind}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}"));

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private sealed class FixedOptions(HistoryOptions value) : IOptionsMonitor<HistoryOptions>
    {
        public HistoryOptions CurrentValue => value;

        public HistoryOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<HistoryOptions, string?> listener) => null;
    }
}
````

- [ ] **Step 4: Run the tests and confirm they pass**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.HistoryRerankEvalTests|FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.History.HistoryEvalCliTests"
```

Expected: PASS — `Passed: 15`.

- [ ] **Step 5: Commit** (never stage `version.json`)

```powershell
git add src/Cortex.Contained.Agent.Host/History/Evaluation/AblationGrid.cs src/Cortex.Contained.Agent.Host/History/Evaluation/AblationRunner.cs src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryRerankEval.cs tests/Cortex.Contained.Agent.Host.Tests/Reranking/HistoryRerankEvalTests.cs
git commit -m "feat(history-eval): rerank latency/quality curve and Gate R commands" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 13: `history-smoke reranker-check`

**Model:** cheap

I5-15. The live check against the real sidecar (synthetic text only): guard and key, readiness via `/health`, a request without the key is refused (401), `/slots` is off, a relevant passage ranks first, and the smallest and largest cells answer within `Reranker:TimeoutMs`. Key from `Reranker__ApiKey`.

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/History/Evaluation/Smoke/HistorySmokeCli.cs`
- Create: `src/Cortex.Contained.Agent.Host/History/Evaluation/Smoke/RerankerCheck.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankerCheckTests.cs`

- [ ] **Step 1: Write the failing test**

**Create** `tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankerCheckTests.cs`:

````csharp
using System.Net;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Agent.Host.History.Evaluation.Smoke;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary><c>history-smoke reranker-check</c> against a stubbed llama-server (no sidecar).</summary>
public sealed class RerankerCheckTests : IDisposable
{
    private readonly StubServer server = new();
    private readonly HttpClient http;
    private readonly RerankerOptions options = new();

    public RerankerCheckTests()
    {
        this.http = new HttpClient(this.server);
        Environment.SetEnvironmentVariable("Reranker__ApiKey", "check-key");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("Reranker__ApiKey", null);
        this.http.Dispose();
        this.server.Dispose();
    }

    private Task<RerankerCheckResult> RunAsync(string? key = "check-key") =>
        RerankerCheck.RunAsync(RerankEvalSetup.LiveReranker(this.options, this.http), this.options, key, this.http, TimeSpan.FromSeconds(5), CancellationToken.None);

    [Fact]
    public async Task HealthySidecar_AllChecksPass()
    {
        var result = await this.RunAsync();

        Assert.True(result.Passed, string.Join('\n', result.Lines));
        Assert.Contains(result.Lines, l => l.StartsWith("PASS a request without the key is refused", StringComparison.Ordinal));
        Assert.Contains(result.Lines, l => l.StartsWith("PASS top30-chars2000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SidecarWithoutAuthentication_Fails()
    {
        this.server.EnforceKey = false;

        var result = await this.RunAsync();

        Assert.False(result.Passed);
        Assert.Contains(result.Lines, l => l.StartsWith("FAIL a request without the key is refused (HTTP 200", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoKey_StopsBeforeSendingAnything()
    {
        var result = await this.RunAsync(key: null);

        Assert.False(result.Passed);
        Assert.Single(result.Lines);
        Assert.Empty(this.server.Paths);
    }

    private sealed class StubServer : HttpMessageHandler
    {
        public bool EnforceKey { get; set; } = true;

        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            this.Paths.Add(path);
            if (path == "/health")
            {
                return Json(HttpStatusCode.OK, """{"status":"ok"}""");
            }

            if (this.EnforceKey && request.Headers.Authorization?.Parameter != "check-key")
            {
                return Json(HttpStatusCode.Unauthorized, """{"error":"Invalid API Key"}""");
            }

            if (path == "/slots")
            {
                return Json(HttpStatusCode.NotImplemented, "{}");
            }

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var documents = body.RootElement.GetProperty("documents").EnumerateArray().Select(d => d.GetString()!).ToList();
            var results = documents.Select((d, i) => $$"""{"index":{{i}},"relevance_score":{{(d.Contains("night train", StringComparison.Ordinal) ? "0.99" : "0.01")}}}""");
            return Json(HttpStatusCode.OK, $$"""{"results":[{{string.Join(',', results)}}]}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
````

- [ ] **Step 2: Run it and confirm it fails**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.RerankerCheckTests"
```

Expected: FAIL — the build fails: `RerankerCheck` does not exist yet.

- [ ] **Step 3: Implement**

**Modify** `src/Cortex.Contained.Agent.Host/History/Evaluation/Smoke/HistorySmokeCli.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/History/Evaluation/Smoke/HistorySmokeCli.cs b/src/Cortex.Contained.Agent.Host/History/Evaluation/Smoke/HistorySmokeCli.cs
index f02c334..82a4b54 100644
--- a/src/Cortex.Contained.Agent.Host/History/Evaluation/Smoke/HistorySmokeCli.cs
+++ b/src/Cortex.Contained.Agent.Host/History/Evaluation/Smoke/HistorySmokeCli.cs
@@ -1,6 +1,8 @@
 using System.Globalization;
 using System.Text;
 using Cortex.Contained.Agent.Host.Llm;
+using Cortex.Contained.Agent.Host.Reranking;
+using Cortex.Contained.Agent.Host.Reranking.Evaluation;
 using Cortex.Contained.Contracts.Hub;
 using Cortex.Contained.Contracts.Llm;
 using Microsoft.Extensions.Logging.Abstractions;
@@ -17,9 +19,10 @@ namespace Cortex.Contained.Agent.Host.History.Evaluation.Smoke;
 public static class HistorySmokeCli
 {
     private const string Usage =
-        "usage: history-smoke <init|generate-queries|pool|register|run|embedder-check> [--dir /app/state/eval/smoke] [--source /app/state/messages.db] [--tz Europe/Copenhagen] [--embedder http://embeddings:11434]\n" +
+        "usage: history-smoke <init|generate-queries|pool|register|run|embedder-check|reranker-check> [--dir /app/state/eval/smoke] [--source /app/state/messages.db] [--tz Europe/Copenhagen] [--embedder http://embeddings:11434] [--reranker http://reranker:8080]\n" +
         "generate-queries needs SMOKE_LLM_ENDPOINT, SMOKE_LLM_API_KEY, SMOKE_LLM_MODEL and optional SMOKE_LLM_API (default openai-completions).\n" +
-        "embedder-check uses MemoryMcp__Ollama__Model / __NumCtx / __Dimensions when set (synthetic text only).";
+        "embedder-check uses MemoryMcp__Ollama__Model / __NumCtx / __Dimensions when set (synthetic text only).\n" +
+        "reranker-check needs Reranker__ApiKey (the Bridge-held key, see docs/reranker.md) and uses Reranker__Model / __TimeoutMs when set (synthetic text only).";
 
     /// <summary>Entry point; returns a process exit code.</summary>
     public static Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken) =>
@@ -83,6 +86,27 @@ public static class HistorySmokeCli
                         return check.Passed ? 0 : 1;
                     }
 
+                case "reranker-check":
+                    var rerankerOptions = RerankEvalSetup.Options(
+                        Option(args, "--reranker"),
+                        RerankEvalSetup.IntOption(Environment.GetEnvironmentVariable("Reranker__TimeoutMs"), new RerankerOptions().TimeoutMs));
+                    using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
+                    {
+                        var rerankerCheck = await RerankerCheck.RunAsync(
+                            RerankEvalSetup.LiveReranker(rerankerOptions, http),
+                            rerankerOptions,
+                            Environment.GetEnvironmentVariable("Reranker__ApiKey"),
+                            http,
+                            TimeSpan.FromSeconds(120),
+                            cancellationToken).ConfigureAwait(false);
+                        foreach (var line in rerankerCheck.Lines)
+                        {
+                            await output.WriteLineAsync(line).ConfigureAwait(false);
+                        }
+
+                        return rerankerCheck.Passed ? 0 : 1;
+                    }
+
                 case "run":
                     var (runDir, gate) = await RunEvaluationAsync(workspace, source, options, cancellationToken).ConfigureAwait(false);
                     await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
````

**Create** `src/Cortex.Contained.Agent.Host/History/Evaluation/Smoke/RerankerCheck.cs`:

````csharp
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Cortex.Contained.Agent.Host.Reranking;

namespace Cortex.Contained.Agent.Host.History.Evaluation.Smoke;

/// <summary>Outcome of the live reranker check: PASS/FAIL lines without any history text.</summary>
public sealed record RerankerCheckResult(bool Passed, IReadOnlyList<string> Lines);

/// <summary>
/// Live reranker check for increment 5 (§4.3), run with <c>history-smoke reranker-check</c> against the
/// real sidecar: the endpoint and key pass the exposure guard, the model loads, requests without the key
/// are refused, slot monitoring is off, a relevant passage ranks first, and the smallest and largest
/// budget cells answer within <c>Reranker:TimeoutMs</c>. Uses synthetic text only.
/// </summary>
public static class RerankerCheck
{
    private const string Query = "when does the night train to TownB leave";

    /// <summary>Runs every check; stops early when the endpoint may not be used or never becomes ready.</summary>
    public static async Task<RerankerCheckResult> RunAsync(
        LlamaServerReranker reranker, RerankerOptions options, string? apiKey, HttpClient http, TimeSpan readyTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(http);
        var lines = new List<string>();
        var passed = true;
        void Report(bool ok, string text)
        {
            passed &= ok;
            lines.Add((ok ? "PASS " : "FAIL ") + text);
        }

        if (RerankerEndpointGuard.Check(options.Endpoint, apiKey, options.AllowedHosts()) is { } reason)
        {
            Report(false, "endpoint: " + reason);
            return new RerankerCheckResult(false, lines);
        }

        Report(true, $"endpoint {new Uri(options.Endpoint).Host} is allowed and a key is set");

        var watch = Stopwatch.StartNew();
        while (!await reranker.ProbeHealthAsync(cancellationToken).ConfigureAwait(false))
        {
            if (watch.Elapsed > readyTimeout)
            {
                Report(false, string.Create(CultureInfo.InvariantCulture, $"the model did not load within {readyTimeout.TotalSeconds:F0} s (GET /health)"));
                return new RerankerCheckResult(false, lines);
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        Report(true, string.Create(CultureInfo.InvariantCulture, $"model ready after {watch.ElapsedMilliseconds} ms"));

        var baseUri = options.Endpoint.TrimEnd('/');
        using (var unauthenticated = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri + "/v1/rerank"))
        {
            Content = new StringContent("""{"query":"q","documents":["a","b"]}""", Encoding.UTF8, "application/json"),
        })
        using (var response = await http.SendAsync(unauthenticated, cancellationToken).ConfigureAwait(false))
        {
            Report(response.StatusCode == HttpStatusCode.Unauthorized,
                $"a request without the key is refused (HTTP {(int)response.StatusCode}, expected 401)");
        }

        using (var slots = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri + "/slots")))
        {
            slots.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await http.SendAsync(slots, cancellationToken).ConfigureAwait(false);
            Report(!response.IsSuccessStatusCode, $"slot monitoring is off (GET /slots: HTTP {(int)response.StatusCode})");
        }

        watch.Restart();
        var scores = await reranker.RerankAsync(
            Query,
            ["we had coffee and talked about the budget", "the night train to TownB leaves at 22:10 from platform 4", "invoice INV2041 is paid"],
            cancellationToken).ConfigureAwait(false);
        var best = scores.OrderByDescending(s => s.Score).First();
        Report(best.Index == 1, string.Create(CultureInfo.InvariantCulture,
            $"the relevant passage ranks first (score {best.Score:F3}, {watch.ElapsedMilliseconds} ms)"));

        foreach (var budget in new[] { new RerankBudget(10, 600), new RerankBudget(30, 2000) })
        {
            var documents = Enumerable.Range(0, budget.TopN).Select(i => EmbedderCheck.Synthetic(budget.MaxDocChars + i)[i..]).ToList();
            watch.Restart();
            await reranker.RerankAsync(Query, documents, cancellationToken).ConfigureAwait(false);
            var ms = watch.ElapsedMilliseconds;
            Report(ms <= options.TimeoutMs, string.Create(CultureInfo.InvariantCulture,
                $"{budget.Label}: {ms} ms (Reranker:TimeoutMs {options.TimeoutMs}; slower calls keep the fused order)"));
        }

        return new RerankerCheckResult(passed, lines);
    }
}
````

- [ ] **Step 4: Run the tests and confirm they pass**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.RerankerCheckTests"
```

Expected: PASS — `Passed: 3`.

- [ ] **Step 5: Commit** (never stage `version.json`)

```powershell
git add src/Cortex.Contained.Agent.Host/History/Evaluation/Smoke/HistorySmokeCli.cs src/Cortex.Contained.Agent.Host/History/Evaluation/Smoke/RerankerCheck.cs tests/Cortex.Contained.Agent.Host.Tests/Reranking/RerankerCheckTests.cs
git commit -m "feat(history-smoke): live reranker check (auth, slots off, ordering, budget latency)" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 14: `memory-eval rerank-curve` and `gate-r`

**Model:** standard

I5-14 for memory. `MemoryAblationConfig` gains `RerankTopN`/`RerankMaxDocChars`; `MemoryEvalCorpus` takes an optional stage, widens the request and applies `HybridMemoryReranking`, returns `MemoryRetrievalRun.Rerank`, and fails loudly for a reranked configuration without a reranker; `MemoryRerankEval` mirrors Task 12 on the self-deleting store copy (embedder load = synthetic document batches); `MemoryEvalCli` gets two `case`s and an optional reranker factory.

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryAblationGrid.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryAblationRunner.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs`
- Create: `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryRerankEval.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Reranking/MemoryRerankEvalTests.cs`

- [ ] **Step 1: Write the failing test**

**Create** `tests/Cortex.Contained.Agent.Host.Tests/Reranking/MemoryRerankEvalTests.cs`:

````csharp
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Tests.Memory.Evaluation;
using MemoryMcp.Core.Configuration;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary><c>memory-eval rerank-curve</c> and <c>gate-r</c> on a seeded store copy with fake embedder and reranker.</summary>
public sealed class MemoryRerankEvalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"memory-rerank-eval-{Guid.NewGuid():N}");
    private readonly MemoryMcpOptions live;
    private readonly MemoryEvalWorkspace workspace;
    private readonly FakeReranker reranker = new();

    public MemoryRerankEvalTests()
    {
        this.live = new MemoryMcpOptions
        {
            DataDirectory = Path.Combine(this.root, "live"),
            Ollama = new OllamaOptions { Dimensions = FakeMemoryEmbedder.Dimensions },
        };
        this.workspace = new MemoryEvalWorkspace(Path.Combine(this.root, "workspace"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private async Task<(int Code, string Output)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        var code = await MemoryEvalCli.RunAsync(
            [.. args, "--dir", this.workspace.Directory], output, this.live, _ => new FakeMemoryEmbedder(), CancellationToken.None, _ => this.reranker);
        return (code, output.ToString());
    }

    private async Task SeedAsync(int lookups)
    {
        await MemoryEvalCorpusTests.SeedAsync(
            this.live,
            ("mem-train", "Trip", "Train trip: the night train to TownB on Friday", ["travel"]),
            ("mem-invoice", null, "Invoice INV-2041 was paid in March", ["finance"]),
            ("mem-editor", null, "PersonA prefers a dark editor theme", []));
        Assert.Equal(0, (await this.RunAsync("init")).Code);
        File.WriteAllLines(this.workspace.DatasetPath, Enumerable.Range(0, lookups)
            .Select(i => new MemoryEvalQuery { Id = $"m{i:D3}", Text = "invoice paid", Category = "lookup", Judgments = [new MemoryJudgment("mem-invoice", 2)] })
            .Select(MemoryEvalDataset.ToLine));
    }

    [Fact]
    public async Task RerankCurve_WritesTheNineCells_AndDeletesTheCopy()
    {
        await this.SeedAsync(lookups: 6);

        var run = await this.RunAsync("rerank-curve", "--split", "all");

        Assert.Equal(0, run.Code);
        var curve = File.ReadAllText(Assert.Single(Directory.GetFiles(this.workspace.RunsDirectory, "curve.md", SearchOption.AllDirectories)));
        Assert.Equal(9, curve.Split('\n').Count(l => l.StartsWith("| normal | top", StringComparison.Ordinal)));
        Assert.DoesNotContain("INV-2041", curve, StringComparison.Ordinal);
        Assert.NotEmpty(this.reranker.Calls);
        Assert.Empty(Directory.GetDirectories(this.workspace.RunsDirectory, "corpus", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task GateR_Registered_ComparesWithTheSameConfigWithoutReranking()
    {
        await this.SeedAsync(lookups: 250);
        var hybrid = MemoryAblationGrid.Default().Single(c => c.Name == "hybrid-rrf60");
        this.workspace.WritePlan(new MemoryAblationPlan
        {
            Baseline = "hybrid-rrf60",
            Configs = [hybrid, hybrid with { Name = "hybrid-rerank", RerankTopN = 10 }],
            Holdout = new MemoryHoldoutPlan { Config = "hybrid-rerank" },
        });
        Assert.Equal(0, (await this.RunAsync("register")).Code);

        var gate = await this.RunAsync("gate-r", "--config", "hybrid-rerank");

        Assert.Equal(0, gate.Code);
        Assert.Contains("Gate R hybrid-rerank vs hybrid-rerank-no-rerank", gate.Output, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(this.workspace.RunsDirectory, "verdict.json", SearchOption.AllDirectories));
    }
}
````

- [ ] **Step 2: Run it and confirm it fails**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.MemoryRerankEvalTests|FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Memory.Evaluation"
```

Expected: FAIL — the build fails: `MemoryAblationConfig.RerankTopN` and the `MemoryEvalCli.RunAsync` reranker factory parameter do not exist yet.

- [ ] **Step 3: Implement**

**Modify** `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryAblationGrid.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryAblationGrid.cs b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryAblationGrid.cs
index 2cd36a5..eac3087 100644
--- a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryAblationGrid.cs
+++ b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryAblationGrid.cs
@@ -1,4 +1,5 @@
 using System.Text.Json;
+using Cortex.Contained.Agent.Host.Reranking;
 using MemoryMcp.Core.Configuration;
 
 namespace Cortex.Contained.Agent.Host.Memory.Evaluation;
@@ -33,6 +34,15 @@ public sealed record MemoryAblationConfig
     /// <summary>Use the model-written query and tags instead of the oracle text and tags.</summary>
     public bool UseModelQuery { get; init; }
 
+    /// <summary>Fused candidates reranked (§4.3); 0 = no reranking. Evaluated with <c>rerank-curve</c> and <c>gate-r</c>.</summary>
+    public int RerankTopN { get; init; }
+
+    /// <summary>Characters of raw best-matching lines per reranked candidate.</summary>
+    public int RerankMaxDocChars { get; init; } = 1200;
+
+    /// <summary>The rerank budget of this configuration, or null when it does not rerank.</summary>
+    public RerankBudget? RerankBudget() => this.RerankTopN > 0 ? new RerankBudget(this.RerankTopN, this.RerankMaxDocChars) : null;
+
     /// <summary>A copy of <paramref name="baseline"/> with this configuration applied.</summary>
     public MemorySearchOptions Apply(MemorySearchOptions baseline)
     {
````

**Modify** `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryAblationRunner.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryAblationRunner.cs b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryAblationRunner.cs
index dd3616c..179f2f7 100644
--- a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryAblationRunner.cs
+++ b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryAblationRunner.cs
@@ -1,3 +1,5 @@
+using Cortex.Contained.Agent.Host.Reranking;
+
 namespace Cortex.Contained.Agent.Host.Memory.Evaluation;
 
 /// <summary>One returned memory and its word count (reading cost).</summary>
@@ -7,7 +9,11 @@ public sealed record ReturnedMemory(string MemoryId, int Words);
 /// <param name="Memories">Returned memories, best first.</param>
 /// <param name="Confidence">Confidence that an answer exists (best semantic similarity; 0 for keyword-only).</param>
 /// <param name="LatencyMs">Search latency in milliseconds.</param>
-public sealed record MemoryRetrievalRun(IReadOnlyList<ReturnedMemory> Memories, double Confidence, double LatencyMs);
+public sealed record MemoryRetrievalRun(IReadOnlyList<ReturnedMemory> Memories, double Confidence, double LatencyMs)
+{
+    /// <summary>The rerank stage's outcome, or null when the configuration does not rerank.</summary>
+    public RerankOutcome? Rerank { get; init; }
+}
 
 /// <summary>
 /// Runs every configuration on every query, scores memory-level metrics with the shared
````

**Modify** `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs
index 8689d5b..f5b3506 100644
--- a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs
+++ b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs
@@ -1,6 +1,8 @@
 using System.Globalization;
 using System.Runtime.InteropServices;
 using System.Text.Json;
+using Cortex.Contained.Agent.Host.Reranking;
+using Cortex.Contained.Agent.Host.Reranking.Evaluation;
 using Cortex.Contained.Contracts.Config.Yaml;
 using MemoryMcp.Core.Configuration;
 using MemoryMcp.Core.Services;
@@ -13,15 +15,17 @@ namespace Cortex.Contained.Agent.Host.Memory.Evaluation;
 /// <summary>
 /// Evaluation CLI for hybrid memory search (Gate H for memory), run by the owner inside the agent
 /// container on a private copy of the raw memory store (D6):
-/// <c>dotnet Cortex.Contained.Agent.Host.dll memory-eval &lt;init|sample|validate|guard|pool|register|ablate|holdout|kappa&gt;</c>.
+/// <c>dotnet Cortex.Contained.Agent.Host.dll memory-eval &lt;init|sample|validate|guard|pool|register|ablate|holdout|kappa|rerank-curve|gate-r&gt;</c>.
 /// Every search runs on a copy that is deleted when the command ends; reports hold ids and numbers
 /// only. The Gate H decision and switching <c>MemoryMcp:Search:DefaultMode</c> are the owner's.
 /// </summary>
 public static class MemoryEvalCli
 {
     private const string Usage =
-        "usage: memory-eval <init|sample|validate|guard|pool|register|ablate|holdout|kappa> [--dir /app/state/eval/memory] " +
-        "[--source /app/state/memory] [--count 300] [--depth 10] [--split dev|test|all] [--embedder http://embeddings:11434] [--labels judge-agreement.csv]";
+        "usage: memory-eval <init|sample|validate|guard|pool|register|ablate|holdout|kappa|rerank-curve|gate-r> [--dir /app/state/eval/memory] " +
+        "[--source /app/state/memory] [--count 300] [--depth 10] [--split dev|test|all] [--embedder http://embeddings:11434] [--labels judge-agreement.csv]\n" +
+        "rerank-curve [--condition normal|embedder-busy|cold] [--base CONFIG] [--reranker http://reranker:8080] [--timeout-ms 30000]; " +
+        "gate-r --config CONFIG [--timeout-ms 2000]; both read the reranker key from Reranker__ApiKey.";
 
     /// <summary>Minimum LLM-vs-human agreement for LLM relevance labels.</summary>
     private const double MinKappa = 0.6;
@@ -120,9 +124,11 @@ public static class MemoryEvalCli
         TextWriter output,
         MemoryMcpOptions live,
         Func<MemoryMcpOptions, IEmbeddingService> embedderFactory,
-        CancellationToken cancellationToken)
+        CancellationToken cancellationToken,
+        Func<RerankerOptions, IReranker>? rerankerFactory = null)
     {
         ArgumentNullException.ThrowIfNull(args);
+        rerankerFactory ??= RerankEvalSetup.LiveReranker;
         ArgumentNullException.ThrowIfNull(output);
         ArgumentNullException.ThrowIfNull(live);
         ArgumentNullException.ThrowIfNull(embedderFactory);
@@ -170,6 +176,10 @@ public static class MemoryEvalCli
                     return await HoldoutAsync(context, cancellationToken).ConfigureAwait(false);
                 case "kappa":
                     return await KappaAsync(Option(args, "--labels") ?? Path.Combine(workspace.Directory, "judge-agreement.csv"), output).ConfigureAwait(false);
+                case "rerank-curve":
+                    return await MemoryRerankEval.CurveAsync(workspace, live, embedderFactory, rerankerFactory, args, output, cancellationToken).ConfigureAwait(false);
+                case "gate-r":
+                    return await MemoryRerankEval.GateAsync(workspace, live, embedderFactory, rerankerFactory, args, output, cancellationToken).ConfigureAwait(false);
                 default:
                     await output.WriteLineAsync(Usage).ConfigureAwait(false);
                     return 2;
````

**Modify** `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs
index e0460bd..bd7c102 100644
--- a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs
+++ b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs
@@ -1,4 +1,5 @@
 using System.Diagnostics;
+using Cortex.Contained.Agent.Host.Reranking;
 using MemoryMcp.Core.Configuration;
 using MemoryMcp.Core.Security;
 using MemoryMcp.Core.Services;
@@ -28,9 +29,11 @@ public sealed class MemoryEvalCorpus : IAsyncDisposable
     private readonly MemoryMcpOptions options;
     private readonly SqliteVecMemoryStore store;
     private readonly HybridMemorySearch search;
+    private readonly IRerankStage? reranker;
 
-    private MemoryEvalCorpus(string corpusDirectory, FileStream runLock, MemoryMcpOptions options, SqliteVecMemoryStore store, IEmbeddingService embedding)
+    private MemoryEvalCorpus(string corpusDirectory, FileStream runLock, MemoryMcpOptions options, SqliteVecMemoryStore store, IEmbeddingService embedding, IRerankStage? reranker)
     {
+        this.reranker = reranker;
         this.corpusDirectory = corpusDirectory;
         this.runLock = runLock;
         this.options = options;
@@ -43,7 +46,7 @@ public sealed class MemoryEvalCorpus : IAsyncDisposable
     /// <paramref name="runDirectory"/><c>/corpus</c> and opens it. Fails when the keyword index cannot be built.
     /// </summary>
     public static async Task<MemoryEvalCorpus> CreateAsync(
-        string sourceDirectory, string runDirectory, MemoryMcpOptions live, IEmbeddingService embedding, CancellationToken cancellationToken)
+        string sourceDirectory, string runDirectory, MemoryMcpOptions live, IEmbeddingService embedding, CancellationToken cancellationToken, IRerankStage? reranker = null)
     {
         ArgumentNullException.ThrowIfNull(live);
         ArgumentNullException.ThrowIfNull(embedding);
@@ -95,7 +98,7 @@ public sealed class MemoryEvalCorpus : IAsyncDisposable
         }
 
         var store = new SqliteVecMemoryStore(Options.Create(options), new NullContentEncryptor(), NullLogger<SqliteVecMemoryStore>.Instance);
-        var corpus = new MemoryEvalCorpus(corpusDirectory, runLock, options, store, embedding);
+        var corpus = new MemoryEvalCorpus(corpusDirectory, runLock, options, store, embedding, reranker);
         try
         {
             await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
@@ -118,18 +121,31 @@ public sealed class MemoryEvalCorpus : IAsyncDisposable
     {
         ArgumentNullException.ThrowIfNull(config);
         ArgumentNullException.ThrowIfNull(query);
+        var rerank = config.RerankBudget();
+        if (rerank is not null && this.reranker is null)
+        {
+            throw new InvalidOperationException($"Configuration '{config.Name}' reranks, but this run has no reranker: evaluate rerank configurations with 'rerank-curve' or 'gate-r'.");
+        }
+
         var model = config.UseModelQuery ? query.ModelQuery : null;
+        var text = model?.Text ?? query.Text;
         var watch = Stopwatch.StartNew();
         var result = await this.search.SearchAsync(
             new HybridSearchRequest
             {
-                Query = model?.Text ?? query.Text,
+                Query = text,
                 Mode = config.Mode,
-                Limit = ResultLimit,
+                Limit = HybridMemoryReranking.CandidateLimit(ResultLimit, rerank),
                 Filter = new MemorySearchFilter { Tags = model is not null ? model.Tags : query.Tags },
                 Settings = config.Apply(this.options.Search),
             },
             cancellationToken).ConfigureAwait(false);
+        RerankOutcome? outcome = null;
+        if (rerank is not null)
+        {
+            (result, outcome, _) = await HybridMemoryReranking.ApplyAsync(this.reranker, text, result, ResultLimit, rerank, cancellationToken).ConfigureAwait(false);
+        }
+
         var latency = watch.Elapsed.TotalMilliseconds;
         if (result.Fallback is { } fallback)
         {
@@ -139,7 +155,7 @@ public sealed class MemoryEvalCorpus : IAsyncDisposable
         var returned = result.Hits
             .Select(h => new ReturnedMemory(h.Memory.MemoryId, WordsShown(h.Memory.Content, this.options.SearchMaxContentLength)))
             .ToList();
-        return new MemoryRetrievalRun(returned, result.TopSimilarity ?? 0, latency);
+        return new MemoryRetrievalRun(returned, result.TopSimilarity ?? 0, latency) { Rerank = outcome };
     }
 
     /// <summary>Every memory with its title, tags and content (query seeds and the paraphrase guard; never written to reports).</summary>
````

**Create** `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryRerankEval.cs`:

````csharp
using System.Diagnostics;
using System.Globalization;
using Cortex.Contained.Agent.Host.History.Evaluation.Smoke;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Memory.Evaluation;

/// <summary>
/// The reranker evaluation of <c>memory-eval</c> (§4.3, Gate R for memory): <c>rerank-curve</c> over the
/// TopN × MaxDocChars grid under one condition and <c>gate-r</c> against the same hybrid configuration
/// without reranking on the test split. Runs on the self-deleting store copy; outputs hold ids and numbers only.
/// </summary>
internal static class MemoryRerankEval
{
    /// <summary>The hybrid configuration the curve reranks when neither <c>--base</c> nor a holdout choice is given.</summary>
    public const string DefaultBase = "hybrid-rrf60";

    private const int ColdReadyTimeoutSeconds = 180;

    /// <summary><c>rerank-curve [--split dev] [--condition normal|embedder-busy|cold] [--base CONFIG] [--reranker URL] [--timeout-ms N]</c>.</summary>
    public static async Task<int> CurveAsync(
        MemoryEvalWorkspace workspace,
        MemoryMcpOptions live,
        Func<MemoryMcpOptions, IEmbeddingService> embedderFactory,
        Func<RerankerOptions, IReranker> rerankerFactory,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var condition = Option(args, "--condition") ?? RerankConditions.Normal;
        if (!RerankConditions.IsValid(condition))
        {
            throw new InvalidOperationException($"--condition must be {RerankConditions.Normal}, {RerankConditions.EmbedderBusy} or {RerankConditions.Cold}.");
        }

        var split = Option(args, "--split") ?? EvalSplits.Dev;
        var plan = workspace.ReadPlan();
        var baseName = Option(args, "--base") ?? plan.Holdout?.Config ?? DefaultBase;
        var baseConfig = plan.Configs.FirstOrDefault(c => c.Name == baseName && c.Mode == MemorySearchMode.Hybrid && c.RerankBudget() is null)
            ?? throw new InvalidOperationException($"ablation.json has no hybrid configuration '{baseName}' without reranking to use as the curve's base (--base).");
        var queries = ReadQueries(workspace, split);
        var (reranker, stage) = CreateStage(rerankerFactory, Option(args, "--reranker"), RerankEvalSetup.IntOption(Option(args, "--timeout-ms"), RerankEvalSetup.CurveTimeoutMs));

        var runDir = NewRunDirectory(workspace, $"rerank-curve-{condition}-{split}");
        double? readyMs = null;
        double? firstCallMs = null;
        IReadOnlyList<RerankCurveRow> rows;
        AblationReport report;
        int? loadCompleted = null;
        int? loadFailed = null;
        await using (var corpus = await MemoryEvalCorpus.CreateAsync(live.DataDirectory, runDir, live, embedderFactory(live), cancellationToken, stage).ConfigureAwait(false))
        {
            if (condition == RerankConditions.Cold)
            {
                readyMs = await WaitReadyAsync(reranker, cancellationToken).ConfigureAwait(false);
            }

            await using var load = condition == RerankConditions.EmbedderBusy ? StartEmbedderLoad(embedderFactory, live) : null;
            (rows, report) = await RerankEvaluation.CurveAsync(
                queries,
                RerankGrid.Cells(),
                condition,
                async (name, budget, query, ct) =>
                {
                    var config = baseConfig with { Name = name, RerankTopN = budget?.TopN ?? 0, RerankMaxDocChars = budget?.MaxDocChars ?? baseConfig.RerankMaxDocChars };
                    var run = await corpus.SearchAsync(config, query, ct).ConfigureAwait(false);
                    firstCallMs ??= run.Rerank is { Status: not RerankStatus.Skipped } called ? called.LatencyMs : null;
                    return new RerankRun(MemoryAblationRunner.Score(name, query, run), run.Rerank);
                },
                cancellationToken).ConfigureAwait(false);
            loadCompleted = load?.Completed;
            loadFailed = load?.Failed;
        }

        var coldNote = condition == RerankConditions.Cold
            ? string.Create(CultureInfo.InvariantCulture, $"Cold model: the reranker answered /health after {readyMs:F0} ms; the first rerank call took {firstCallMs ?? 0:F0} ms (later calls are warm).")
            : null;
        MemoryEvalWorkspace.WriteJson(Path.Combine(runDir, "results.json"), report);
        MemoryEvalWorkspace.WriteJson(Path.Combine(runDir, "curve.json"), new { condition, split, Base = baseName, rows, readyMs, firstCallMs, loadCompleted, loadFailed });
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "curve.md"),
            RerankEvaluation.Markdown(rows, string.Create(CultureInfo.InvariantCulture, $"memory rerank curve: {condition} on {split} ({queries.Count} queries, base {baseName})"), coldNote),
            cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Curve: {Path.Combine(runDir, "curve.md")}").ConfigureAwait(false);
        return 0;
    }

    /// <summary><c>gate-r --config CONFIG [--reranker URL] [--timeout-ms N]</c> on the test split (registration required).</summary>
    public static async Task<int> GateAsync(
        MemoryEvalWorkspace workspace,
        MemoryMcpOptions live,
        Func<MemoryMcpOptions, IEmbeddingService> embedderFactory,
        Func<RerankerOptions, IReranker> rerankerFactory,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        workspace.VerifyRegistration();
        var plan = workspace.ReadPlan();
        var name = Option(args, "--config") ?? throw new InvalidOperationException("gate-r needs --config: the registered reranked configuration.");
        var candidate = plan.Configs.FirstOrDefault(c => c.Name == name && c.RerankBudget() is not null)
            ?? throw new InvalidOperationException($"ablation.json has no reranked configuration '{name}' (rerankTopN > 0).");
        var unreranked = candidate with { Name = name + "-no-rerank", RerankTopN = 0 };
        var queries = ReadQueries(workspace, EvalSplits.Test);
        var (_, stage) = CreateStage(rerankerFactory, Option(args, "--reranker"), RerankEvalSetup.IntOption(Option(args, "--timeout-ms"), new RerankerOptions().TimeoutMs));

        var runDir = NewRunDirectory(workspace, "gate-r");
        AblationReport report;
        var busy = new List<double>();
        await using (var corpus = await MemoryEvalCorpus.CreateAsync(live.DataDirectory, runDir, live, embedderFactory(live), cancellationToken, stage).ConfigureAwait(false))
        {
            report = await MemoryAblationRunner.RunAsync([unreranked, candidate], queries, corpus.SearchAsync, unreranked.Name, cancellationToken).ConfigureAwait(false);
            await using (StartEmbedderLoad(embedderFactory, live))
            {
                foreach (var query in queries)
                {
                    busy.Add((await corpus.SearchAsync(candidate, query, cancellationToken).ConfigureAwait(false)).LatencyMs);
                }
            }
        }

        var verdict = RerankEvaluation.Gate(
            report.Comparisons.Single(c => c.Config == name && c.Slice == "all"),
            report.Summaries.Single(s => s.Config == name).P95LatencyMs,
            busy.Count == 0 ? 0 : EvalStatistics.Percentile(busy, 95));
        MemoryEvalWorkspace.WriteJson(Path.Combine(runDir, "results.json"), report);
        MemoryEvalWorkspace.WriteJson(Path.Combine(runDir, "verdict.json"), verdict);
        await File.WriteAllTextAsync(Path.Combine(runDir, "report.md"), AblationReporting.Markdown(report, $"Gate R (memory): {name} on test ({queries.Count} queries)"), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(RerankEvaluation.Describe(verdict)).ConfigureAwait(false);
        return 0;
    }

    private static List<MemoryEvalQuery> ReadQueries(MemoryEvalWorkspace workspace, string split)
    {
        var all = MemoryEvalDataset.Read(workspace.DatasetPath);
        if (MemoryEvalDataset.Validate(all) is { Count: > 0 } errors)
        {
            throw new InvalidOperationException($"dataset.jsonl has {errors.Count} problems; run 'validate'.");
        }

        return split == "all" ? [.. all] : [.. all.Where(q => EvalSplits.Assign(q.Id) == split)];
    }

    private static (IReranker Reranker, RerankStage Stage) CreateStage(Func<RerankerOptions, IReranker> factory, string? endpoint, int timeoutMs)
    {
        var options = RerankEvalSetup.Options(endpoint, timeoutMs);
        var reranker = factory(options);
        if (reranker.UnavailableReason is { } reason)
        {
            throw new InvalidOperationException("the reranker cannot be used: " + reason + " (pass the key as Reranker__ApiKey; see docs/reranker.md).");
        }

        return (reranker, RerankEvalSetup.Stage(reranker, options));
    }

    private static async Task<double> WaitReadyAsync(IReranker reranker, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        if (reranker is not LlamaServerReranker live)
        {
            return 0;
        }

        while (watch.Elapsed < TimeSpan.FromSeconds(ColdReadyTimeoutSeconds))
        {
            if (await live.ProbeHealthAsync(cancellationToken).ConfigureAwait(false))
            {
                return watch.Elapsed.TotalMilliseconds;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"the reranker did not become ready within {ColdReadyTimeoutSeconds} s.");
    }

    /// <summary>Keeps the embedder busy with batches of synthetic documents (no memory text).</summary>
    private static BackgroundLoad StartEmbedderLoad(Func<MemoryMcpOptions, IEmbeddingService> embedderFactory, MemoryMcpOptions live)
    {
        var embedder = embedderFactory(live);
        IReadOnlyList<string> batch = [.. Enumerable.Repeat(EmbedderCheck.Synthetic(3000), 16)];
        return BackgroundLoad.Start(ct => embedder.EmbedBatchStrictAsync(batch, ct));
    }

    private static string NewRunDirectory(MemoryEvalWorkspace workspace, string kind) =>
        Path.Combine(workspace.RunsDirectory, string.Create(CultureInfo.InvariantCulture, $"{kind}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}"));

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
````

- [ ] **Step 4: Run the tests and confirm they pass**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking.MemoryRerankEvalTests|FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Memory.Evaluation"
```

Expected: PASS — `Passed: 21`.

- [ ] **Step 5: Commit** (never stage `version.json`)

```powershell
git add src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryAblationGrid.cs src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryAblationRunner.cs src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryRerankEval.cs tests/Cortex.Contained.Agent.Host.Tests/Reranking/MemoryRerankEvalTests.cs
git commit -m "feat(memory-eval): rerank latency/quality curve and Gate R commands" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 15: Documentation

**Model:** cheap

`docs/reranker.md` (pieces, behaviour, configuration, exposure, measured latencies, owner Gate R workflow), the reranker sections of `docs/security.md`, `docs/history-hybrid-search.md` and `docs/memory-hybrid-search.md`, and the CLAUDE.md reference link. (`docs/self-update.md` changed in Task 10.)

**Files:**
- Modify: `CLAUDE.md`
- Modify: `docs/history-hybrid-search.md`
- Modify: `docs/memory-hybrid-search.md`
- Create: `docs/reranker.md`
- Modify: `docs/security.md`

- [ ] **Step 1: Implement**

**Modify** `CLAUDE.md` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/CLAUDE.md b/CLAUDE.md
index c959b71..e5e3d99 100644
--- a/CLAUDE.md
+++ b/CLAUDE.md
@@ -134,6 +134,7 @@ scripts/
 - [Design reference](docs/design-reference.md) — design patterns and conventions
 - [Setup guide](docs/setup-guide.md) — installation and configuration
 - [Self-update](docs/self-update.md) — how to deploy a cortex change to the running install (MSIX + agent image), detached from a coda session
+- [Reranker](docs/reranker.md) — the opt-in `cortex-reranker` sidecar after hybrid history/memory fusion: exposure, Bridge key, Self-Update coverage, Gate R workflow
 - [History locator](docs/history-locator.md) — episodic history search (`history_search`/`history_read`), journal + FTS5 index, erasure workflow, D10 memory quarantine, smoke eval and behaviour gate
 - [Event log and provenance](docs/event-log.md) — content-free events for messages, tools and sends; delivery reports; `event_get`; backfill; encryption at rest
 
````

**Modify** `docs/history-hybrid-search.md` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/docs/history-hybrid-search.md b/docs/history-hybrid-search.md
index 1dd0df5..144ebe5 100644
--- a/docs/history-hybrid-search.md
+++ b/docs/history-hybrid-search.md
@@ -66,6 +66,14 @@ pending job. Relevance hits carry `match keyword|semantic|both`.
 `/health → history.dense`: `state`, `embedded`, `total`, `pending`, `failed`, `oldestPendingQueuedAt`,
 `lastError`. An embedder failure never halts the projector: embedding pauses for 30 ticks and retries.
 
+## Reranking (increment 5)
+
+With the reranker enabled (Bridge `Reranker:Enabled`, default false), the first `Reranker:History:TopN`
+fused segments of a hybrid relevance search are reordered by the `cortex-reranker` sidecar, which sees
+only each segment's verified matching lines (≤ `MaxDocChars`). The coverage block adds a `rerank:` line
+(`on (…)`, `skipped: …`, `failed: …; fused order kept`, `not used: …`) and reranked hits show
+`| rerank 0.87`. Keyword and time-order searches are not reranked. Evaluation: `history-eval rerank-curve`
+and `gate-r`. See [reranker.md](reranker.md).
 ## Erasure and encryption
 
 Vectors (float32 in `chunks`, the int8 copy in `chunk_q8`) live in the generation file, so they are encrypted
````

**Modify** `docs/memory-hybrid-search.md` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/docs/memory-hybrid-search.md b/docs/memory-hybrid-search.md
index 82cdcc8..9fa23bd 100644
--- a/docs/memory-hybrid-search.md
+++ b/docs/memory-hybrid-search.md
@@ -50,6 +50,13 @@ hits ≠ never happened).
 
 Invalid values stop the agent at startup.
 
+## Reranking (increment 5)
+
+With the reranker enabled (Bridge `Reranker:Enabled`, default false), `memory_search` in hybrid mode asks
+for `Reranker:Memory:TopN` fused memories, has the `cortex-reranker` sidecar reorder them by their title and
+content lines (≤ `MaxDocChars` each), returns `limit` of them and adds a `rerank: …` line to the footer.
+Dense (the default mode) and keyword searches are not reranked, so the default output is unchanged.
+Evaluation: `memory-eval rerank-curve` and `gate-r`. See [reranker.md](reranker.md).
 ## Erasure
 
 Deleting or editing a memory (tool, Bridge UI, consolidation, compaction) removes its FTS row in the same
````

**Create** `docs/reranker.md`:

````markdown
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
  (`off`, `ok`, `circuit-open`, `unavailable: …`), `consecutiveFailures`, `circuitOpenUntil`, `lastError`,
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

## Measured on the owner's machine (2026-11-21, synthetic English text)

| Variant | Cell | Latency |
|---|---|---|
| CUDA (RTX 4070 Laptop) | top10 × 600 chars | ~100 ms |
| CUDA | top15 × 1200 chars | ~240 ms |
| CUDA | top30 × 2000 chars | ~0.7–1.1 s |
| CPU (20 cores) | top10 × 600 chars | ~6.5 s |

The CPU variant cannot meet Gate R's p95 ≤ 2.5 s at any useful budget; use the GPU.

## Owner workflow (Gate R)

Inside the agent container (`docker exec -it cortex-agent sh`). The evaluation CLIs run outside the agent
process, so they read the key from the environment; take it from the sidecar:

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
````

**Modify** `docs/security.md` (`git apply --ignore-whitespace --recount`):

````diff
diff --git a/docs/security.md b/docs/security.md
index 697cc66..be4f3d1 100644
--- a/docs/security.md
+++ b/docs/security.md
@@ -98,6 +98,18 @@ only while the source segment is unchanged. `history-eval` run directories hold
 `messages.db`; purges destroy the ones overlapping the purged range. See
 [history-hybrid-search.md](history-hybrid-search.md).
 
+## Reranker sidecar exposure
+
+With `Reranker:Enabled` (Bridge configuration, default false), the raw best-matching lines of the top
+hybrid history and memory candidates are sent to the `cortex-reranker` sidecar. It is reachable only on
+the internal compose network (no published port), requires an API key on every request (generated once,
+DPAPI-encrypted by the Bridge, passed to the container through the `docker compose` process environment
+and pushed to the agent's memory; the entrypoint refuses to start without it), logs no requests
+(`--log-verbosity 2`), exposes no web UI, `/slots` or `/metrics`, runs as an unprivileged user on a
+read-only root and keeps no state. The agent refuses to send text to any other host unless it uses HTTPS,
+and always with the key. The image's server and model are pinned (digest, sha256) and Self-Update verifies
+the built image id. See [reranker.md](reranker.md).
+
 ## Curated memory keyword index
 
 `memory.db` holds a contentless FTS5 index of every curated memory (folded title and content tokens
````

- [ ] **Step 2: Commit** (never stage `version.json`)

```powershell
git add CLAUDE.md docs/history-hybrid-search.md docs/memory-hybrid-search.md docs/reranker.md docs/security.md
git commit -m "docs(reranker): reranker guide, security, hybrid search and reference links" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 16: Final verification

**Model:** standard

Runs every suite this increment touches, checks the solution build, and checks that nothing changes while reranking is off.

- [ ] **Step 1: Build without warnings**

```powershell
dotnet build cortex-contained.sln -p:NuGetAudit=false
```

Expected: `Build succeeded.` with `0 Warning(s)`.

- [ ] **Step 2: Run the suites**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false
dotnet test tests/Cortex.Contained.Bridge.Tests -p:NuGetAudit=false
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Cortex.Contained.Agent.Host.Tests.Reranking"
dotnet test tests/Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "Category=Integrity"
```

Expected: PASS — Agent.Host `Passed: 2621` (2568 before this increment + 53 new), Bridge `Passed: 1871` (1864 + 7), Reranking `Passed: 53`, Integrity `Passed: 43` (unchanged). A timing-sensitive test may flake in a full run; re-run it alone.

- [ ] **Step 3: Prove "off" changes nothing**

```powershell
git diff --stat ab2edb0 HEAD -- tests/Cortex.Contained.Agent.Host.Tests/History tests/Cortex.Contained.Agent.Host.Tests/Tools tests/Cortex.Contained.Agent.Host.Tests/Memory tests/Cortex.Contained.Agent.Host.Tests/Integrity
```

Expected: no output — the increment 1–4 tests (history search output, `memory_search` byte-identical dense output, integrity) were not edited and pass; defaults keep `Reranker:Enabled=false`.

- [ ] **Step 4: Check the branch**

```powershell
git log --oneline ab2edb0..HEAD
git status --short
```

Expected: 15 commits; only `version.json` is modified (never staged). Pushing, the owner's curves, Gate R and enabling the reranker (spec §9) are out of this plan.

- [ ] **Step 5 (optional, manual — needs Docker and a GPU): live check**

```powershell
.\scripts\Build-RerankerImage.ps1 -NoPrune
$env:CORTEX_RERANKER_API_KEY = 'try-key'; docker compose --profile reranker up -d reranker; Remove-Item Env:CORTEX_RERANKER_API_KEY
docker exec -e Reranker__ApiKey=try-key cortex-agent dotnet Cortex.Contained.Agent.Host.dll history-smoke reranker-check
docker compose --profile reranker rm -sf reranker
```

Expected (verified on the plan author's machine, RTX 4070 Laptop): every line `PASS` — model ready < 2 s, 401 without the key, `/slots` 501, relevant passage first, `top10-chars600` ≈ 0.2 s, `top30-chars2000` ≈ 1.3 s. (The agent container must run this increment's image for the `reranker-check` command to exist.)

---

## Spec coverage

| Spec item | Task(s) |
|---|---|
| I5-1 llama-server `/v1/rerank`, Qwen3-Reranker-0.6B pinned by sha256, baked in; bge fallback documented | 3, 8 |
| I5-2 in-repo build context `docker/reranker` | 8 |
| I5-3 GPU default, CPU variant, measured latencies | 8, 10, 15 |
| I5-4 exposure: no port, internal net, key required, request logging off, no UI/slots/metrics, unprivileged, read-only, `pull_policy: never`; per-call guard; HttpClient without loggers | 1, 3, 7, 8 |
| I5-5 DPAPI key, env-only to the container, hub grant, memory-only in the agent | 1, 7, 9 |
| I5-6 single Bridge switch `Reranker:Enabled` (default false); agent technical settings and per-surface budgets; validation | 1, 7, 9 |
| I5-7 after fusion only (history hybrid relevance, memory hybrid); others say `not used` | 5, 6 |
| I5-8 raw best-matching lines only within `MaxDocChars` | 2, 5, 6 |
| I5-9 head reorder, scores, deterministic pages, no call past the head, cursor binding | 4, 5 |
| I5-10 history reranks inside the read lease | 5 |
| I5-11 timeout, errors, circuit breaker, honest notes, `/health` | 4, 5, 6, 7 |
| I5-12 memory reranking in Agent.Host, no submodule change | 6, 14 |
| I5-13 Self-Update build, pin (`imageIds`), upgrade via Bridge reconcile, health gate, rollback, downgrade, drill | 9, 10 |
| I5-14 evaluation: rerank dimension, `rerank-curve` (grid × normal/embedder-busy/cold), `gate-r` (paired gain + p95 ≤ 2.5 s), loud refusal in `ablate`/`holdout` | 11, 12, 14 |
| I5-15 `history-smoke reranker-check` | 13 |
| §4.3 "defer until inc3–4 show a measured need" | default off (1, 9); owner decision (spec §9) |
| Documentation | 10, 15 |
| §9 owner tasks (curves, Gate R, enabling) | not implemented (owner) |

## Out of scope

Running the evaluation and enabling the reranker (owner); a Bridge web UI switch; reranking keyword, dense or time-order results; a GHCR release or separate repository for the image; caching rerank scores; reranking for consolidation, compaction or subagent seeding; the standalone MemoryMcp `search_memory` tool; voice-specific budgets.
