# Recall Settings UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A per-tenant **Recall** page in the Bridge web UI to configure and observe the recall stack at runtime — history search on/off, default history search mode, dense embeddings, reranker switch and budgets, default memory search mode, live status, history deletion (channel / all; everything / older than / between dates) with a preview and the owner's per-purge choice for dependent memories (*review* = D10 quarantine, default; *delete* = erased with the history), a quarantine review list with bulk keep/delete, and an event-backfill button. Shipped defaults stay unchanged; the owner's choices persist locally per tenant.

**Architecture:**
- **Agent** (`Agent.Host/Recall`): `RecallSettingsStore` persists nullable overrides in `<state>/recall-settings.json` (state volume, outside the tool sandbox) and feeds `IPostConfigureOptions` for `HistoryOptions`, `RerankerOptions` and `MemoryMcpOptions` plus a change-token source, so every `IOptionsMonitor` consumer sees changes live. `HistoryProjectorService` polls `History:Enabled` every tick (start/idle without a restart); a new guard rebuilds when the journal was pruned past the index. `HistoryPurgeService` gains `From` (between dates), `PreviewAsync`, and `DependentMemoryAction.Delete` (erasure step `dependent-memories` → one `DeleteManyAsync` batch). `QuarantineReviewService` lists quarantined memories with their purge (new `ChannelId` column) and keeps/deletes in bulk. `RecallControlService` is the facade behind a new typed `IRecallHub` (thin `AgentHub.Recall.cs`).
- **Bridge**: `RecallEndpoints` (`/api/tenants/{tenantId}/recall/...`, `/api/recall/reranker`, all `RequireAuthorization`) as static handler seams over `IRecallAgentClient` (adapter over `HubClient`); `RerankerSwitch` persists `reranker: enabled:` in `cortex.yml` (new YAML section) and converges sidecar + grants in a safe order; `RerankerSidecarLifecycle` records honest sidecar problems (image missing, compose/GPU failure, exited). `HistoryDeletionResponse`/`HistoryPurgeConfirmation` state the chosen option and counts.
- **UI** (Alpine): `tenant-recall.js` page (settings, reranker, status polling, delete history, needs review), shared `recall-purge.js` used by the History and channel "Clear" modals; Memory page links to the review list.

**Tech Stack:** .NET 10, C#, ASP.NET Core minimal API + SignalR (JSON protocol), `Microsoft.Extensions.Options` post-configure + `IOptionsChangeTokenSource<T>`, SQLite (SQLite3MC) via `Microsoft.Data.Sqlite`, xUnit v2 + NSubstitute + `Microsoft.Extensions.Time.Testing`, Alpine.js 3 + Bootstrap 5 (+ Bootstrap Icons), Node (`node --check` only).

**Design spec:** [`docs/superpowers/specs/2026-11-22-recall-settings-ui-design.md`](../specs/2026-11-22-recall-settings-ui-design.md) (decisions R1–R15). Parent: [`2026-11-21-episodic-history-memory-design.md`](../specs/2026-11-21-episodic-history-memory-design.md) D3/D10 (amended in Task 19).

**Base:** `feat/episodic-history-memory` at `43c5747`. No submodule change.

---

## Before you start (gotchas found while replaying this plan)

- **Line endings and BOMs:** `src/**/*.cs` working copies are CRLF; `Program.cs` files start with a UTF-8 BOM; `app.html` is CRLF. Blocks below are LF; use the edit tool (or an editor) and keep each file's BOM and line endings. **Create** blocks are complete files; **Edit** blocks are exact `old → new` replacements.
- **PowerShell pitfalls:** never paste C#/Markdown into a *double-quoted* here-string (backticks become escapes). `[IO.File]::ReadAllText/WriteAllText` resolve relative paths against the **process** directory, not `Set-Location` — always pass absolute paths (this wrote into the wrong worktree during the replay).
- **Never stage `version.json`.** Stage only the files listed in each task's commit step.
- **NuGet audit:** every `dotnet build`/`dotnet test` uses `-p:NuGetAudit=false` (known NU1902 advisory on MessagePack).
- **Locked DLLs on Windows:** set `$env:MSBUILDDISABLENODEREUSE=1` (and `-nodeReuse:false` for solution builds) if MSBuild nodes keep `bin\` files locked.
- **Analyzers that bit during the replay:** CA1822 (a test helper that touches no instance state must be `static`), CA1861 (no inline constant arrays in `Arg.Is` lambdas — compare `Count`/`Contains`), CA1001 (a class owning a `SemaphoreSlim` must be `IDisposable`), CA1873 (compute `[LoggerMessage]` arguments into a local first).
- **SignalR client:** `HubClient` partials need `using Microsoft.AspNetCore.SignalR.Client;` for `InvokeAsync`.
- **Hub contract order:** `IRecallHub` is created in Task 1 but composed into `IAgentHub` only in Task 12 (when `AgentHub` implements it); `HubClient.Recall.cs` (Task 16) uses `nameof(IAgentHub.…)` and therefore comes after Task 12.
- **No UI test harness exists.** UI tasks are verified with `node --check`, a full build (static files are copied), and the optional manual smoke in Task 20.
- **Enums on the wire:** the hub uses the JSON protocol without a string-enum converter, and the Bridge binds JS bodies; all new modes are therefore **strings** (`"auto"|"hybrid"|"keyword"`, `"dense"|…`, `"review"|"delete"`), parsed and validated on the agent.
- **Commits:** every commit ends with the trailer passed as a second `-m`: `-m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"`.

## File structure

| File | Change | Task(s) |
|---|---|---|
| `src/Cortex.Contained.Contracts/Hub/RecallTypes.cs` | create | 1 |
| `src/Cortex.Contained.Contracts/Hub/IRecallHub.cs` | create | 1 |
| `src/Cortex.Contained.Contracts/Hub/HistoryPurgeTypes.cs` | modify (replace) | 1 |
| `src/Cortex.Contained.Contracts/Hub/IAgentHub.cs` | modify | 12 |
| `tests/Cortex.Contained.Contracts.Tests/Hub/RecallContractsTests.cs` | create | 1 |
| `src/Cortex.Contained.Agent.Host/History/HistoryOptions.cs` | modify | 2 |
| `src/Cortex.Contained.Agent.Host/History/HistoryRecallService.Hybrid.cs` | modify | 2 |
| `src/Cortex.Contained.Agent.Host/Recall/RecallOverrides.cs` | create | 3 |
| `src/Cortex.Contained.Agent.Host/Recall/RecallSettingsStore.cs` | create | 3 |
| `src/Cortex.Contained.Agent.Host/Recall/RecallPostConfigure.cs` | create | 4 |
| `src/Cortex.Contained.Agent.Host/Program.cs` | modify | 4, 9, 12 |
| `src/Cortex.Contained.Agent.Host/History/HistoryProjectorService.cs` | modify (replace) | 5 |
| `src/Cortex.Contained.Agent.Host/History/HistoryProjector.cs` | modify | 5 |
| `src/Cortex.Contained.Agent.Host/History/HistoryPurge.cs` | modify | 6, 8, 9 |
| `src/Cortex.Contained.Agent.Host/Storage/MessageStore.History.cs` | modify | 6, 7 |
| `src/Cortex.Contained.Agent.Host/Events/PurgeEvents.cs` | modify | 6 |
| `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs` | modify | 6, 9, 12 |
| `src/Cortex.Contained.Agent.Host/History/QuarantineWindow.cs` | modify | 7 |
| `src/Cortex.Contained.Agent.Host/Storage/MessageStore.Quarantine.cs` | modify (replace) | 7 |
| `src/Cortex.Contained.Agent.Host/Memory/MemoryQuarantine.cs` | modify (replace) | 7, 9 |
| `src/Cortex.Contained.Agent.Host/History/IHistorySource.cs` | modify | 8 |
| `src/Cortex.Contained.Agent.Host/Storage/MessageStore.Preview.cs` | create | 8 |
| `src/Cortex.Contained.Agent.Host/History/HistoryPurgeService.cs` | modify | 8, 9 |
| `src/Cortex.Contained.Agent.Host/History/ErasureTask.cs` | modify | 9 |
| `src/Cortex.Contained.Agent.Host/Memory/DependentMemoryErasureStep.cs` | create | 9 |
| `src/Cortex.Contained.Agent.Host/Memory/QuarantineReviewService.cs` | create | 10 |
| `src/Cortex.Contained.Agent.Host/Events/EventBackfillService.cs` | modify | 11 |
| `src/Cortex.Contained.Agent.Host/Recall/RecallStatusMapper.cs` | create | 11 |
| `src/Cortex.Contained.Agent.Host/Recall/RecallControlService.cs` | create | 12 |
| `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.Recall.cs` | create | 12 |
| `tests/Cortex.Contained.Agent.Host.Tests/History/HistoryHybridSearchTests.cs` | modify | 2 |
| `tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallSettingsStoreTests.cs` | create | 3 |
| `tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallPostConfigureTests.cs` | create | 4 |
| `tests/Cortex.Contained.Agent.Host.Tests/History/HistoryHostedServicesTests.cs` | modify | 5 |
| `tests/Cortex.Contained.Agent.Host.Tests/History/HistoryProjectorTests.cs` | modify | 5 |
| `tests/Cortex.Contained.Agent.Host.Tests/History/HistoryPurgeServiceTests.cs` | modify | 6 |
| `tests/Cortex.Contained.Agent.Host.Tests/History/HistoryPurgeTypesTests.cs` | modify | 6 |
| `tests/Cortex.Contained.Agent.Host.Tests/History/AgentHubHistoryTests.cs` | modify | 6, 9 |
| `tests/Cortex.Contained.Agent.Host.Tests/History/MemoryQuarantineTests.cs` | modify | 7 |
| `tests/Cortex.Contained.Agent.Host.Tests/History/HistoryPurgePreviewTests.cs` | create | 8 |
| `tests/Cortex.Contained.Agent.Host.Tests/History/DependentMemoryErasureTests.cs` | create | 9 |
| `tests/Cortex.Contained.Agent.Host.Tests/Recall/QuarantineReviewServiceTests.cs` | create | 10 |
| `tests/Cortex.Contained.Agent.Host.Tests/Events/EventBackfillServiceTests.cs` | modify | 11 |
| `tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallStatusMapperTests.cs` | create | 11 |
| `tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallControlServiceTests.cs` | create | 12 |
| `tests/Cortex.Contained.Agent.Host.Tests/Recall/AgentHubRecallTests.cs` | create | 12 |
| `src/Cortex.Contained.Bridge/Speech/IRerankerComposeRunner.cs` | modify (replace) | 13 |
| `src/Cortex.Contained.Bridge/Speech/DockerComposeCommandRunner.cs` | modify | 13 |
| `src/Cortex.Contained.Bridge/Speech/RerankerSidecarLifecycle.cs` | modify (replace) | 13 |
| `tests/Cortex.Contained.Bridge.Tests/Speech/RerankerSidecarLifecycleTests.cs` | modify (replace) | 13 |
| `src/Cortex.Contained.Bridge/RerankerConfigYamlWriter.cs` | create | 14 |
| `src/Cortex.Contained.Bridge/Setup/BridgeSettingsWriter.cs` | modify | 14 |
| `src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs` | modify | 14 |
| `src/Cortex.Contained.Bridge/Recall/RerankerSwitch.cs` | create | 14 |
| `src/Cortex.Contained.Bridge/Program.cs` | modify | 14, 16 |
| `tests/Cortex.Contained.Bridge.Tests/Recall/RerankerSwitchTests.cs` | create | 14 |
| `src/Cortex.Contained.Bridge/Recall/HistoryPurgeConfirmation.cs` | create | 15 |
| `src/Cortex.Contained.Bridge/Tenants/HistoryDeletionResponse.cs` | modify (replace) | 15 |
| `src/Cortex.Contained.Bridge/Tenants/TenantEndpoints.cs` | modify | 15 |
| `tests/Cortex.Contained.Bridge.Tests/HistoryDeletionResponseTests.cs` | modify (replace) | 15 |
| `tests/Cortex.Contained.Bridge.Tests/Recall/HistoryPurgeConfirmationTests.cs` | create | 15 |
| `src/Cortex.Contained.Bridge/Hub/HubClient.Recall.cs` | create | 16 |
| `src/Cortex.Contained.Bridge/Recall/IRecallAgentClient.cs` | create | 16 |
| `src/Cortex.Contained.Bridge/Endpoints/RecallEndpoints.cs` | create | 16 |
| `tests/Cortex.Contained.Bridge.Tests/Recall/RecallEndpointsTests.cs` | create | 16 |
| `src/Cortex.Contained.Bridge/wwwroot/js/components/layout.js` | modify | 17 |
| `src/Cortex.Contained.Bridge/wwwroot/js/components/recall-purge.js` | create | 17 |
| `src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-recall.js` | create | 17 |
| `src/Cortex.Contained.Bridge/wwwroot/app.html` | modify | 17, 18 |
| `src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-history.js` | modify | 18 |
| `src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-channel-history.js` | modify | 18 |
| `src/Cortex.Contained.Bridge/wwwroot/help-content/settings/recall-settings.md` | create | 19 |
| `src/Cortex.Contained.Bridge/wwwroot/help-content/manifest.json` | modify | 19 |
| `docs/api-reference.md` | modify | 19 |
| `docs/history-locator.md` | modify | 19 |
| `docs/reranker.md` | modify | 19 |
| `docs/setup-guide.md` | modify | 19 |
| `docs/superpowers/specs/2026-11-21-episodic-history-memory-design.md` | modify | 19 |

## Tasks overview

| # | Task | Model |
|---|---|---|
| 1 | Contracts: recall DTOs, `IRecallHub`, purge request/result fields | cheap |
| 2 | `History:DefaultSearchMode` (what `mode=auto` means) | cheap |
| 3 | `RecallSettingsStore`: persisted, validated overrides | standard |
| 4 | Options post-configure + change tokens (live settings) | standard |
| 5 | History on/off at runtime (projector loop + pruned-journal guard) | standard |
| 6 | Purge between dates (`From`) | standard |
| 7 | Quarantine windows record their channel; bulk keep; review helpers | standard |
| 8 | Purge preview | cheap |
| 9 | Per-purge dependent-memory deletion (`dependent-memories` erasure step) | standard |
| 10 | `QuarantineReviewService` (list with purge source, bulk keep/delete) | cheap |
| 11 | Event backfill status + `RecallStatusMapper` | cheap |
| 12 | `RecallControlService`, `IRecallHub` on `AgentHub`, DI | standard |
| 13 | Reranker sidecar: honest status (image probe, GPU hint) | cheap |
| 14 | Reranker switch: `cortex.yml` persistence + safe converge | standard |
| 15 | Purge confirmation wording + `dependentMemories` on existing DELETE endpoints | cheap |
| 16 | Bridge Recall endpoints + `HubClient` calls | standard |
| 17 | UI: Recall page (settings, reranker, status, delete history, needs review) | standard |
| 18 | UI: History/channel Clear modals use preview + choice; Memory page link | cheap |
| 19 | Docs: API reference, history locator, reranker, setup guide, help, master spec D3/D10 | cheap |
| 20 | Final verification | standard |

---

### Task 1: Contracts — recall DTOs, `IRecallHub`, purge request/result fields

**Files:**
- Create: `src/Cortex.Contained.Contracts/Hub/RecallTypes.cs`
- Create: `src/Cortex.Contained.Contracts/Hub/IRecallHub.cs`
- Modify (replace whole file): `src/Cortex.Contained.Contracts/Hub/HistoryPurgeTypes.cs`
- Test: `tests/Cortex.Contained.Contracts.Tests/Hub/RecallContractsTests.cs`

- [ ] **Step 1: Write the failing test** — create `tests/Cortex.Contained.Contracts.Tests/Hub/RecallContractsTests.cs`:

```csharp
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Contracts.Tests.Hub;

public sealed class RecallContractsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("review", true)]
    [InlineData("DELETE", true)]
    [InlineData("purge", false)]
    public void DependentMemoryActions_IsValid_AcceptsReviewDeleteOrEmpty(string? value, bool expected)
    {
        Assert.Equal(expected, DependentMemoryActions.IsValid(value));
    }

    [Fact]
    public void HistoryPurgeRequestDto_Default_ReviewsDependentMemories()
    {
        var request = new HistoryPurgeRequestDto();

        Assert.Equal(DependentMemoryActions.Review, request.DependentMemories);
        Assert.False(DependentMemoryActions.IsDelete(request.DependentMemories));
    }

    [Fact]
    public void RecallHub_Methods_UseSerializableDtos()
    {
        var keep = typeof(IRecallHub).GetMethod(nameof(IRecallHub.KeepQuarantinedMemories))!;
        var delete = typeof(IRecallHub).GetMethod(nameof(IRecallHub.DeleteQuarantinedMemories))!;

        Assert.Equal(typeof(Task<QuarantineDecisionResultDto>), keep.ReturnType);
        Assert.Equal([typeof(IReadOnlyList<string>)], delete.GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Equal(typeof(Task<HistoryPurgePreviewDto>), typeof(IRecallHub).GetMethod(nameof(IRecallHub.PreviewHistoryPurge))!.ReturnType);
    }
}
```

- [ ] **Step 2: Run it — expect a compile failure** (`DependentMemoryActions`, `IRecallHub` … do not exist):

```powershell
dotnet test tests\Cortex.Contained.Contracts.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~RecallContractsTests"
```

- [ ] **Step 3: Create `src/Cortex.Contained.Contracts/Hub/RecallTypes.cs`:**

```csharp
namespace Cortex.Contained.Contracts.Hub;

/// <summary>String values of <see cref="HistoryPurgeRequestDto.DependentMemories"/>.</summary>
public static class DependentMemoryActions
{
    /// <summary>Quarantine dependent memories for the owner's review (D10, the default).</summary>
    public const string Review = "review";

    /// <summary>Erase dependent memories physically as part of the purge (owner opt-in per purge).</summary>
    public const string Delete = "delete";

    /// <summary>True for <see cref="Review"/> or <see cref="Delete"/> (case-insensitive), or null/empty (review).</summary>
    public static bool IsValid(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value, Review, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Delete, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="value"/> asks for deletion.</summary>
    public static bool IsDelete(string? value) => string.Equals(value, Delete, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The effective recall settings of one agent (per tenant): configured defaults overlaid with the
/// owner's runtime choices persisted in the agent's state volume.
/// </summary>
public sealed record RecallSettingsDto
{
    /// <summary><c>History:Enabled</c>: projector running and recall tools exposed.</summary>
    public bool HistoryEnabled { get; init; }

    /// <summary>What <c>history_search mode=auto</c> means: <c>auto</c> / <c>hybrid</c> (hybrid when embeddings are available) or <c>keyword</c>.</summary>
    public string HistorySearchMode { get; init; } = "auto";

    /// <summary><c>History:Dense:Enabled</c>: chunks are embedded and hybrid search is possible.</summary>
    public bool HistoryDenseEnabled { get; init; }

    /// <summary><c>MemoryMcp:Search:DefaultMode</c>: <c>dense</c>, <c>hybrid</c> or <c>keyword</c>.</summary>
    public string MemorySearchMode { get; init; } = "dense";

    /// <summary><c>Reranker:History:TopN</c>.</summary>
    public int RerankHistoryTopN { get; init; }

    /// <summary><c>Reranker:History:MaxDocChars</c>.</summary>
    public int RerankHistoryMaxDocChars { get; init; }

    /// <summary><c>Reranker:Memory:TopN</c>.</summary>
    public int RerankMemoryTopN { get; init; }

    /// <summary><c>Reranker:Memory:MaxDocChars</c>.</summary>
    public int RerankMemoryMaxDocChars { get; init; }

    /// <summary><c>Reranker:TimeoutMs</c>.</summary>
    public int RerankTimeoutMs { get; init; }

    /// <summary>Names of the settings that carry a persisted runtime override (the rest are configured defaults).</summary>
    public IReadOnlyList<string> Overridden { get; init; } = [];
}

/// <summary>
/// A change to the recall settings. Null members are left unchanged; <see cref="ResetToDefaults"/>
/// first drops every persisted override (the configured defaults apply again).
/// </summary>
public sealed record RecallSettingsPatchDto
{
    /// <summary>Drop every persisted override before applying the other members.</summary>
    public bool ResetToDefaults { get; init; }

    public bool? HistoryEnabled { get; init; }

    public string? HistorySearchMode { get; init; }

    public bool? HistoryDenseEnabled { get; init; }

    public string? MemorySearchMode { get; init; }

    public int? RerankHistoryTopN { get; init; }

    public int? RerankHistoryMaxDocChars { get; init; }

    public int? RerankMemoryTopN { get; init; }

    public int? RerankMemoryMaxDocChars { get; init; }

    public int? RerankTimeoutMs { get; init; }
}

/// <summary>Outcome of <c>UpdateRecallSettings</c>.</summary>
public sealed record RecallSettingsUpdateResultDto
{
    /// <summary>False when validation failed; nothing was persisted.</summary>
    public bool Applied { get; init; }

    /// <summary>Validation errors (empty when applied).</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Consequences the owner should know about (for example an index rebuild).</summary>
    public IReadOnlyList<string> Notices { get; init; } = [];

    /// <summary>The effective settings after the update.</summary>
    public required RecallSettingsDto Settings { get; init; }
}

/// <summary>Live state of the recall stack of one agent.</summary>
public sealed record RecallStatusDto
{
    public required RecallSettingsDto Settings { get; init; }

    public required HistoryIndexStatusDto History { get; init; }

    public required DenseEmbeddingStatusDto Dense { get; init; }

    public required RerankerStatusDto Reranker { get; init; }

    public required EventBackfillStatusDto EventBackfill { get; init; }

    /// <summary>Curated memories currently hidden for review (D10).</summary>
    public int QuarantinedMemories { get; init; }
}

/// <summary>History index build progress (the <c>/health</c> history block).</summary>
public sealed record HistoryIndexStatusDto
{
    public bool Enabled { get; init; }

    public bool ToolsEnabled { get; init; }

    /// <summary><c>open</c>, <c>awaitingKey</c> or <c>failed</c>.</summary>
    public string Storage { get; init; } = "open";

    public long? Generation { get; init; }

    public bool BuildComplete { get; init; }

    public bool BuildInProgress { get; init; }

    public long CheckpointLag { get; init; }

    public long JournalBacklog { get; init; }

    public bool Halted { get; init; }

    public string? LastError { get; init; }

    public int PendingErasureTasks { get; init; }

    public IReadOnlyList<string> PendingErasureSteps { get; init; } = [];

    public int MaintenancePendingTasks { get; init; }
}

/// <summary>Dense embedding progress of the served history generation.</summary>
public sealed record DenseEmbeddingStatusDto
{
    /// <summary><c>off</c>, <c>on</c> or <c>unavailable: reason</c>.</summary>
    public string State { get; init; } = "off";

    public long Embedded { get; init; }

    public long Total { get; init; }

    public long Pending { get; init; }

    public long Failed { get; init; }

    public DateTimeOffset? OldestPendingQueuedAt { get; init; }

    public string? LastError { get; init; }
}

/// <summary>Agent-side reranker health (the Bridge adds the sidecar state).</summary>
public sealed record RerankerStatusDto
{
    /// <summary>Whether the Bridge granted the reranker.</summary>
    public bool Enabled { get; init; }

    /// <summary><c>off</c>, <c>ok</c>, <c>circuit-open</c>, <c>half-open</c> or <c>unavailable: reason</c>.</summary>
    public string State { get; init; } = "off";

    public int ConsecutiveFailures { get; init; }

    public DateTimeOffset? CircuitOpenUntil { get; init; }

    public string? LastError { get; init; }

    public double? LastLatencyMs { get; init; }

    public DateTimeOffset? LastCallAt { get; init; }
}

/// <summary>Event backfill state.</summary>
public sealed record EventBackfillStatusDto
{
    public bool Running { get; init; }

    public DateTimeOffset? LastRunAt { get; init; }

    public int? LastScanned { get; init; }

    public int? LastInserted { get; init; }

    public long? LastElapsedMs { get; init; }
}

/// <summary>What a history deletion would remove (shown before the owner confirms).</summary>
public sealed record HistoryPurgePreviewDto
{
    /// <summary>Messages the request would delete.</summary>
    public int Messages { get; init; }

    /// <summary>Curated memories created in the affected period (not already kept) — quarantined or deleted per the owner's choice.</summary>
    public int DependentMemories { get; init; }

    /// <summary>Oldest affected message, or null.</summary>
    public DateTimeOffset? RangeFrom { get; init; }

    /// <summary>Newest affected message, or null.</summary>
    public DateTimeOffset? RangeTo { get; init; }
}

/// <summary>One quarantined memory with the purge that quarantined it.</summary>
public sealed record QuarantinedMemoryDto
{
    public required MemoryItem Memory { get; init; }

    /// <summary>Erasure workflow id of the (latest) purge whose window contains the memory.</summary>
    public required string PurgeId { get; init; }

    /// <summary>When that purge ran.</summary>
    public DateTimeOffset PurgedAt { get; init; }

    /// <summary>The purged channel, or null for an all-channel purge.</summary>
    public string? PurgeChannelId { get; init; }

    /// <summary>Start of the quarantine window.</summary>
    public DateTimeOffset WindowFrom { get; init; }

    /// <summary>End of the quarantine window.</summary>
    public DateTimeOffset WindowTo { get; init; }
}

/// <summary>Quarantined memories awaiting review.</summary>
public sealed record QuarantineReviewListDto
{
    public required IReadOnlyList<QuarantinedMemoryDto> Items { get; init; }
}

/// <summary>Outcome of a bulk keep or delete of quarantined memories.</summary>
public sealed record QuarantineDecisionResultDto
{
    /// <summary>Memories kept or deleted.</summary>
    public int Affected { get; init; }

    /// <summary>Ids ignored because they were not quarantined (or no longer exist).</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];
}
```

- [ ] **Step 4: Create `src/Cortex.Contained.Contracts/Hub/IRecallHub.cs`** (not yet composed into `IAgentHub` — Task 12):

```csharp
namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Recall settings, status, purge preview and quarantine review exposed by the agent (the Bridge
/// "Recall" page). Part of the composed <see cref="IAgentHub"/> surface.
/// </summary>
public interface IRecallHub
{
    /// <summary>The effective recall settings (configured defaults plus persisted overrides).</summary>
    Task<RecallSettingsDto> GetRecallSettings();

    /// <summary>Validates, persists (agent state volume) and applies a settings change live.</summary>
    Task<RecallSettingsUpdateResultDto> UpdateRecallSettings(RecallSettingsPatchDto patch);

    /// <summary>History index, embedding, reranker, backfill and quarantine state.</summary>
    Task<RecallStatusDto> GetRecallStatus();

    /// <summary>Counts what <see cref="IHistoryHub.PurgeHistory"/> would delete for the same request; deletes nothing.</summary>
    Task<HistoryPurgePreviewDto> PreviewHistoryPurge(HistoryPurgeRequestDto request);

    /// <summary>Quarantined memories with the purge that quarantined each one.</summary>
    Task<QuarantineReviewListDto> ListQuarantineReview();

    /// <summary>Keeps the given quarantined memories (visible to the agent again). Ids not in quarantine are skipped.</summary>
    Task<QuarantineDecisionResultDto> KeepQuarantinedMemories(IReadOnlyList<string> memoryIds);

    /// <summary>Erases the given quarantined memories in one batch. Ids not in quarantine are skipped.</summary>
    Task<QuarantineDecisionResultDto> DeleteQuarantinedMemories(IReadOnlyList<string> memoryIds);
}
```

- [ ] **Step 5: Replace `src/Cortex.Contained.Contracts/Hub/HistoryPurgeTypes.cs`** (adds `From`, `DependentMemories`, `DeletedMemories`; existing members unchanged):

```csharp
namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// A history deletion request: one channel and/or a time range (<see cref="From"/> inclusive,
/// <see cref="OlderThan"/> exclusive), or everything.
/// </summary>
public sealed record HistoryPurgeRequestDto
{
    /// <summary>Only this channel; null for every channel.</summary>
    public string? ChannelId { get; init; }

    /// <summary>Only messages older than this instant; null for no upper bound.</summary>
    public DateTimeOffset? OlderThan { get; init; }

    /// <summary>Only messages at or after this instant; null for no lower bound.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>
    /// What happens to curated memories created in the purged period:
    /// <see cref="DependentMemoryActions.Review"/> (default, quarantine) or <see cref="DependentMemoryActions.Delete"/>.
    /// </summary>
    public string DependentMemories { get; init; } = DependentMemoryActions.Review;
}

/// <summary>Outcome of a history deletion, including the erasure status and the dependent-memory outcome.</summary>
public sealed record HistoryPurgeResultDto
{
    /// <summary>Messages deleted.</summary>
    public int Deleted { get; init; }

    /// <summary>True when every cleanup step (index, WAL, spill files, subagent records, evaluation artifacts, dependent memories) finished.</summary>
    public bool ErasureComplete { get; init; }

    /// <summary>Cleanup steps that will resume automatically.</summary>
    public IReadOnlyList<string> CleanupPending { get; init; } = [];

    /// <summary>Curated memories created in the deleted period that are now hidden until reviewed.</summary>
    public int QuarantinedMemories { get; init; }

    /// <summary>Curated memories created in the deleted period that were erased with the history (owner opt-in).</summary>
    public int DeletedMemories { get; init; }

    /// <summary>The dependent-memory action that was applied (<c>review</c> or <c>delete</c>).</summary>
    public string DependentMemories { get; init; } = DependentMemoryActions.Review;
}

/// <summary>Outcome of the explicit event backfill (legacy_import events for rows stored before the event log).</summary>
public sealed record EventBackfillResultDto
{
    /// <summary>Messages scanned.</summary>
    public int Scanned { get; init; }

    /// <summary>Messages that received a legacy_import event (0 on a repeated run).</summary>
    public int Inserted { get; init; }

    /// <summary>Duration of the maintenance window, in milliseconds.</summary>
    public long ElapsedMs { get; init; }
}
```

- [ ] **Step 6: Run the test — expect PASS (7 tests):** same command as Step 2.

- [ ] **Step 7: Commit**

```powershell
git add src/Cortex.Contained.Contracts/Hub/RecallTypes.cs src/Cortex.Contained.Contracts/Hub/IRecallHub.cs src/Cortex.Contained.Contracts/Hub/HistoryPurgeTypes.cs tests/Cortex.Contained.Contracts.Tests/Hub/RecallContractsTests.cs
git commit -m "feat(recall): add recall settings/status/review contracts and purge options" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 2: `History:DefaultSearchMode` (what `mode=auto` means)

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/History/HistoryOptions.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/HistoryRecallService.Hybrid.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/History/HistoryHybridSearchTests.cs`

- [ ] **Step 1: Write the failing tests** — in `HistoryHybridSearchTests.cs`, insert before `public async Task Hybrid_KeywordAndDenseAgree_MatchedByBothRanksFirst()` (keep its `[Fact]`):

```csharp
    [Fact]
    public async Task Auto_DefaultSearchModeKeyword_StaysKeywordWithoutEmbeddingTheQuery()
    {
        await this.SeedAsync();
        await this.EmbedAllAsync();
        this.h.Options.DefaultSearchMode = HistorySearchMode.Keyword;

        var result = await this.SearchAsync("railway");

        Assert.Equal(HistorySearchMode.Keyword, result.Mode);
        Assert.Equal("not used: mode=keyword (default)", result.Coverage.Dense);
        Assert.Empty(this.embedder.Queries);
    }

    [Fact]
    public async Task Hybrid_DefaultSearchModeKeyword_ExplicitHybridStillHybrid()
    {
        await this.SeedAsync();
        await this.EmbedAllAsync();
        this.h.Options.DefaultSearchMode = HistorySearchMode.Keyword;

        var result = await this.SearchAsync("train trip", HistorySearchMode.Hybrid);

        Assert.Equal(HistorySearchMode.Hybrid, result.Mode);
        Assert.Contains("railway journey", result.Hits[0].Lines[0].Text, StringComparison.Ordinal);
    }

```

- [ ] **Step 2: Run — expect a compile failure** (`DefaultSearchMode` missing):

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~HistoryHybridSearchTests"
```

- [ ] **Step 3: Edit `HistoryOptions.cs`** — insert before `/// <summary>Keyword index variant; part of the index fingerprint (§4.2 stemming ablation).</summary>`:

```csharp
    /// <summary>
    /// What <c>history_search mode=auto</c> (the default) means: <see cref="HistorySearchMode.Auto"/> or
    /// <see cref="HistorySearchMode.Hybrid"/> use hybrid search when embeddings are available,
    /// <see cref="HistorySearchMode.Keyword"/> keeps relevance searches keyword-only unless the caller asks
    /// for <c>mode=hybrid</c>. Applies at query time.
    /// </summary>
    public HistorySearchMode DefaultSearchMode { get; set; } = HistorySearchMode.Auto;

```

- [ ] **Step 4: Edit `HistoryRecallService.Hybrid.cs`** (`EmbedQueryAsync`) — replace:

```csharp
        if (request.Order == HistoryOrder.Time || request.Mode == HistorySearchMode.Keyword)
        {
            return (null, request.Order == HistoryOrder.Time ? "not used: order=time is exhaustive keyword" : "not used: mode=keyword");
        }
```

with:

```csharp
        if (request.Order == HistoryOrder.Time)
        {
            return (null, "not used: order=time is exhaustive keyword");
        }

        if (request.Mode == HistorySearchMode.Keyword)
        {
            return (null, "not used: mode=keyword");
        }

        if (request.Mode == HistorySearchMode.Auto && this.options.CurrentValue.DefaultSearchMode == HistorySearchMode.Keyword)
        {
            return (null, "not used: mode=keyword (default)");
        }
```

- [ ] **Step 5: Run — expect PASS** (same command; the whole class passes).

- [ ] **Step 6: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/History/HistoryOptions.cs src/Cortex.Contained.Agent.Host/History/HistoryRecallService.Hybrid.cs tests/Cortex.Contained.Agent.Host.Tests/History/HistoryHybridSearchTests.cs
git commit -m "feat(history): configurable default search mode for history_search mode=auto" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 3: `RecallSettingsStore` — persisted, validated overrides

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Recall/RecallOverrides.cs`
- Create: `src/Cortex.Contained.Agent.Host/Recall/RecallSettingsStore.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallSettingsStoreTests.cs`

- [ ] **Step 1: Write the failing tests** — create `tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallSettingsStoreTests.cs`:

```csharp
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Recall;
using Cortex.Contained.Contracts.Hub;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Recall;

public sealed class RecallSettingsStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "recall-settings-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(this.root, "recall-settings.json");

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* best effort */ }
    }

    private RecallSettingsStore NewStore() => new(this.FilePath, NullLogger<RecallSettingsStore>.Instance);

    [Fact]
    public void Current_NoFile_HasNoOverrides()
    {
        using var store = this.NewStore();

        Assert.Equal(RecallOverrides.None, store.Current);
        Assert.Empty(store.Current.Names());
    }

    [Fact]
    public void Apply_ValidPatch_PersistsAndSurvivesRestart()
    {
        using (var store = this.NewStore())
        {
            var result = store.Apply(new RecallSettingsPatchDto { HistoryEnabled = true, HistorySearchMode = "Hybrid", MemorySearchMode = "hybrid", RerankHistoryTopN = 20 });
            Assert.True(result.Applied);
        }

        using var reopened = this.NewStore();
        Assert.True(reopened.Current.HistoryEnabled);
        Assert.Equal(HistorySearchMode.Hybrid, reopened.Current.HistorySearchMode);
        Assert.Equal(MemorySearchMode.Hybrid, reopened.Current.MemorySearchMode);
        Assert.Equal(20, reopened.Current.RerankHistoryTopN);
        Assert.Contains("\"historySearchMode\": \"hybrid\"", File.ReadAllText(this.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_NullMembers_KeepEarlierOverrides()
    {
        using var store = this.NewStore();
        store.Apply(new RecallSettingsPatchDto { HistoryDenseEnabled = true });

        store.Apply(new RecallSettingsPatchDto { RerankTimeoutMs = 2500 });

        Assert.True(store.Current.HistoryDenseEnabled);
        Assert.Equal(2500, store.Current.RerankTimeoutMs);
    }

    [Fact]
    public void Apply_ResetToDefaults_DropsEveryOverrideThenAppliesThePatch()
    {
        using var store = this.NewStore();
        store.Apply(new RecallSettingsPatchDto { HistoryEnabled = true, HistoryDenseEnabled = true });

        store.Apply(new RecallSettingsPatchDto { ResetToDefaults = true, MemorySearchMode = "keyword" });

        Assert.Equal(["memorySearchMode"], store.Current.Names());
    }

    [Theory]
    [InlineData("semantic", null, 0)]
    [InlineData(null, "auto", 0)]
    [InlineData(null, null, 51)]
    [InlineData("2", null, 0)]
    public void Apply_InvalidValue_RejectedAndNothingPersisted(string? historyMode, string? memoryMode, int topN)
    {
        using var store = this.NewStore();

        var result = store.Apply(new RecallSettingsPatchDto
        {
            HistorySearchMode = historyMode,
            MemorySearchMode = memoryMode,
            RerankMemoryTopN = topN == 0 ? null : topN,
        });

        Assert.False(result.Applied);
        Assert.Single(result.Errors);
        Assert.False(File.Exists(this.FilePath));
    }

    [Fact]
    public void Apply_Applied_FiresTheChangeToken()
    {
        using var store = this.NewStore();
        var token = store.GetChangeToken();

        store.Apply(new RecallSettingsPatchDto { HistoryEnabled = false });

        Assert.True(token.HasChanged);
        Assert.False(store.GetChangeToken().HasChanged);
    }

    [Fact]
    public void Current_CorruptFile_FallsBackToNoOverrides()
    {
        Directory.CreateDirectory(this.root);
        File.WriteAllText(this.FilePath, "{ not json");

        using var store = this.NewStore();

        Assert.Equal(RecallOverrides.None, store.Current);
    }
}
```

- [ ] **Step 2: Run — expect a compile failure:**

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Recall.RecallSettingsStoreTests"
```

- [ ] **Step 3: Create `src/Cortex.Contained.Agent.Host/Recall/RecallOverrides.cs`:**

```csharp
using System.Globalization;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Contracts.Hub;
using MemoryMcp.Core.Configuration;

namespace Cortex.Contained.Agent.Host.Recall;

/// <summary>
/// The owner's persisted recall choices. A null member keeps the configured value
/// (<c>appsettings.json</c> / <c>History__*</c>, <c>Reranker__*</c>, <c>MemoryMcp__*</c> environment variables);
/// a set member overrides it at runtime and survives restarts and image upgrades (agent state volume).
/// </summary>
public sealed record RecallOverrides
{
    /// <summary>No overrides: the configured defaults apply.</summary>
    public static RecallOverrides None { get; } = new();

    public bool? HistoryEnabled { get; init; }

    public HistorySearchMode? HistorySearchMode { get; init; }

    public bool? HistoryDenseEnabled { get; init; }

    public MemorySearchMode? MemorySearchMode { get; init; }

    public int? RerankHistoryTopN { get; init; }

    public int? RerankHistoryMaxDocChars { get; init; }

    public int? RerankMemoryTopN { get; init; }

    public int? RerankMemoryMaxDocChars { get; init; }

    public int? RerankTimeoutMs { get; init; }

    /// <summary>camelCase names of the members that are set (reported as <c>overridden</c>).</summary>
    public IReadOnlyList<string> Names()
    {
        var names = new List<string>();
        Add(this.HistoryEnabled.HasValue, "historyEnabled");
        Add(this.HistorySearchMode.HasValue, "historySearchMode");
        Add(this.HistoryDenseEnabled.HasValue, "historyDenseEnabled");
        Add(this.MemorySearchMode.HasValue, "memorySearchMode");
        Add(this.RerankHistoryTopN.HasValue, "rerankHistoryTopN");
        Add(this.RerankHistoryMaxDocChars.HasValue, "rerankHistoryMaxDocChars");
        Add(this.RerankMemoryTopN.HasValue, "rerankMemoryTopN");
        Add(this.RerankMemoryMaxDocChars.HasValue, "rerankMemoryMaxDocChars");
        Add(this.RerankTimeoutMs.HasValue, "rerankTimeoutMs");
        return names;

        void Add(bool set, string name)
        {
            if (set)
            {
                names.Add(name);
            }
        }
    }

    /// <summary>Overlays the set members of <paramref name="patch"/> on these overrides.</summary>
    public RecallOverrides With(RecallOverrides patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return new RecallOverrides
        {
            HistoryEnabled = patch.HistoryEnabled ?? this.HistoryEnabled,
            HistorySearchMode = patch.HistorySearchMode ?? this.HistorySearchMode,
            HistoryDenseEnabled = patch.HistoryDenseEnabled ?? this.HistoryDenseEnabled,
            MemorySearchMode = patch.MemorySearchMode ?? this.MemorySearchMode,
            RerankHistoryTopN = patch.RerankHistoryTopN ?? this.RerankHistoryTopN,
            RerankHistoryMaxDocChars = patch.RerankHistoryMaxDocChars ?? this.RerankHistoryMaxDocChars,
            RerankMemoryTopN = patch.RerankMemoryTopN ?? this.RerankMemoryTopN,
            RerankMemoryMaxDocChars = patch.RerankMemoryMaxDocChars ?? this.RerankMemoryMaxDocChars,
            RerankTimeoutMs = patch.RerankTimeoutMs ?? this.RerankTimeoutMs,
        };
    }
}

/// <summary>Parses and validates a <see cref="RecallSettingsPatchDto"/> (the same bounds the options enforce at startup).</summary>
public static class RecallSettingsValidation
{
    /// <summary>Smallest accepted <c>Reranker:TimeoutMs</c>.</summary>
    public const int MinTimeoutMs = 100;

    /// <summary>Largest accepted <c>Reranker:TimeoutMs</c>.</summary>
    public const int MaxTimeoutMs = 30_000;

    /// <summary>Converts <paramref name="patch"/> into overrides; returns the validation errors (empty when valid).</summary>
    public static IReadOnlyList<string> TryParse(RecallSettingsPatchDto patch, out RecallOverrides parsed)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var errors = new List<string>();
        HistorySearchMode? historyMode = null;
        if (patch.HistorySearchMode is { } h)
        {
            if (TryParseHistoryMode(h, out var mode))
            {
                historyMode = mode;
            }
            else
            {
                errors.Add("historySearchMode must be auto, hybrid or keyword.");
            }
        }

        MemorySearchMode? memoryMode = null;
        if (patch.MemorySearchMode is { } m)
        {
            if (TryParseMemoryMode(m, out var mode))
            {
                memoryMode = mode;
            }
            else
            {
                errors.Add("memorySearchMode must be dense, hybrid or keyword.");
            }
        }

        Range(patch.RerankHistoryTopN, RerankBudget.MinTopN, RerankBudget.MaxTopN, "rerankHistoryTopN");
        Range(patch.RerankMemoryTopN, RerankBudget.MinTopN, RerankBudget.MaxTopN, "rerankMemoryTopN");
        Range(patch.RerankHistoryMaxDocChars, RerankBudget.MinDocChars, RerankBudget.MaxDocCharsLimit, "rerankHistoryMaxDocChars");
        Range(patch.RerankMemoryMaxDocChars, RerankBudget.MinDocChars, RerankBudget.MaxDocCharsLimit, "rerankMemoryMaxDocChars");
        Range(patch.RerankTimeoutMs, MinTimeoutMs, MaxTimeoutMs, "rerankTimeoutMs");

        parsed = new RecallOverrides
        {
            HistoryEnabled = patch.HistoryEnabled,
            HistorySearchMode = historyMode,
            HistoryDenseEnabled = patch.HistoryDenseEnabled,
            MemorySearchMode = memoryMode,
            RerankHistoryTopN = patch.RerankHistoryTopN,
            RerankHistoryMaxDocChars = patch.RerankHistoryMaxDocChars,
            RerankMemoryTopN = patch.RerankMemoryTopN,
            RerankMemoryMaxDocChars = patch.RerankMemoryMaxDocChars,
            RerankTimeoutMs = patch.RerankTimeoutMs,
        };
        return errors;

        void Range(int? value, int min, int max, string name)
        {
            if (value is { } v && (v < min || v > max))
            {
                errors.Add(string.Create(CultureInfo.InvariantCulture, $"{name} must be in [{min}, {max}]."));
            }
        }
    }

    /// <summary>Parses <c>auto</c>, <c>hybrid</c> or <c>keyword</c> (case-insensitive).</summary>
    public static bool TryParseHistoryMode(string value, out HistorySearchMode mode)
    {
        mode = HistorySearchMode.Auto;
        return value is not null
            && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            && Enum.TryParse(value, ignoreCase: true, out mode)
            && Enum.IsDefined(mode);
    }

    /// <summary>Parses <c>dense</c>, <c>hybrid</c> or <c>keyword</c> (case-insensitive).</summary>
    public static bool TryParseMemoryMode(string value, out MemorySearchMode mode)
    {
        mode = MemorySearchMode.Dense;
        return value is not null
            && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            && Enum.TryParse(value, ignoreCase: true, out mode)
            && Enum.IsDefined(mode);
    }

    /// <summary>The lower-case wire form of a mode.</summary>
    public static string Text<TEnum>(TEnum mode)
        where TEnum : struct, Enum =>
        mode.ToString().ToLowerInvariant();
}
```

- [ ] **Step 4: Create `src/Cortex.Contained.Agent.Host/Recall/RecallSettingsStore.cs`:**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Primitives;

namespace Cortex.Contained.Agent.Host.Recall;

/// <summary>Outcome of <see cref="RecallSettingsStore.Apply"/>.</summary>
/// <param name="Applied">False when validation or persistence failed; the overrides are unchanged.</param>
/// <param name="Errors">Validation or persistence errors.</param>
/// <param name="Previous">Overrides before the update.</param>
/// <param name="Current">Overrides after the update.</param>
public sealed record RecallSettingsApplyResult(bool Applied, IReadOnlyList<string> Errors, RecallOverrides Previous, RecallOverrides Current);

/// <summary>
/// File-backed runtime overrides for the recall stack (<c>&lt;state&gt;/recall-settings.json</c>, one per
/// agent container = per tenant). It lives in the state volume, outside the tool sandbox, so the agent
/// cannot change it and Self-Update, image upgrades and the Launcher's compose-file sync keep it.
/// Option post-configures read <see cref="Current"/>; <see cref="GetChangeToken"/> fires on every
/// applied update so <c>IOptionsMonitor&lt;T&gt;.CurrentValue</c> picks the change up live.
/// A missing or unreadable file means no overrides.
/// </summary>
public sealed partial class RecallSettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string filePath;
    private readonly ILogger<RecallSettingsStore> logger;
    private readonly Lock syncLock = new();
    private RecallOverrides current;
    private CancellationTokenSource changeTokenSource = new();

    public RecallSettingsStore(string filePath, ILogger<RecallSettingsStore> logger)
    {
        this.filePath = filePath;
        this.logger = logger;
        this.current = this.Load();
    }

    /// <summary>The persisted overrides.</summary>
    public RecallOverrides Current
    {
        get
        {
            lock (this.syncLock)
            {
                return this.current;
            }
        }
    }

    /// <summary>Validates <paramref name="patch"/>, persists the merged overrides atomically and signals the change.</summary>
    public RecallSettingsApplyResult Apply(RecallSettingsPatchDto patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var errors = RecallSettingsValidation.TryParse(patch, out var parsed);
        CancellationTokenSource? fired = null;
        RecallSettingsApplyResult result;
        lock (this.syncLock)
        {
            var previous = this.current;
            if (errors.Count > 0)
            {
                return new RecallSettingsApplyResult(false, errors, previous, previous);
            }

            var next = (patch.ResetToDefaults ? RecallOverrides.None : previous).With(parsed);
            try
            {
                this.Persist(next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                this.LogWriteFailed(this.filePath, ex.Message);
                return new RecallSettingsApplyResult(false, [$"Could not save the recall settings: {ex.Message}"], previous, previous);
            }

            this.current = next;
            fired = this.changeTokenSource;
            this.changeTokenSource = new CancellationTokenSource();
            result = new RecallSettingsApplyResult(true, [], previous, next);
        }

        fired.Cancel();
        fired.Dispose();
        var overridden = string.Join(", ", result.Current.Names());
        this.LogApplied(overridden);
        return result;
    }

    /// <summary>A token that fires when the overrides change.</summary>
    public IChangeToken GetChangeToken()
    {
        lock (this.syncLock)
        {
            return new CancellationChangeToken(this.changeTokenSource.Token);
        }
    }

    /// <inheritdoc />
    public void Dispose() => this.changeTokenSource.Dispose();

    private RecallOverrides Load()
    {
        try
        {
            if (File.Exists(this.filePath))
            {
                return JsonSerializer.Deserialize<RecallOverrides>(File.ReadAllText(this.filePath), json) ?? RecallOverrides.None;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            this.LogReadFailed(this.filePath, ex.Message);
        }

        return RecallOverrides.None;
    }

    private void Persist(RecallOverrides overrides)
    {
        var directory = Path.GetDirectoryName(this.filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = this.filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(overrides, json));
        File.Move(temp, this.filePath, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "[recall-settings] read failed for {Path}: {Reason}; using the configured defaults")]
    private partial void LogReadFailed(string path, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "[recall-settings] write failed for {Path}: {Reason}")]
    private partial void LogWriteFailed(string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "[recall-settings] applied; overridden: {Overridden}")]
    private partial void LogApplied(string overridden);
}
```

- [ ] **Step 5: Run — expect PASS (10 tests).**

- [ ] **Step 6: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Recall/RecallOverrides.cs src/Cortex.Contained.Agent.Host/Recall/RecallSettingsStore.cs tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallSettingsStoreTests.cs
git commit -m "feat(recall): persist validated recall overrides in the agent state volume" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 4: Options post-configure + change tokens (live settings)

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Recall/RecallPostConfigure.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Program.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallPostConfigureTests.cs`

- [ ] **Step 1: Write the failing tests** — create `tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallPostConfigureTests.cs`:

```csharp
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Recall;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Contracts.Hub;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Recall;

public sealed class RecallPostConfigureTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "recall-options-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider provider;

    public RecallPostConfigureTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.Configure<HistoryOptions>(o => o.TimeZone = "UTC");
        services.Configure<RerankerOptions>(_ => { });
        services.Configure<MemoryMcpOptions>(_ => { });
        services.AddRecallSettings(this.root);
        this.provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        this.provider.Dispose();
        try { Directory.Delete(this.root, recursive: true); } catch { /* best effort */ }
    }

    private RecallSettingsStore Store => this.provider.GetRequiredService<RecallSettingsStore>();

    [Fact]
    public void CurrentValue_NoOverrides_KeepsConfiguredDefaults()
    {
        var history = this.provider.GetRequiredService<IOptionsMonitor<HistoryOptions>>().CurrentValue;
        var memory = this.provider.GetRequiredService<IOptionsMonitor<MemoryMcpOptions>>().CurrentValue;

        Assert.False(history.Enabled);
        Assert.False(history.Dense.Enabled);
        Assert.Equal(HistorySearchMode.Auto, history.DefaultSearchMode);
        Assert.Equal(MemorySearchMode.Dense, memory.Search.DefaultMode);
    }

    [Fact]
    public void Apply_UpdatesEveryMonitorLive()
    {
        var history = this.provider.GetRequiredService<IOptionsMonitor<HistoryOptions>>();
        var reranker = this.provider.GetRequiredService<IOptionsMonitor<RerankerOptions>>();
        var memory = this.provider.GetRequiredService<IOptionsMonitor<MemoryMcpOptions>>();
        _ = history.CurrentValue;
        _ = reranker.CurrentValue;
        _ = memory.CurrentValue;

        this.Store.Apply(new RecallSettingsPatchDto
        {
            HistoryEnabled = true,
            HistoryDenseEnabled = true,
            HistorySearchMode = "keyword",
            MemorySearchMode = "hybrid",
            RerankHistoryTopN = 10,
            RerankMemoryMaxDocChars = 800,
            RerankTimeoutMs = 4000,
        });

        Assert.True(history.CurrentValue.Enabled);
        Assert.True(history.CurrentValue.Dense.Enabled);
        Assert.Equal(HistorySearchMode.Keyword, history.CurrentValue.DefaultSearchMode);
        Assert.Equal("UTC", history.CurrentValue.TimeZone);
        Assert.Equal(MemorySearchMode.Hybrid, memory.CurrentValue.Search.DefaultMode);
        Assert.Equal(10, reranker.CurrentValue.History.TopN);
        Assert.Equal(800, reranker.CurrentValue.Memory.MaxDocChars);
        Assert.Equal(4000, reranker.CurrentValue.TimeoutMs);
        Assert.Empty(reranker.CurrentValue.Validate());
    }

    [Fact]
    public void Apply_Reset_RestoresConfiguredDefaults()
    {
        var history = this.provider.GetRequiredService<IOptionsMonitor<HistoryOptions>>();
        this.Store.Apply(new RecallSettingsPatchDto { HistoryEnabled = true });
        Assert.True(history.CurrentValue.Enabled);

        this.Store.Apply(new RecallSettingsPatchDto { ResetToDefaults = true });

        Assert.False(history.CurrentValue.Enabled);
    }
}
```

- [ ] **Step 2: Run — expect a compile failure** (`AddRecallSettings` missing):

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Recall.RecallPostConfigureTests"
```

- [ ] **Step 3: Create `src/Cortex.Contained.Agent.Host/Recall/RecallPostConfigure.cs`:**

```csharp
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Reranking;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Cortex.Contained.Agent.Host.Recall;

/// <summary>Invalidates <c>IOptionsMonitor&lt;T&gt;</c> whenever <see cref="RecallSettingsStore"/> applies an update.</summary>
public sealed class RecallSettingsChangeTokenSource<T>(RecallSettingsStore store) : IOptionsChangeTokenSource<T>
{
    /// <inheritdoc />
    public string Name => Options.DefaultName;

    /// <inheritdoc />
    public IChangeToken GetChangeToken() => store.GetChangeToken();
}

/// <summary>Applies the recall overrides to <see cref="HistoryOptions"/> (<c>Enabled</c>, <c>DefaultSearchMode</c>, <c>Dense:Enabled</c>).</summary>
public sealed class RecallHistoryPostConfigure(RecallSettingsStore store) : IPostConfigureOptions<HistoryOptions>
{
    /// <inheritdoc />
    public void PostConfigure(string? name, HistoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var overrides = store.Current;
        options.Enabled = overrides.HistoryEnabled ?? options.Enabled;
        options.DefaultSearchMode = overrides.HistorySearchMode ?? options.DefaultSearchMode;
        options.Dense.Enabled = overrides.HistoryDenseEnabled ?? options.Dense.Enabled;
    }
}

/// <summary>Applies the recall overrides to the agent-side <see cref="RerankerOptions"/> budgets and timeout.</summary>
public sealed class RecallRerankerPostConfigure(RecallSettingsStore store) : IPostConfigureOptions<RerankerOptions>
{
    /// <inheritdoc />
    public void PostConfigure(string? name, RerankerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var overrides = store.Current;
        options.History.TopN = overrides.RerankHistoryTopN ?? options.History.TopN;
        options.History.MaxDocChars = overrides.RerankHistoryMaxDocChars ?? options.History.MaxDocChars;
        options.Memory.TopN = overrides.RerankMemoryTopN ?? options.Memory.TopN;
        options.Memory.MaxDocChars = overrides.RerankMemoryMaxDocChars ?? options.Memory.MaxDocChars;
        options.TimeoutMs = overrides.RerankTimeoutMs ?? options.TimeoutMs;
    }
}

/// <summary>Applies the recall override of <c>MemoryMcp:Search:DefaultMode</c>.</summary>
public sealed class RecallMemorySearchPostConfigure(RecallSettingsStore store) : IPostConfigureOptions<MemoryMcpOptions>
{
    /// <inheritdoc />
    public void PostConfigure(string? name, MemoryMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Search.DefaultMode = store.Current.MemorySearchMode ?? options.Search.DefaultMode;
    }
}

/// <summary>Registers the recall settings store, its post-configures and change-token sources.</summary>
public static class RecallSettingsRegistration
{
    /// <summary>File name of the overrides in the agent state directory.</summary>
    public const string FileName = "recall-settings.json";

    /// <summary>Adds <see cref="RecallSettingsStore"/> at <c>&lt;stateRoot&gt;/recall-settings.json</c> and wires it into the options pipeline.</summary>
    public static IServiceCollection AddRecallSettings(this IServiceCollection services, string stateRoot)
    {
        services.AddSingleton(sp => new RecallSettingsStore(
            Path.Combine(stateRoot, FileName),
            sp.GetRequiredService<ILogger<RecallSettingsStore>>()));
        services.AddSingleton<IPostConfigureOptions<HistoryOptions>, RecallHistoryPostConfigure>();
        services.AddSingleton<IPostConfigureOptions<RerankerOptions>, RecallRerankerPostConfigure>();
        services.AddSingleton<IPostConfigureOptions<MemoryMcpOptions>, RecallMemorySearchPostConfigure>();
        services.AddSingleton<IOptionsChangeTokenSource<HistoryOptions>, RecallSettingsChangeTokenSource<HistoryOptions>>();
        services.AddSingleton<IOptionsChangeTokenSource<RerankerOptions>, RecallSettingsChangeTokenSource<RerankerOptions>>();
        services.AddSingleton<IOptionsChangeTokenSource<MemoryMcpOptions>, RecallSettingsChangeTokenSource<MemoryMcpOptions>>();
        return services;
    }
}
```

- [ ] **Step 4: Edit `src/Cortex.Contained.Agent.Host/Program.cs`** — replace

```csharp
builder.Services.Configure<HistoryOptions>(builder.Configuration.GetSection(HistoryOptions.SectionName));
```

with

```csharp
builder.Services.Configure<HistoryOptions>(builder.Configuration.GetSection(HistoryOptions.SectionName));
// Recall settings (owner's runtime overrides from the Bridge "Recall" page): <state>/recall-settings.json,
// applied over History, Reranker and MemoryMcp:Search options via post-configure + change token.
Cortex.Contained.Agent.Host.Recall.RecallSettingsRegistration.AddRecallSettings(builder.Services, stateRoot);
```

(`stateRoot` is defined earlier: `builder.Configuration["CORTEX_STATE_PATH"] ?? "/app/state"`.)

- [ ] **Step 5: Run — expect PASS (3 tests).** Also build the host: `dotnet build src\Cortex.Contained.Agent.Host -p:NuGetAudit=false` → 0 warnings.

- [ ] **Step 6: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Recall/RecallPostConfigure.cs src/Cortex.Contained.Agent.Host/Program.cs tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallPostConfigureTests.cs
git commit -m "feat(recall): apply recall overrides live through options post-configure" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---
### Task 5: History on/off at runtime (projector loop + pruned-journal guard)

**Files:**
- Modify (replace whole file): `src/Cortex.Contained.Agent.Host/History/HistoryProjectorService.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/HistoryProjector.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/History/HistoryHostedServicesTests.cs`, `tests/Cortex.Contained.Agent.Host.Tests/History/HistoryProjectorTests.cs`

- [ ] **Step 1: Write the failing tests.** In `HistoryHostedServicesTests.cs`, **replace** the test `ProjectorService_Disabled_DoesNothing` (whole method incl. `[Fact]`) with:

```csharp
    [Fact]
    public async Task ProjectorService_Disabled_IdlesWithoutBuilding()
    {
        this.h.Options = new HistoryOptions { Enabled = false };
        var service = new HistoryProjectorService(this.h.Projector, this.h.Status, this.h.Monitor, NullLogger<HistoryProjectorService>.Instance, TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        Assert.False(service.ExecuteTask!.IsCompleted);
        Assert.False(this.h.Projector.IsInitialized);
        Assert.Empty(this.h.Generations.List());
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ProjectorService_EnabledAtRuntime_BuildsIndex()
    {
        await this.h.SayAsync("kayak trip", 0);
        this.h.Options = new HistoryOptions { Enabled = false, TimeZone = "UTC" };
        var service = new HistoryProjectorService(this.h.Projector, this.h.Status, this.h.Monitor, NullLogger<HistoryProjectorService>.Instance, TimeSpan.FromMilliseconds(10));
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        this.h.Options = new HistoryOptions { Enabled = true, TimeZone = "UTC" };
        await WaitUntilAsync(() => this.h.Status.Snapshot().BuildComplete);

        await service.StopAsync(CancellationToken.None);
        Assert.True(this.h.Status.Snapshot().BuildComplete);
    }

    [Fact]
    public async Task ProjectorService_DisabledThenReenabledAfterJournalPrune_RebuildsNewGeneration()
    {
        await this.h.SayAsync("kayak trip", 0);
        var service = new HistoryProjectorService(this.h.Projector, this.h.Status, this.h.Monitor, NullLogger<HistoryProjectorService>.Instance, TimeSpan.FromMilliseconds(10));
        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => this.h.Status.Snapshot().Generation == 1);

        this.h.Options = new HistoryOptions { Enabled = false, TimeZone = "UTC" };
        await Task.Delay(100);
        await this.h.SayAsync("canoe lake", 1);
        await using (var snapshot = await this.h.Store.OpenSnapshotAsync(CancellationToken.None))
        {
            // What HistoryMaintenanceService does while History is off.
            await this.h.Store.PruneJournalAsync((await snapshot.ReadMetaAsync(CancellationToken.None)).JournalHead, CancellationToken.None);
        }

        this.h.Options = new HistoryOptions { Enabled = true, TimeZone = "UTC" };
        await WaitUntilAsync(() => this.h.Status.Snapshot() is { Generation: 2, BuildComplete: true });

        await service.StopAsync(CancellationToken.None);
        using var reader = this.h.Projector.Current!.OpenReader();
        Assert.Equal(1, reader.DocumentFrequency("canoe"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }
```

In `HistoryProjectorTests.cs`, insert after `Restart_CheckpointBelowPrunedThrough_Rebuilds`:

```csharp
    [Fact]
    public async Task ApplyPendingAsync_JournalPrunedPastCheckpointWhileRunning_RequiresRebuild()
    {
        await this.h.SayAsync("a", 0);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        await this.h.SayAsync("b", 1);
        await using (var snapshot = await this.h.Store.OpenSnapshotAsync(CancellationToken.None))
        {
            var head = (await snapshot.ReadMetaAsync(CancellationToken.None)).JournalHead;
            await this.h.Store.PruneJournalAsync(head, CancellationToken.None);
        }

        Assert.Equal(ProjectorApplyResult.RebuildRequired, await this.h.Projector.ApplyPendingAsync(CancellationToken.None));
    }
```

- [ ] **Step 2: Run — expect FAILS:** `ProjectorService_Disabled_IdlesWithoutBuilding` (the old service returns at once), `…EnabledAtRuntime…` (never builds), `…JournalPrunedPastCheckpoint…` (returns `Applied`):

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~HistoryHostedServicesTests|FullyQualifiedName~HistoryProjectorTests"
```

- [ ] **Step 3: Edit `HistoryProjector.cs`** (`ApplyJournalAsync`) — replace

```csharp
        if (!string.Equals(meta.DbInstanceId, state.SourceInstanceId, StringComparison.Ordinal)
            || meta.RestoreGeneration != state.SourceRestoreGeneration
            || !string.Equals(state.ConfigFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return false;
        }
```

with

```csharp
        if (!string.Equals(meta.DbInstanceId, state.SourceInstanceId, StringComparison.Ordinal)
            || meta.RestoreGeneration != state.SourceRestoreGeneration
            || !string.Equals(state.ConfigFingerprint, fingerprint, StringComparison.Ordinal)
            || state.CheckpointJournalSeq < meta.JournalPrunedThrough)
        {
            // The last condition: the journal was pruned past this index (History was switched off at
            // runtime and maintenance dropped the unconsumed journal), so it can no longer catch up.
            return false;
        }
```

(A build database starts at `CheckpointJournalSeq = JournalHead` (its watermark) and pruning never passes `min(current checkpoint, watermark)`, so normal operation is unaffected.)

- [ ] **Step 4: Replace `src/Cortex.Contained.Agent.Host/History/HistoryProjectorService.cs`:**

```csharp
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Hosted loop around <see cref="HistoryProjector"/> (§9.5): initialize, rebuild when needed, then
/// apply the journal about once a second. Three consecutive failures halt the projector with a
/// health warning; it never skips a batch. Recall keeps working through verification and the
/// unindexed tail while halted.
/// <para>
/// With dense retrieval on, each tick then runs embedding rounds. An embedder failure never halts the
/// projector: it is reported as <see cref="HistoryStatusSnapshot.LastEmbeddingError"/> and embedding
/// pauses for <see cref="EmbeddingBackoffTicks"/> ticks while keyword search keeps working.
/// </para>
/// </summary>
public sealed partial class HistoryProjectorService : BackgroundService
{
    private const int MaxConsecutiveFailures = 3;
   private const int MaxEmbeddingRoundsPerTick = 8;

   /// <summary>Ticks skipped after an embedder failure.</summary>
   internal const int EmbeddingBackoffTicks = 30;
    private readonly HistoryProjector projector;
    private readonly HistoryStatus status;
    private readonly IOptionsMonitor<HistoryOptions> options;
    private readonly ILogger<HistoryProjectorService> logger;
    private readonly TimeSpan pollInterval;
    private readonly Storage.IStorageState? storage;

    public HistoryProjectorService(
        HistoryProjector projector,
        HistoryStatus status,
        IOptionsMonitor<HistoryOptions> options,
        ILogger<HistoryProjectorService> logger,
        Storage.IStorageState? storage = null)
        : this(projector, status, options, logger, TimeSpan.FromSeconds(1), storage)
    {
    }

    internal HistoryProjectorService(
        HistoryProjector projector,
        HistoryStatus status,
        IOptionsMonitor<HistoryOptions> options,
        ILogger<HistoryProjectorService> logger,
        TimeSpan pollInterval,
        Storage.IStorageState? storage = null)
    {
        this.storage = storage;
        this.projector = projector;
        this.status = status;
        this.options = options;
        this.logger = logger;
        this.pollInterval = pollInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // History:Enabled is read every tick, so the Recall settings page can switch it at runtime:
        // switched on, the startup checks run and the projector resumes or rebuilds; switched off, the
        // loop idles (recall tools are hidden by HistoryToolsSwitch; the index stays until the next erasure
        // pass deletes it, and maintenance prunes the unconsumed journal, which forces a rebuild on re-enable).
        var running = false;
        var reportedDisabled = false;
        var failures = 0;
        var embeddingBackoff = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = this.options.CurrentValue;
            if (!settings.Enabled)
            {
                if (running)
                {
                    this.LogPaused();
                    running = false;
                }
                else if (!reportedDisabled)
                {
                    this.LogDisabled();
                }

                reportedDisabled = true;
                if (!await this.DelayAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            if (!running)
            {
                var start = await this.TryStartAsync(settings, stoppingToken).ConfigureAwait(false);
                if (start != StartOutcome.Started)
                {
                    return;
                }

                running = true;
                failures = 0;
                embeddingBackoff = 0;
            }

            try
            {
                if (!this.projector.IsInitialized)
                {
                    await this.projector.InitializeAsync(stoppingToken).ConfigureAwait(false);
                }

                if (this.projector.NeedsRebuild)
                {
                    await this.projector.RebuildAsync(stoppingToken).ConfigureAwait(false);
                }
                else if (await this.projector.ApplyPendingAsync(stoppingToken).ConfigureAwait(false) == ProjectorApplyResult.RebuildRequired)
                {
                    await this.projector.RetireCurrentAsync(writeLeaseHeld: false, stoppingToken).ConfigureAwait(false);
                }

                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                this.status.Update(s => s with { LastError = ex.Message });
                this.LogBatchFailed(failures, ex.Message);
                if (failures >= MaxConsecutiveFailures)
                {
                    this.status.Update(s => s with { Halted = true });
                    this.LogHalted(ex.Message);
                    return;
                }
            }

            if (failures == 0 && !this.projector.NeedsRebuild)
            {
                embeddingBackoff = await this.EmbedAsync(embeddingBackoff, stoppingToken).ConfigureAwait(false);
            }

            if (!await this.DelayAsync(stoppingToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task<StartOutcome> TryStartAsync(HistoryOptions settings, CancellationToken stoppingToken)
    {
        var problems = settings.Validate().ToList();
        var capabilities = SqliteRuntimeInfo.Capabilities;
        if (!capabilities.SupportsHistoryIndex)
        {
            problems.Add($"SQLite {capabilities.Version} cannot host the history index: {capabilities.Error}");
        }

        if (problems.Count > 0)
        {
            var message = string.Join(" ", problems);
            this.status.Update(s => s with { Halted = true, LastError = message });
            this.LogHalted(message);
            return StartOutcome.Halted;
        }

        if (this.storage is not null)
        {
            // Encrypted storage opens only once the Bridge pushed the key; wait rather than burn retries.
            try
            {
                await this.storage.WhenOpenAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return StartOutcome.Stopped;
            }
            catch (Exception ex) when (ex is Storage.StorageKeyMismatchException or Storage.StorageLockedException)
            {
                this.status.Update(s => s with { Halted = true, LastError = ex.Message });
                this.LogHalted(ex.Message);
                return StartOutcome.Halted;
            }
        }

        this.LogStarted();
        return StartOutcome.Started;
    }

    private async Task<bool> DelayAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(this.pollInterval, stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
    private async Task<int> EmbedAsync(int backoff, CancellationToken stoppingToken)
    {
        if (backoff > 0)
        {
            return backoff - 1;
        }

        try
        {
            for (var round = 0; round < MaxEmbeddingRoundsPerTick; round++)
            {
                var result = await this.projector.EmbedPendingAsync(stoppingToken).ConfigureAwait(false);
                this.status.Update(s => s with { LastEmbeddingError = null });
                if (result.Published == 0)
                {
                    break;
                }
            }

            return 0;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            this.status.Update(s => s with { LastEmbeddingError = ex.Message });
            this.LogEmbeddingFailed(ex.Message);
            return EmbeddingBackoffTicks;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "History locator disabled (History:Enabled=false); projector idle until it is enabled")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "History locator enabled; projector started")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "History locator switched off at runtime; projector paused")]
    private partial void LogPaused();

    [LoggerMessage(Level = LogLevel.Warning, Message = "History projector batch failed (attempt {Attempt}): {Error}")]
    private partial void LogBatchFailed(int attempt, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "History projector halted: {Error}")]
    private partial void LogHalted(string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "History embedding failed; keyword search continues and embedding retries later: {Error}")]
    private partial void LogEmbeddingFailed(string error);

    private enum StartOutcome
    {
        Started,
        Halted,
        Stopped,
    }
}
```

- [ ] **Step 5: Run — expect PASS** (same command), then the other users of the service:

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~StorageReadinessTests|FullyQualifiedName~HistoryEmbeddingServiceTests"
```

- [ ] **Step 6: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/History/HistoryProjectorService.cs src/Cortex.Contained.Agent.Host/History/HistoryProjector.cs tests/Cortex.Contained.Agent.Host.Tests/History/HistoryHostedServicesTests.cs tests/Cortex.Contained.Agent.Host.Tests/History/HistoryProjectorTests.cs
git commit -m "feat(history): switch the history locator on and off at runtime" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 6: Purge between dates (`From`)

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/History/HistoryPurge.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Storage/MessageStore.History.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Events/PurgeEvents.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs`
- Test: `HistoryPurgeServiceTests.cs`, `HistoryPurgeTypesTests.cs`, `AgentHubHistoryTests.cs` (all in `tests/Cortex.Contained.Agent.Host.Tests/History/`)

- [ ] **Step 1: Write the failing tests.**

`HistoryPurgeServiceTests.cs` — insert before `PurgeAsync_Channel_MarkersGoneFromEveryFileAndVocabulary` (keep its `[Fact]`):

```csharp
    [Fact]
    public async Task PurgeAsync_BetweenDates_DeletesOnlyRowsInRange()
    {
        await this.h.SayAsync("before", 0);
        await this.h.SayAsync("inside", 60);
        await this.h.SayAsync("after", 120);

        var result = await this.purge.PurgeAsync(
            new HistoryPurgeScope(null, OlderThan: HistoryHarness.T0.AddMinutes(90), From: HistoryHarness.T0.AddMinutes(30)), null, CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        var left = await this.h.Store.GetMessagesAsync("ch");
        Assert.Equal(["after", "before"], left.Select(m => m.Content).Order(StringComparer.Ordinal).ToArray());
    }

```

`HistoryPurgeTypesTests.cs` — insert before `Contains_IsHalfOpen` (keep its `[Fact]`):

```csharp
    [Fact]
    public void Problem_FromNotBeforeOlderThan_ReportsInvertedRange()
    {
        Assert.NotNull(new HistoryPurgeScope(null, OlderThan: T0, From: T0).Problem());
        Assert.Null(new HistoryPurgeScope(null, OlderThan: T0.AddDays(1), From: T0).Problem());
        Assert.Null(new HistoryPurgeScope("ch", null, From: T0).Problem());
    }

```

`AgentHubHistoryTests.cs` — insert after `PurgeHistory_ReturnsErasureStatus`:

```csharp
    [Fact]
    public async Task PurgeHistory_BetweenDates_DeletesOnlyTheRange()
    {
        await this.h.SayAsync("a", 0);
        await this.h.SayAsync("b", 60);

        var result = await this.hub.PurgeHistory(new HistoryPurgeRequestDto { From = HistoryHarness.T0.AddMinutes(30), OlderThan = HistoryHarness.T0.AddDays(1) });

        Assert.Equal(1, result.Deleted);
        Assert.Equal("a", Assert.Single(await this.h.Store.GetMessagesAsync("ch")).Content);
    }

    [Fact]
    public async Task PurgeHistory_InvertedRange_RejectedWithoutDeleting()
    {
        await this.h.SayAsync("a", 0);

        await Assert.ThrowsAsync<HubException>(() => this.hub.PurgeHistory(new HistoryPurgeRequestDto { From = HistoryHarness.T0, OlderThan = HistoryHarness.T0 }));

        Assert.Single(await this.h.Store.GetMessagesAsync("ch"));
    }
```

- [ ] **Step 2: Run — expect a compile failure** (`From`, `Problem()`):

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~HistoryPurgeServiceTests|FullyQualifiedName~HistoryPurgeTypesTests|FullyQualifiedName~AgentHubHistoryTests"
```

- [ ] **Step 3: Edit `HistoryPurge.cs`** — replace the `HistoryPurgeScope` record (with its doc comment) by:

```csharp
/// <summary>Which rows a purge deletes. Null members are unbounded.</summary>
/// <param name="ChannelId">Restrict to one channel, or null for every channel.</param>
/// <param name="OlderThan">Delete rows with <c>Timestamp &lt; OlderThan</c>, or null for no upper bound.</param>
/// <param name="From">Delete rows with <c>Timestamp &gt;= From</c>, or null for no lower bound (a "between dates" purge sets both).</param>
public sealed record HistoryPurgeScope(string? ChannelId, DateTimeOffset? OlderThan, DateTimeOffset? From = null)
{
    /// <summary>Every row.</summary>
    public static HistoryPurgeScope All { get; } = new(null, null);

    /// <summary>True when only one channel is affected.</summary>
    public bool IsChannelScoped => this.ChannelId is not null;

    /// <summary>Null when the scope is usable; otherwise why not (an empty or inverted time range).</summary>
    public string? Problem() =>
        this.From is { } from && this.OlderThan is { } olderThan && from >= olderThan
            ? "The start of the range must be before its end."
            : null;
}
```

- [ ] **Step 4: Edit `MessageStore.History.cs`.** In `AppendPurgeAsync`, replace

```csharp
            scope.ChannelId,
            null,
            null,
            scope.OlderThan is { } olderThan ? SqliteDateTimeText.Format(olderThan) : null,
```

with

```csharp
            scope.ChannelId,
            null,
            scope.From is { } from ? SqliteDateTimeText.Format(from) : null,
            scope.OlderThan is { } olderThan ? SqliteDateTimeText.Format(olderThan) : null,
```

In `PurgeFilter`, after the `OlderThan` clause add

```csharp
        if (scope.From is not null)
        {
            clauses.Add($"{timeColumn} >= @from");
        }
```

and in its bind lambda, after the `@olderThan` parameter add

```csharp
            if (scope.From is { } from)
            {
                cmd.Parameters.AddWithValue("@from", SqliteDateTimeText.Format(from));
            }
```

(`PurgeFilter` also drives the event deletion, so events of the range go with the rows.)

- [ ] **Step 5: Edit `Events/PurgeEvents.cs`** — after the `olderThan` payload block add:

```csharp
        if (scope.From is { } from)
        {
            payload["from"] = SqliteDateTimeText.Format(from);
        }
```

- [ ] **Step 6: Edit `Hubs/AgentHub.cs`** (`PurgeHistory`) — replace

```csharp
        ArgumentNullException.ThrowIfNull(request);
        var result = await this.historyPurge.PurgeAsync(
            new HistoryPurgeScope(string.IsNullOrWhiteSpace(request.ChannelId) ? null : request.ChannelId, request.OlderThan),
            null,
            Context.ConnectionAborted).ConfigureAwait(false);
```

with

```csharp
        ArgumentNullException.ThrowIfNull(request);
        var scope = new HistoryPurgeScope(string.IsNullOrWhiteSpace(request.ChannelId) ? null : request.ChannelId, request.OlderThan, request.From);
        if (scope.Problem() is { } problem)
        {
            throw new HubException(problem);
        }

        var result = await this.historyPurge.PurgeAsync(
            scope,
            null,
            Context.ConnectionAborted).ConfigureAwait(false);
```

- [ ] **Step 7: Run — expect PASS** (same command), plus `--filter "FullyQualifiedName~EventErasureTests|FullyQualifiedName~MessageStoreJournalTests"`.

- [ ] **Step 8: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/History/HistoryPurge.cs src/Cortex.Contained.Agent.Host/Storage/MessageStore.History.cs src/Cortex.Contained.Agent.Host/Events/PurgeEvents.cs src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs tests/Cortex.Contained.Agent.Host.Tests/History/HistoryPurgeServiceTests.cs tests/Cortex.Contained.Agent.Host.Tests/History/HistoryPurgeTypesTests.cs tests/Cortex.Contained.Agent.Host.Tests/History/AgentHubHistoryTests.cs
git commit -m "feat(history): purge a time range between two dates" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 7: Quarantine windows record their channel; bulk keep; review helpers

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/History/QuarantineWindow.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Storage/MessageStore.History.cs`
- Modify (replace whole file): `src/Cortex.Contained.Agent.Host/Storage/MessageStore.Quarantine.cs`
- Modify (replace whole file): `src/Cortex.Contained.Agent.Host/Memory/MemoryQuarantine.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/History/MemoryQuarantineTests.cs`

- [ ] **Step 1: Write the failing tests** — in `MemoryQuarantineTests.cs`, append after `CountInWindowsAsync_ReportsAffectedMemories` (inside the class):

```csharp
    [Fact]
    public async Task ListAsync_AfterPurge_ReportsThePurgeAndItsChannel()
    {
        await this.PurgeOneConversationAsync();

        var listed = await this.quarantine.ListAsync(CancellationToken.None);

        Assert.Equal(["slack", "during"], listed.Select(q => q.MemoryId));
        Assert.All(listed, q => Assert.Equal("ch", q.Source.ChannelId));
        Assert.All(listed, q => Assert.Equal(T0.AddDays(2), q.Source.PurgedAt));
    }

    [Fact]
    public async Task InErasureAsync_OnlyThatPurgesWindows()
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "a", T0);
        await this.store.SaveMessageAsync("u", "other", "user", "b", T0.AddDays(3));
        var first = new MessagePurgeRequest(new HistoryPurgeScope("ch", null)) { Now = T0.AddDays(5) };
        await this.store.PurgeAsync(first, CancellationToken.None);
        await this.store.PurgeAsync(new MessagePurgeRequest(new HistoryPurgeScope("other", null)) { Now = T0.AddDays(5) }, CancellationToken.None);

        Assert.Equal(["during", "slack"], (await this.quarantine.InErasureAsync(first.ErasureId, CancellationToken.None)).Order());
        Assert.Equal(3, (await this.quarantine.GetQuarantinedIdsAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task KeepManyAsync_KeepsQuarantinedAndSkipsTheRest()
    {
        await this.PurgeOneConversationAsync();

        var (kept, skipped) = await this.quarantine.KeepManyAsync(["during", "slack", "after", "during"], CancellationToken.None);

        Assert.Equal(["during", "slack"], kept);
        Assert.Equal(["after"], skipped);
        Assert.Empty(await this.quarantine.GetQuarantinedIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadQuarantineWindowRecordsAsync_DatabaseFromBeforeChannelColumn_IsMigrated()
    {
        var legacyDir = Path.Combine(Path.GetTempPath(), $"quarantine-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(legacyDir);
        var path = Path.Combine(legacyDir, "messages.db");
        try
        {
            using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                legacy.Open();
                using var cmd = legacy.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE MemoryQuarantineWindows (Id INTEGER PRIMARY KEY AUTOINCREMENT, ErasureId TEXT NOT NULL, FromTs TEXT NOT NULL, ToTs TEXT NOT NULL, CreatedAt TEXT NOT NULL);
                    INSERT INTO MemoryQuarantineWindows (ErasureId, FromTs, ToTs, CreatedAt) VALUES ('e1', '2026-05-04 08:00:00', '2026-05-05 08:00:00', '2026-05-06 08:00:00');
                    """;
                cmd.ExecuteNonQuery();
            }

            await using var migrated = new MessageStore(path, NullLogger<MessageStore>.Instance);
            var record = Assert.Single(await migrated.ReadQuarantineWindowRecordsAsync(CancellationToken.None));

            Assert.Equal("e1", record.ErasureId);
            Assert.Null(record.ChannelId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(legacyDir, recursive: true); } catch { /* best effort */ }
        }
    }
```

- [ ] **Step 2: Run — expect a compile failure:**

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~MemoryQuarantineTests"
```

- [ ] **Step 3: Edit `QuarantineWindow.cs`** — after the `QuarantineWindow` record add:

```csharp
/// <summary>A stored quarantine window with the purge that recorded it.</summary>
/// <param name="ErasureId">Erasure workflow id of the purge.</param>
/// <param name="Window">The window.</param>
/// <param name="PurgedAt">When the purge ran.</param>
/// <param name="ChannelId">The purged channel, or null for an all-channel purge (and windows recorded before this column existed).</param>
public sealed record QuarantineWindowRecord(string ErasureId, QuarantineWindow Window, DateTimeOffset PurgedAt, string? ChannelId);
```

- [ ] **Step 4: Edit `MessageStore.History.cs`.**
  1. In `InitializeHistorySchema`, change the `MemoryQuarantineWindows` table so its last column line reads `CreatedAt TEXT NOT NULL,` followed by a new line `ChannelId TEXT NULL` (new databases).
  2. At the end of `InitializeHistorySchema` (after `cmd.ExecuteNonQuery();`) add the migration for existing databases:

```csharp

        // Purged channel per quarantine window (Recall review list); null for all-channel purges and older rows.
        if (!this.ColumnExists("MemoryQuarantineWindows", "ChannelId"))
        {
            using var alter = this.writer.CreateCommand();
            alter.CommandText = "ALTER TABLE MemoryQuarantineWindows ADD COLUMN ChannelId TEXT NULL;";
            alter.ExecuteNonQuery();
        }
```

  3. In `PurgeAsync`, replace the window insert

```csharp
                    await using var insert = this.WriterCommand(transaction, "INSERT INTO MemoryQuarantineWindows (ErasureId, FromTs, ToTs, CreatedAt) VALUES (@id, @from, @to, @at);");
                    insert.Parameters.AddWithValue("@id", request.ErasureId);
```

with

```csharp
                    await using var insert = this.WriterCommand(transaction, "INSERT INTO MemoryQuarantineWindows (ErasureId, FromTs, ToTs, CreatedAt, ChannelId) VALUES (@id, @from, @to, @at, @channelId);");
                    insert.Parameters.AddWithValue("@id", request.ErasureId);
                    insert.Parameters.AddWithValue("@channelId", (object?)request.Scope.ChannelId ?? DBNull.Value);
```

- [ ] **Step 5: Replace `src/Cortex.Contained.Agent.Host/Storage/MessageStore.Quarantine.cs`:**

```csharp
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Memory;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>D10 quarantine tables in <c>messages.db</c> (windows are written by the purge transaction).</summary>
public sealed partial class MessageStore : IMemoryQuarantineSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<QuarantineWindow>> ReadQuarantineWindowsAsync(CancellationToken cancellationToken)
    {
        var windows = new List<QuarantineWindow>();
        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT FromTs, ToTs FROM MemoryQuarantineWindows ORDER BY FromTs;";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            windows.Add(new QuarantineWindow(SqliteDateTimeText.Parse(reader.GetString(0)), SqliteDateTimeText.Parse(reader.GetString(1))));
        }

        return windows;
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> ReadKeptMemoryIdsAsync(CancellationToken cancellationToken)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MemoryId FROM MemoryQuarantineDecisions WHERE Decision = 'kept';";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            kept.Add(reader.GetString(0));
        }

        return kept;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QuarantineWindowRecord>> ReadQuarantineWindowRecordsAsync(CancellationToken cancellationToken)
    {
        var records = new List<QuarantineWindowRecord>();
        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ErasureId, FromTs, ToTs, CreatedAt, ChannelId FROM MemoryQuarantineWindows ORDER BY FromTs;";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new QuarantineWindowRecord(
                reader.GetString(0),
                new QuarantineWindow(SqliteDateTimeText.Parse(reader.GetString(1)), SqliteDateTimeText.Parse(reader.GetString(2))),
                SqliteDateTimeText.Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return records;
    }

    /// <inheritdoc />
    public Task RecordKeptMemoriesAsync(IReadOnlyCollection<string> memoryIds, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memoryIds);
        return this.WriteAsync(
            async transaction =>
            {
                foreach (var memoryId in memoryIds)
                {
                    await using var cmd = this.WriterCommand(transaction, """
                        INSERT INTO MemoryQuarantineDecisions (MemoryId, Decision, DecidedAt) VALUES (@id, 'kept', @at)
                        ON CONFLICT(MemoryId) DO UPDATE SET Decision = 'kept', DecidedAt = excluded.DecidedAt;
                        """);
                    cmd.Parameters.AddWithValue("@id", memoryId);
                    cmd.Parameters.AddWithValue("@at", SqliteDateTimeText.Format(at));
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task RecordKeptMemoryAsync(string memoryId, DateTimeOffset at, CancellationToken cancellationToken) =>
        this.RecordKeptMemoriesAsync([memoryId], at, cancellationToken);
}
```

- [ ] **Step 6: Replace `src/Cortex.Contained.Agent.Host/Memory/MemoryQuarantine.cs`** (adds the two source members, `QuarantinedMemory`, `ListAsync`, `InErasureAsync`, `KeepManyAsync`; the class summary is amended in Task 9):

```csharp
using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>Quarantine windows and owner decisions stored in <c>messages.db</c> (D10).</summary>
public interface IMemoryQuarantineSource
{
    /// <summary>Every recorded quarantine window.</summary>
    Task<IReadOnlyList<QuarantineWindow>> ReadQuarantineWindowsAsync(CancellationToken cancellationToken);

    /// <summary>Memories the owner chose to keep after review.</summary>
    Task<IReadOnlySet<string>> ReadKeptMemoryIdsAsync(CancellationToken cancellationToken);

    /// <summary>Records that the owner kept <paramref name="memoryId"/>.</summary>
    Task RecordKeptMemoryAsync(string memoryId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Every recorded quarantine window with the purge that recorded it.</summary>
    Task<IReadOnlyList<QuarantineWindowRecord>> ReadQuarantineWindowRecordsAsync(CancellationToken cancellationToken);

    /// <summary>Records that the owner kept every memory in <paramref name="memoryIds"/> (one transaction).</summary>
    Task RecordKeptMemoriesAsync(IReadOnlyCollection<string> memoryIds, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>A quarantined memory and the purge that quarantined it.</summary>
public sealed record QuarantinedMemory(string MemoryId, DateTimeOffset CreatedAt, QuarantineWindowRecord Source);

/// <summary>Creation time of one curated memory.</summary>
public sealed record MemoryCreationTime(string MemoryId, DateTimeOffset CreatedAt);

/// <summary>Lists the creation time of every curated memory.</summary>
public interface IMemoryCreationTimes
{
    /// <summary>All memories with their <c>CreatedAt</c>.</summary>
    Task<IReadOnlyList<MemoryCreationTime>> ListCreationTimesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// D10: curated memories whose <c>CreatedAt</c> falls in a
/// purged time window are quarantined — hidden from <c>memory_search</c>, subagent seeding,
/// consolidation and compaction until the owner keeps or deletes them. Never auto-deleted.
/// Time-window quarantine remains the mechanism; recording which events a memory was extracted
/// from (event provenance for memories) is a possible future refinement.
/// </summary>
public sealed class MemoryQuarantine
{
    private readonly IMemoryQuarantineSource source;
    private readonly IMemoryCreationTimes memories;
    private readonly TimeProvider time;

    public MemoryQuarantine(IMemoryQuarantineSource source, IMemoryCreationTimes memories, TimeProvider time)
    {
        this.source = source;
        this.memories = memories;
        this.time = time;
    }

    /// <summary>Ids of memories currently quarantined.</summary>
    public async Task<IReadOnlySet<string>> GetQuarantinedIdsAsync(CancellationToken cancellationToken)
    {
        var windows = await this.source.ReadQuarantineWindowsAsync(cancellationToken).ConfigureAwait(false);
        if (windows.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return await this.InWindowsAsync(windows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How many not-kept memories fall in <paramref name="windows"/> (for the delete confirmation).</summary>
    public async Task<int> CountInWindowsAsync(IReadOnlyList<QuarantineWindow> windows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return windows.Count == 0 ? 0 : (await this.InWindowsAsync(windows, cancellationToken).ConfigureAwait(false)).Count;
    }

    /// <summary>The owner keeps <paramref name="memoryId"/>: it leaves quarantine for good.</summary>
    public Task KeepAsync(string memoryId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryId);
        return this.source.RecordKeptMemoryAsync(memoryId, this.time.GetUtcNow(), cancellationToken);
    }

    /// <summary>Quarantined memories with the latest purge whose window contains each one (the Recall review list).</summary>
    public async Task<IReadOnlyList<QuarantinedMemory>> ListAsync(CancellationToken cancellationToken)
    {
        var records = await this.source.ReadQuarantineWindowRecordsAsync(cancellationToken).ConfigureAwait(false);
        if (records.Count == 0)
        {
            return [];
        }

        var kept = await this.source.ReadKeptMemoryIdsAsync(cancellationToken).ConfigureAwait(false);
        var all = await this.memories.ListCreationTimesAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<QuarantinedMemory>();
        foreach (var memory in all.Where(m => !kept.Contains(m.MemoryId)))
        {
            var source = records
                .Where(r => r.Window.Contains(memory.CreatedAt))
                .OrderByDescending(r => r.PurgedAt)
                .FirstOrDefault();
            if (source is not null)
            {
                result.Add(new QuarantinedMemory(memory.MemoryId, memory.CreatedAt, source));
            }
        }

        return result.OrderByDescending(q => q.CreatedAt).ThenBy(q => q.MemoryId, StringComparer.Ordinal).ToList();
    }

    /// <summary>Not-kept memories inside the windows recorded by purge <paramref name="erasureId"/>.</summary>
    public async Task<IReadOnlySet<string>> InErasureAsync(string erasureId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(erasureId);
        var windows = (await this.source.ReadQuarantineWindowRecordsAsync(cancellationToken).ConfigureAwait(false))
            .Where(r => string.Equals(r.ErasureId, erasureId, StringComparison.Ordinal))
            .Select(r => r.Window)
            .ToList();
        return windows.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : await this.InWindowsAsync(windows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Keeps the quarantined members of <paramref name="memoryIds"/> in one transaction; the others are returned as skipped.</summary>
    public async Task<(IReadOnlyList<string> Kept, IReadOnlyList<string> Skipped)> KeepManyAsync(IReadOnlyCollection<string> memoryIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memoryIds);
        var quarantined = await this.GetQuarantinedIdsAsync(cancellationToken).ConfigureAwait(false);
        var requested = memoryIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        var keep = requested.Where(quarantined.Contains).ToList();
        var skipped = requested.Where(id => !quarantined.Contains(id)).ToList();
        if (keep.Count > 0)
        {
            await this.source.RecordKeptMemoriesAsync(keep, this.time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }

        return (keep, skipped);
    }

    private async Task<IReadOnlySet<string>> InWindowsAsync(IReadOnlyList<QuarantineWindow> windows, CancellationToken cancellationToken)
    {
        var kept = await this.source.ReadKeptMemoryIdsAsync(cancellationToken).ConfigureAwait(false);
        var all = await this.memories.ListCreationTimesAsync(cancellationToken).ConfigureAwait(false);
        return all
            .Where(m => !kept.Contains(m.MemoryId) && windows.Any(w => w.Contains(m.CreatedAt)))
            .Select(m => m.MemoryId)
            .ToHashSet(StringComparer.Ordinal);
    }
}
```

- [ ] **Step 7: Run — expect PASS** (same command), plus `--filter "FullyQualifiedName~QuarantineAware|FullyQualifiedName~MemoryCompactionServiceTests"` (they substitute `IMemoryQuarantineSource`).

- [ ] **Step 8: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/History/QuarantineWindow.cs src/Cortex.Contained.Agent.Host/Storage/MessageStore.History.cs src/Cortex.Contained.Agent.Host/Storage/MessageStore.Quarantine.cs src/Cortex.Contained.Agent.Host/Memory/MemoryQuarantine.cs tests/Cortex.Contained.Agent.Host.Tests/History/MemoryQuarantineTests.cs
git commit -m "feat(memory): record the purged channel per quarantine window; bulk keep" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 8: Purge preview

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/History/IHistorySource.cs`
- Create: `src/Cortex.Contained.Agent.Host/Storage/MessageStore.Preview.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/HistoryPurge.cs`, `src/Cortex.Contained.Agent.Host/History/HistoryPurgeService.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/History/HistoryPurgePreviewTests.cs`

- [ ] **Step 1: Write the failing tests** — create `tests/Cortex.Contained.Agent.Host.Tests/History/HistoryPurgePreviewTests.cs`:

```csharp
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>The purge preview counts exactly what the purge then deletes and quarantines, and deletes nothing itself.</summary>
public sealed class HistoryPurgePreviewTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = HistoryHarness.T0;

    private readonly HistoryHarness h = new();
    private readonly IMemoryCreationTimes times = Substitute.For<IMemoryCreationTimes>();
    private readonly HistoryPurgeService purge;

    public HistoryPurgePreviewTests()
    {
        this.times.ListCreationTimesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new MemoryCreationTime("during", T0.AddHours(2)),
            new MemoryCreationTime("later", T0.AddDays(10)),
        ]);
        var clock = new FakeTimeProvider(T0.AddDays(2));
        this.purge = new HistoryPurgeService(
            this.h.Store, this.h.Gate, this.h.Projector, [new MessagesWalErasureStep(this.h.Store)],
            this.h.Monitor, clock, NullLogger<HistoryPurgeService>.Instance,
            new MemoryQuarantine(this.h.Store, this.times, clock));
    }

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    [Fact]
    public async Task PreviewAsync_MatchesThePurgeAndDeletesNothing()
    {
        await this.h.SayAsync("a", 0, channel: "x");
        await this.h.SayAsync("b", 60, channel: "x");
        await this.h.SayAsync("c", 0, channel: "y");
        var scope = new HistoryPurgeScope("x", null);

        var preview = await this.purge.PreviewAsync(scope, CancellationToken.None);

        Assert.Equal(new HistoryPurgePreview(2, 1, T0, T0.AddMinutes(60)), preview);
        Assert.Equal(2, (await this.h.Store.GetMessagesAsync("x")).Count);
        var result = await this.purge.PurgeAsync(scope, null, CancellationToken.None);
        Assert.Equal(preview.Messages, result.Deleted);
        Assert.Equal(preview.DependentMemories, result.QuarantinedMemories);
    }

    [Fact]
    public async Task PreviewAsync_NothingInScope_ReportsZero()
    {
        await this.h.SayAsync("a", 0, channel: "x");

        var preview = await this.purge.PreviewAsync(new HistoryPurgeScope("y", null), CancellationToken.None);

        Assert.Equal(new HistoryPurgePreview(0, 0, null, null), preview);
    }
}
```

- [ ] **Step 2: Run — expect a compile failure:**

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~HistoryPurgePreviewTests"
```

- [ ] **Step 3: Edit `IHistorySource.cs`** — in `IHistoryErasureStore`, after `ReadLastVacuumAsync` add:

```csharp

    /// <summary>Ascending timestamps of the rows a purge of <paramref name="scope"/> would delete (read-only; the purge preview).</summary>
    Task<IReadOnlyList<DateTimeOffset>> ReadPurgeTimestampsAsync(HistoryPurgeScope scope, CancellationToken cancellationToken);
```

- [ ] **Step 4: Create `src/Cortex.Contained.Agent.Host/Storage/MessageStore.Preview.cs`:**

```csharp
using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>Read-only purge preview: what a purge of a scope would delete (Recall page confirmation).</summary>
public sealed partial class MessageStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DateTimeOffset>> ReadPurgeTimestampsAsync(HistoryPurgeScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var (where, bind) = PurgeFilter(scope, "Timestamp");
        var timestamps = new List<DateTimeOffset>();
        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT Timestamp FROM Messages WHERE {where} ORDER BY Timestamp;";
        bind(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            timestamps.Add(SqliteDateTimeText.Parse(reader.GetString(0)));
        }

        return timestamps;
    }
}
```

- [ ] **Step 5: Edit `HistoryPurge.cs`** — insert before `/// <summary>Input to the single purge transaction in <c>MessageStore.PurgeAsync</c>.</summary>`:

```csharp
/// <summary>What a purge would delete (shown before the owner confirms).</summary>
/// <param name="Messages">Rows the purge would delete.</param>
/// <param name="DependentMemories">Not-kept curated memories inside the purge's quarantine windows.</param>
/// <param name="RangeFrom">Oldest affected row, or null.</param>
/// <param name="RangeTo">Newest affected row, or null.</param>
public sealed record HistoryPurgePreview(int Messages, int DependentMemories, DateTimeOffset? RangeFrom, DateTimeOffset? RangeTo);

```

- [ ] **Step 6: Edit `HistoryPurgeService.cs`** — insert before `/// <summary>Resumes every pending erasure step (maintenance tick and startup). Returns what is still pending.</summary>`:

```csharp
    /// <summary>
    /// Counts what <see cref="PurgeAsync"/> would delete for <paramref name="scope"/> now: the messages and the
    /// not-kept curated memories its quarantine windows would cover (the dependent memories). Deletes nothing.
    /// </summary>
    public async Task<HistoryPurgePreview> PreviewAsync(HistoryPurgeScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var settings = this.options.CurrentValue;
        var timestamps = await this.store.ReadPurgeTimestampsAsync(scope, cancellationToken).ConfigureAwait(false);
        var windows = QuarantineWindowBuilder.Build(
            timestamps,
            TimeSpan.FromHours(settings.QuarantineClusterGapHours),
            TimeSpan.FromHours(settings.QuarantineExtractionSlackHours),
            this.time.GetUtcNow());
        var dependent = this.quarantine is null ? 0 : await this.quarantine.CountInWindowsAsync(windows, cancellationToken).ConfigureAwait(false);
        return new HistoryPurgePreview(
            timestamps.Count,
            dependent,
            timestamps.Count > 0 ? timestamps[0] : null,
            timestamps.Count > 0 ? timestamps[^1] : null);
    }

```

- [ ] **Step 7: Run — expect PASS (2 tests).**

- [ ] **Step 8: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/History/IHistorySource.cs src/Cortex.Contained.Agent.Host/Storage/MessageStore.Preview.cs src/Cortex.Contained.Agent.Host/History/HistoryPurge.cs src/Cortex.Contained.Agent.Host/History/HistoryPurgeService.cs tests/Cortex.Contained.Agent.Host.Tests/History/HistoryPurgePreviewTests.cs
git commit -m "feat(history): preview what a purge deletes, including dependent memories" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 9: Per-purge dependent-memory deletion (`dependent-memories` erasure step)

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/History/HistoryPurge.cs`, `ErasureTask.cs`, `HistoryPurgeService.cs`
- Create: `src/Cortex.Contained.Agent.Host/Memory/DependentMemoryErasureStep.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Memory/MemoryQuarantine.cs` (summary), `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs`, `src/Cortex.Contained.Agent.Host/Program.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/History/DependentMemoryErasureTests.cs`, `AgentHubHistoryTests.cs`

- [ ] **Step 1: Write the failing tests** — create `tests/Cortex.Contained.Agent.Host.Tests/History/DependentMemoryErasureTests.cs`:

```csharp
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>The owner's per-purge choice to erase dependent memories instead of quarantining them.</summary>
public sealed class DependentMemoryErasureTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = HistoryHarness.T0;

    private readonly HistoryHarness h = new();
    private readonly IMemoryCreationTimes times = Substitute.For<IMemoryCreationTimes>();
    private readonly IMemoryManagementService memories = Substitute.For<IMemoryManagementService>();
    private readonly MemoryQuarantine quarantine;
    private readonly HistoryPurgeService purge;

    public DependentMemoryErasureTests()
    {
        this.times.ListCreationTimesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new MemoryCreationTime("during", T0.AddHours(2)),
            new MemoryCreationTime("kept", T0.AddHours(3)),
            new MemoryCreationTime("later", T0.AddDays(10)),
        ]);
        var clock = new FakeTimeProvider(T0.AddDays(2));
        this.quarantine = new MemoryQuarantine(this.h.Store, this.times, clock);
        this.purge = new HistoryPurgeService(
            this.h.Store, this.h.Gate, this.h.Projector,
            [new MessagesWalErasureStep(this.h.Store), new DependentMemoryErasureStep(this.quarantine, this.memories)],
            this.h.Monitor, clock, NullLogger<HistoryPurgeService>.Instance, this.quarantine);
    }

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    private async Task SeedAsync()
    {
        await this.h.SayAsync("a", 0, channel: "x");
        await this.h.SayAsync("b", 60, channel: "x");

        // Kept after an earlier purge of the same period: never deleted by a later one.
        await this.h.Store.RecordKeptMemoryAsync("kept", T0, CancellationToken.None);
    }

    [Fact]
    public async Task PurgeAsync_Delete_ErasesDependentMemoriesInOneBatch()
    {
        await this.SeedAsync();

        var result = await this.purge.PurgeAsync(new HistoryPurgeScope("x", null), null, CancellationToken.None, dependentMemories: DependentMemoryAction.Delete);

        await this.memories.Received(1).DeleteManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.Count == 1 && ids.Contains("during")), Arg.Any<CancellationToken>());
        Assert.Equal(1, result.DeletedMemories);
        Assert.Equal(0, result.QuarantinedMemories);
        Assert.True(result.ErasureComplete);
    }

    [Fact]
    public async Task PurgeAsync_Review_QuarantinesAndDeletesNothing()
    {
        await this.SeedAsync();

        var result = await this.purge.PurgeAsync(new HistoryPurgeScope("x", null), null, CancellationToken.None);

        await this.memories.DidNotReceive().DeleteManyAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        Assert.Equal(1, result.QuarantinedMemories);
        Assert.Equal(0, result.DeletedMemories);
    }

    [Fact]
    public async Task PurgeAsync_DeleteFails_StaysQuarantinedAndResumes()
    {
        await this.SeedAsync();
        this.memories.DeleteManyAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new IOException("memory.db busy"), _ => 1);

        var result = await this.purge.PurgeAsync(new HistoryPurgeScope("x", null), null, CancellationToken.None, dependentMemories: DependentMemoryAction.Delete);

        Assert.False(result.ErasureComplete);
        Assert.Contains(ErasureSteps.DependentMemories, result.CleanupPending);
        Assert.Equal(1, result.QuarantinedMemories);
        Assert.Equal(0, result.DeletedMemories);

        Assert.Empty(await this.purge.ResumePendingAsync(CancellationToken.None));
        await this.memories.Received(2).DeleteManyAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ForPurge_Delete_AddsTheStepBeforeVacuum()
    {
        Assert.Equal(
            [ErasureSteps.Index, ErasureSteps.MessagesWal, ErasureSteps.ToolOutput, ErasureSteps.Subagents, ErasureSteps.SmokeArtifacts, ErasureSteps.DependentMemories, ErasureSteps.Vacuum],
            ErasureSteps.ForPurge(HistoryPurgeScope.All, DependentMemoryAction.Delete));
    }
}
```

and in `AgentHubHistoryTests.cs` insert before `PurgeHistory_InvertedRange_RejectedWithoutDeleting` (keep its `[Fact]`):

```csharp
    [Fact]
    public async Task PurgeHistory_UnknownDependentMemoryAction_RejectedWithoutDeleting()
    {
        await this.h.SayAsync("a", 0);

        await Assert.ThrowsAsync<HubException>(() => this.hub.PurgeHistory(new HistoryPurgeRequestDto { DependentMemories = "purge" }));

        Assert.Single(await this.h.Store.GetMessagesAsync("ch"));
    }

```

- [ ] **Step 2: Run — expect a compile failure:**

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~DependentMemoryErasureTests|FullyQualifiedName~AgentHubHistoryTests"
```

- [ ] **Step 3: Edit `HistoryPurge.cs`** — insert before the `HistoryPurgePreview` summary:

```csharp
/// <summary>What a purge does with the curated memories created in the purged period.</summary>
public enum DependentMemoryAction
{
    /// <summary>Quarantine them until the owner keeps or deletes them (D10, the default).</summary>
    Review,

    /// <summary>Erase them physically with the history (owner opt-in per purge).</summary>
    Delete,
}

```

- [ ] **Step 4: Edit `ErasureTask.cs`** — insert before `/// <summary><c>VACUUM</c> of <c>messages.db</c> and the current index generation.</summary>`:

```csharp
    /// <summary>Erase the curated memories inside this purge's quarantine windows (owner opt-in per purge).</summary>
    public const string DependentMemories = "dependent-memories";

```

and replace `ForPurge` with:

```csharp
    /// <summary>The steps of a user purge of <paramref name="scope"/>; <see cref="DependentMemories"/> only when the owner chose deletion.</summary>
    public static IReadOnlyList<string> ForPurge(HistoryPurgeScope scope, DependentMemoryAction dependentMemories = DependentMemoryAction.Review)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return dependentMemories == DependentMemoryAction.Delete
            ? [Index, MessagesWal, ToolOutput, Subagents, SmokeArtifacts, DependentMemories, Vacuum]
            : [Index, MessagesWal, ToolOutput, Subagents, SmokeArtifacts, Vacuum];
    }
```

- [ ] **Step 5: Create `src/Cortex.Contained.Agent.Host/Memory/DependentMemoryErasureStep.cs`:**

```csharp
using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// Erasure step <see cref="ErasureSteps.DependentMemories"/>: deletes the not-kept curated memories inside
/// the quarantine windows of its purge in one batch (<c>DeleteManyAsync</c>: memory rows, content files,
/// vectors and keyword rows with a single FTS optimize and WAL truncate). Only scheduled when the owner
/// chose <see cref="DependentMemoryAction.Delete"/> for that purge; re-running it is idempotent, so a
/// failure stays pending (the memories remain quarantined meanwhile) and resumes on the next maintenance tick.
/// </summary>
public sealed class DependentMemoryErasureStep(MemoryQuarantine quarantine, IMemoryManagementService memories) : IErasureStep
{
    /// <inheritdoc />
    public string Name => ErasureSteps.DependentMemories;

    /// <inheritdoc />
    public async Task<bool> RunAsync(ErasureTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var ids = await quarantine.InErasureAsync(task.Id, cancellationToken).ConfigureAwait(false);
        if (ids.Count > 0)
        {
            await memories.DeleteManyAsync(ids.ToList(), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}
```

- [ ] **Step 6: Edit `HistoryPurgeService.cs`.** Replace the `HistoryPurgeResult` record line (keep the doc params, add one):

```csharp
/// <param name="QuarantinedMemories">Curated memories now quarantined for review (D10).</param>
/// <param name="DeletedMemories">Curated memories erased with the history (owner opt-in).</param>
public sealed record HistoryPurgeResult(int Deleted, int Inserted, bool ErasureComplete, IReadOnlyList<string> CleanupPending, int QuarantinedMemories, int DeletedMemories = 0);
```

Replace the whole `PurgeAsync` method (from its `/// <param name="quarantineCap">` line to its closing brace) with:

```csharp
    /// <param name="quarantineCap">Quarantine windows end no later than this (the moment an import began), or null for the purge time.</param>
    /// <param name="dependentMemories">Quarantine (default) or erase the curated memories created in the purged period.</param>
    public async Task<HistoryPurgeResult> PurgeAsync(
        HistoryPurgeScope scope,
        IReadOnlyList<MessageRecord>? replacement,
        CancellationToken cancellationToken,
        DateTimeOffset? quarantineCap = null,
        DependentMemoryAction dependentMemories = DependentMemoryAction.Review)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var settings = this.options.CurrentValue;
        MessagePurgeOutcome outcome;
        IReadOnlyList<string> pending;
        int affected;

        await using (await this.gate.AcquireWriteAsync(cancellationToken).ConfigureAwait(false))
        {
            var request = new MessagePurgeRequest(scope)
            {
                Now = this.time.GetUtcNow(),
                QuarantineSlack = TimeSpan.FromHours(settings.QuarantineExtractionSlackHours),
                QuarantineClusterGap = TimeSpan.FromHours(settings.QuarantineClusterGapHours),
                PendingSteps = ErasureSteps.ForPurge(scope, dependentMemories),
                Replacement = replacement,
                QuarantineCap = quarantineCap,
            };
            outcome = await this.store.PurgeAsync(request, cancellationToken).ConfigureAwait(false);

            // Counted before the steps run: the dependent-memories step deletes exactly these.
            affected = this.quarantine is null
                ? 0
                : await this.quarantine.CountInWindowsAsync(outcome.Windows, cancellationToken).ConfigureAwait(false);
            pending = await this.RunPendingLockedAsync(request.ErasureId, cancellationToken).ConfigureAwait(false);
        }

        var erased = dependentMemories == DependentMemoryAction.Delete && !pending.Contains(ErasureSteps.DependentMemories);
        var quarantined = erased ? 0 : affected;
        var deletedMemories = erased ? affected : 0;
        this.LogPurged(scope.ChannelId ?? "(all)", outcome.Deleted, pending.Count == 0, quarantined, deletedMemories);
        return new HistoryPurgeResult(outcome.Deleted, outcome.Inserted, pending.Count == 0, pending, quarantined, deletedMemories);
    }
```

and replace the `LogPurged` declaration with:

```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "History purge of {Scope}: {Deleted} messages deleted, erasure complete={Complete}, {Quarantined} memories quarantined, {DeletedMemories} memories deleted")]
    private partial void LogPurged(string scope, int deleted, bool complete, int quarantined, int deletedMemories);
```

- [ ] **Step 7: Edit `MemoryQuarantine.cs`** — in the `MemoryQuarantine` class summary replace
`/// consolidation and compaction until the owner keeps or deletes them. Never auto-deleted.` with:

```csharp
/// consolidation and compaction until the owner keeps or deletes them. They are deleted automatically
/// only when the owner chose that for the purge (<c>DependentMemories = delete</c>, see
/// <c>DependentMemoryErasureStep</c>).
```

- [ ] **Step 8: Edit `AgentHub.cs`** (`PurgeHistory`). After the `scope.Problem()` check insert:

```csharp

        if (!DependentMemoryActions.IsValid(request.DependentMemories))
        {
            throw new HubException("dependentMemories must be review or delete.");
        }

        var dependentMemories = DependentMemoryActions.IsDelete(request.DependentMemories) ? DependentMemoryAction.Delete : DependentMemoryAction.Review;
```

change the purge call to pass it:

```csharp
        var result = await this.historyPurge.PurgeAsync(
            scope,
            null,
            Context.ConnectionAborted,
            dependentMemories: dependentMemories).ConfigureAwait(false);
```

and extend the returned DTO after `QuarantinedMemories = result.QuarantinedMemories,`:

```csharp
            DeletedMemories = result.DeletedMemories,
            DependentMemories = dependentMemories == DependentMemoryAction.Delete ? DependentMemoryActions.Delete : DependentMemoryActions.Review,
```

- [ ] **Step 9: Edit `Program.cs`** — after `builder.Services.AddSingleton<IErasureStep, VacuumErasureStep>();` add:

```csharp
builder.Services.AddSingleton<IErasureStep, Cortex.Contained.Agent.Host.Memory.DependentMemoryErasureStep>();
```

(Resolved lazily with `HistoryPurgeService`; `MemoryQuarantine` and `IMemoryManagementService` are registered further down.)

- [ ] **Step 10: Run — expect PASS**, then the neighbours:

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~DependentMemoryErasureTests|FullyQualifiedName~AgentHubHistoryTests|FullyQualifiedName~HistoryPurge|FullyQualifiedName~MessageStoreJournalTests"
```

- [ ] **Step 11: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/History/HistoryPurge.cs src/Cortex.Contained.Agent.Host/History/ErasureTask.cs src/Cortex.Contained.Agent.Host/History/HistoryPurgeService.cs src/Cortex.Contained.Agent.Host/Memory/DependentMemoryErasureStep.cs src/Cortex.Contained.Agent.Host/Memory/MemoryQuarantine.cs src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs src/Cortex.Contained.Agent.Host/Program.cs tests/Cortex.Contained.Agent.Host.Tests/History/DependentMemoryErasureTests.cs tests/Cortex.Contained.Agent.Host.Tests/History/AgentHubHistoryTests.cs
git commit -m "feat(history): let the owner delete dependent memories with a purge" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---
### Task 10: `QuarantineReviewService` (list with purge source, bulk keep/delete)

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Memory/QuarantineReviewService.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Recall/QuarantineReviewServiceTests.cs`

- [ ] **Step 1: Write the failing tests** — create `tests/Cortex.Contained.Agent.Host.Tests/Recall/QuarantineReviewServiceTests.cs`:

```csharp
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Recall;

/// <summary>Quarantine review: list with purge source, bulk keep, bulk delete through one batched delete.</summary>
public sealed class QuarantineReviewServiceTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"review-{Guid.NewGuid():N}");
    private readonly MessageStore store;
    private readonly IMemoryCreationTimes times = Substitute.For<IMemoryCreationTimes>();
    private readonly IMemoryManagementService memories = Substitute.For<IMemoryManagementService>();
    private readonly QuarantineReviewService review;

    public QuarantineReviewServiceTests()
    {
        this.store = new MessageStore(Path.Combine(this.dir, "messages.db"), NullLogger<MessageStore>.Instance);
        this.times.ListCreationTimesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new MemoryCreationTime("m1", T0.AddHours(1)),
            new MemoryCreationTime("m2", T0.AddHours(2)),
            new MemoryCreationTime("outside", T0.AddDays(9)),
        ]);
        this.memories.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new MemoryItem { MemoryId = call.Arg<string>(), Content = "content of " + call.Arg<string>() });
        this.memories.DeleteManyAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<string>>().Count);
        this.review = new QuarantineReviewService(new MemoryQuarantine(this.store, this.times, TimeProvider.System), this.memories);
    }

    public async ValueTask DisposeAsync()
    {
        await this.store.DisposeAsync();
        try { Directory.Delete(this.dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task PurgeAsync()
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "a", T0);
        await this.store.PurgeAsync(new MessagePurgeRequest(new HistoryPurgeScope("ch", null)) { Now = T0.AddDays(2) }, CancellationToken.None);
    }

    [Fact]
    public async Task ListAsync_IncludesContentAndPurgeSource()
    {
        await this.PurgeAsync();

        var list = await this.review.ListAsync(CancellationToken.None);

        Assert.Equal(["m2", "m1"], list.Items.Select(i => i.Memory.MemoryId));
        Assert.All(list.Items, i => Assert.Equal("ch", i.PurgeChannelId));
        Assert.Equal("content of m2", list.Items[0].Memory.Content);
        Assert.Equal(T0.AddDays(2), list.Items[0].PurgedAt);
    }

    [Fact]
    public async Task DeleteAsync_OnlyQuarantinedIds_InOneBatch()
    {
        await this.PurgeAsync();

        var result = await this.review.DeleteAsync(["m1", "m2", "outside"], CancellationToken.None);

        Assert.Equal(2, result.Affected);
        Assert.Equal(["outside"], result.Skipped);
        await this.memories.Received(1).DeleteManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.Count == 2 && ids.Contains("m1") && ids.Contains("m2")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_NothingQuarantined_DoesNotCallDelete()
    {
        var result = await this.review.DeleteAsync(["m1"], CancellationToken.None);

        Assert.Equal(0, result.Affected);
        await this.memories.DidNotReceive().DeleteManyAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KeepAsync_KeepsQuarantinedAndTheyLeaveTheList()
    {
        await this.PurgeAsync();

        var result = await this.review.KeepAsync(["m1", "outside"], CancellationToken.None);

        Assert.Equal(1, result.Affected);
        Assert.Equal(["outside"], result.Skipped);
        Assert.Equal(["m2"], (await this.review.ListAsync(CancellationToken.None)).Items.Select(i => i.Memory.MemoryId));
    }

    [Fact]
    public async Task KeepAsync_TooManyIds_Rejected()
    {
        var ids = Enumerable.Range(0, QuarantineReviewService.MaxBatch + 1).Select(i => "m" + i).ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => this.review.KeepAsync(ids, CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run — expect a compile failure:**

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~QuarantineReviewServiceTests"
```

- [ ] **Step 3: Create `src/Cortex.Contained.Agent.Host/Memory/QuarantineReviewService.cs`:**

```csharp
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// The owner's review of quarantined memories (D10) on the Recall page: the list with the purge that
/// quarantined each memory, and bulk keep / bulk delete. Only memories that are quarantined right now are
/// ever kept or deleted, so a stale or forged id list cannot touch other memories. Bulk delete is one
/// <see cref="IMemoryManagementService.DeleteManyAsync"/> call (a single keyword-index rewrite).
/// </summary>
public sealed class QuarantineReviewService
{
    /// <summary>Largest id list one keep or delete request may carry.</summary>
    public const int MaxBatch = 1000;

    private readonly MemoryQuarantine quarantine;
    private readonly IMemoryManagementService memories;

    public QuarantineReviewService(MemoryQuarantine quarantine, IMemoryManagementService memories)
    {
        this.quarantine = quarantine;
        this.memories = memories;
    }

    /// <summary>Quarantined memories, newest first, with content and the purge that quarantined them.</summary>
    public async Task<QuarantineReviewListDto> ListAsync(CancellationToken cancellationToken)
    {
        var items = new List<QuarantinedMemoryDto>();
        foreach (var entry in await this.quarantine.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await this.memories.GetAsync(entry.MemoryId, cancellationToken).ConfigureAwait(false) is not { } memory)
            {
                continue;
            }

            items.Add(new QuarantinedMemoryDto
            {
                Memory = memory,
                PurgeId = entry.Source.ErasureId,
                PurgedAt = entry.Source.PurgedAt,
                PurgeChannelId = entry.Source.ChannelId,
                WindowFrom = entry.Source.Window.From,
                WindowTo = entry.Source.Window.To,
            });
        }

        return new QuarantineReviewListDto { Items = items };
    }

    /// <summary>Keeps the quarantined members of <paramref name="memoryIds"/>.</summary>
    public async Task<QuarantineDecisionResultDto> KeepAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken)
    {
        CheckBatch(memoryIds);
        var (kept, skipped) = await this.quarantine.KeepManyAsync(memoryIds, cancellationToken).ConfigureAwait(false);
        return new QuarantineDecisionResultDto { Affected = kept.Count, Skipped = skipped };
    }

    /// <summary>Erases the quarantined members of <paramref name="memoryIds"/> in one batch.</summary>
    public async Task<QuarantineDecisionResultDto> DeleteAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken)
    {
        CheckBatch(memoryIds);
        var quarantined = await this.quarantine.GetQuarantinedIdsAsync(cancellationToken).ConfigureAwait(false);
        var requested = memoryIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        var delete = requested.Where(quarantined.Contains).ToList();
        var skipped = requested.Where(id => !quarantined.Contains(id)).ToList();
        var deleted = delete.Count == 0 ? 0 : await this.memories.DeleteManyAsync(delete, cancellationToken).ConfigureAwait(false);
        return new QuarantineDecisionResultDto { Affected = deleted, Skipped = skipped };
    }

    private static void CheckBatch(IReadOnlyList<string> memoryIds)
    {
        ArgumentNullException.ThrowIfNull(memoryIds);
        if (memoryIds.Count > MaxBatch)
        {
            throw new ArgumentException($"At most {MaxBatch} memories per request.", nameof(memoryIds));
        }
    }
}
```

- [ ] **Step 4: Run — expect PASS (5 tests).**

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Memory/QuarantineReviewService.cs tests/Cortex.Contained.Agent.Host.Tests/Recall/QuarantineReviewServiceTests.cs
git commit -m "feat(memory): quarantine review list with purge source and bulk keep/delete" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 11: Event backfill status + `RecallStatusMapper`

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Events/EventBackfillService.cs`
- Create: `src/Cortex.Contained.Agent.Host/Recall/RecallStatusMapper.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Events/EventBackfillServiceTests.cs`, `tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallStatusMapperTests.cs`

- [ ] **Step 1: Write the failing tests.** In `EventBackfillServiceTests.cs` insert before `RunAsync_LegacyRows_GetOneLegacyImportEach` (keep its `[Fact]`):

```csharp
    [Fact]
    public async Task Status_AfterRun_ReportsTheLastRun()
    {
        Assert.Equal(new EventBackfillStatus(false, null, null), this.backfill.Status);
        await this.h.SayAsync("m", 0);
        await this.ForgetAllEventsAsync();

        var report = await this.backfill.RunAsync(CancellationToken.None);

        var status = this.backfill.Status;
        Assert.False(status.Running);
        Assert.Equal(report, status.Last);
        Assert.NotNull(status.LastRunAt);
    }

    [Fact]
    public async Task Status_WhileWaitingForTheMaintenanceWindow_ReportsRunning()
    {
        var lease = await this.h.Gate.AcquireWriteAsync(CancellationToken.None);
        var run = this.backfill.RunAsync(CancellationToken.None);

        Assert.True(this.backfill.Status.Running);
        await lease.DisposeAsync();
        await run;
        Assert.False(this.backfill.Status.Running);
    }

```

Create `tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallStatusMapperTests.cs`:

```csharp
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Recall;
using Cortex.Contained.Agent.Host.Reranking;
using MemoryMcp.Core.Configuration;

namespace Cortex.Contained.Agent.Host.Tests.Recall;

public sealed class RecallStatusMapperTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Settings_ReflectsEffectiveOptionsAndOverrideNames()
    {
        var history = new HistoryOptions { Enabled = true, DefaultSearchMode = HistorySearchMode.Hybrid };
        history.Dense.Enabled = true;
        var reranker = new RerankerOptions { TimeoutMs = 2500 };
        reranker.History.TopN = 12;

        var settings = RecallStatusMapper.Settings(history, reranker, new MemorySearchOptions { DefaultMode = MemorySearchMode.Keyword }, new RecallOverrides { HistoryEnabled = true });

        Assert.True(settings.HistoryEnabled);
        Assert.True(settings.HistoryDenseEnabled);
        Assert.Equal("hybrid", settings.HistorySearchMode);
        Assert.Equal("keyword", settings.MemorySearchMode);
        Assert.Equal(12, settings.RerankHistoryTopN);
        Assert.Equal(1200, settings.RerankMemoryMaxDocChars);
        Assert.Equal(2500, settings.RerankTimeoutMs);
        Assert.Equal(["historyEnabled"], settings.Overridden);
    }

    [Fact]
    public void Status_MapsEverySubsystem()
    {
        var settings = RecallStatusMapper.Settings(new HistoryOptions(), new RerankerOptions(), new MemorySearchOptions(), RecallOverrides.None);
        var history = new HistoryHealth(true, true, "3.51.2", 3, false, true, 4, 9, false, null, 1, ["index"], 0)
        {
            Dense = new HistoryDenseHealth("on", 40, 100, 60, 1, T0, null),
        };
        var reranker = new RerankHealth(true, "circuit-open", 3, T0.AddMinutes(1), "timed out after 2000 ms", 2100, T0);
        var backfill = new EventBackfillStatus(false, new EventBackfillReport(10, 4, TimeSpan.FromMilliseconds(250)), T0);

        var status = RecallStatusMapper.Status(settings, history, reranker, backfill, quarantinedMemories: 2);

        Assert.Equal(3, status.History.Generation);
        Assert.True(status.History.BuildInProgress);
        Assert.Equal(4, status.History.CheckpointLag);
        Assert.Equal(["index"], status.History.PendingErasureSteps);
        Assert.Equal(60, status.Dense.Pending);
        Assert.Equal("circuit-open", status.Reranker.State);
        Assert.Equal(2100, status.Reranker.LastLatencyMs);
        Assert.Equal(4, status.EventBackfill.LastInserted);
        Assert.Equal(250, status.EventBackfill.LastElapsedMs);
        Assert.Equal(2, status.QuarantinedMemories);
    }

    [Fact]
    public void Status_NoBackfillService_ReportsNeverRun()
    {
        var settings = RecallStatusMapper.Settings(new HistoryOptions(), new RerankerOptions(), new MemorySearchOptions(), RecallOverrides.None);
        var history = new HistoryHealth(false, false, "3.51.2", null, false, false, 0, 0, false, null, 0, [], 0);

        var status = RecallStatusMapper.Status(settings, history, new RerankHealth(false, "off", 0, null, null, null, null), null, 0);

        Assert.False(status.EventBackfill.Running);
        Assert.Null(status.EventBackfill.LastRunAt);
        Assert.Equal("off", status.Dense.State);
    }
}
```

- [ ] **Step 2: Run — expect a compile failure:**

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~RecallStatusMapperTests|FullyQualifiedName~EventBackfillServiceTests"
```

- [ ] **Step 3: Edit `EventBackfillService.cs`.** After the `EventBackfillReport` record add:

```csharp

/// <summary>Whether a backfill is running and how the last one (since the agent started) ended.</summary>
public sealed record EventBackfillStatus(bool Running, EventBackfillReport? Last, DateTimeOffset? LastRunAt);
```

After the `logger` field add:

```csharp
    private int running;
    private volatile EventBackfillStatus last = new(false, null, null);
```

Replace

```csharp
    /// <summary>Runs the backfill to the end of the table.</summary>
    public async Task<EventBackfillReport> RunAsync(CancellationToken cancellationToken)
    {
```

with (the old body becomes `RunCoreAsync` unchanged):

```csharp
    /// <summary>The current state (for the Recall status page).</summary>
    public EventBackfillStatus Status => this.last with { Running = Volatile.Read(ref this.running) > 0 };

    /// <summary>Runs the backfill to the end of the table.</summary>
    public async Task<EventBackfillReport> RunAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this.running);
        try
        {
            var report = await this.RunCoreAsync(cancellationToken).ConfigureAwait(false);
            this.last = new EventBackfillStatus(false, report, this.time.GetUtcNow());
            return report;
        }
        finally
        {
            Interlocked.Decrement(ref this.running);
        }
    }

    private async Task<EventBackfillReport> RunCoreAsync(CancellationToken cancellationToken)
    {
```

- [ ] **Step 4: Create `src/Cortex.Contained.Agent.Host/Recall/RecallStatusMapper.cs`:**

```csharp
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Contracts.Hub;
using MemoryMcp.Core.Configuration;

namespace Cortex.Contained.Agent.Host.Recall;

/// <summary>Maps effective options and subsystem snapshots to the Recall DTOs (pure, no I/O).</summary>
public static class RecallStatusMapper
{
    /// <summary>The effective settings (options already carry the overrides through the post-configures).</summary>
    public static RecallSettingsDto Settings(HistoryOptions history, RerankerOptions reranker, MemorySearchOptions memorySearch, RecallOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(memorySearch);
        ArgumentNullException.ThrowIfNull(overrides);
        return new RecallSettingsDto
        {
            HistoryEnabled = history.Enabled,
            HistorySearchMode = RecallSettingsValidation.Text(history.DefaultSearchMode),
            HistoryDenseEnabled = history.Dense.Enabled,
            MemorySearchMode = RecallSettingsValidation.Text(memorySearch.DefaultMode),
            RerankHistoryTopN = reranker.History.TopN,
            RerankHistoryMaxDocChars = reranker.History.MaxDocChars,
            RerankMemoryTopN = reranker.Memory.TopN,
            RerankMemoryMaxDocChars = reranker.Memory.MaxDocChars,
            RerankTimeoutMs = reranker.TimeoutMs,
            Overridden = overrides.Names(),
        };
    }

    /// <summary>The Recall status page payload.</summary>
    public static RecallStatusDto Status(RecallSettingsDto settings, HistoryHealth history, RerankHealth reranker, EventBackfillStatus? backfill, int quarantinedMemories)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(reranker);
        var dense = history.Dense;
        return new RecallStatusDto
        {
            Settings = settings,
            History = new HistoryIndexStatusDto
            {
                Enabled = history.Enabled,
                ToolsEnabled = history.ToolsEnabled,
                Storage = history.Storage,
                Generation = history.Generation,
                BuildComplete = history.BuildComplete,
                BuildInProgress = history.BuildInProgress,
                CheckpointLag = history.CheckpointLag,
                JournalBacklog = history.JournalBacklog,
                Halted = history.Halted,
                LastError = history.LastError,
                PendingErasureTasks = history.PendingErasureTasks,
                PendingErasureSteps = history.PendingErasureSteps,
                MaintenancePendingTasks = history.MaintenancePendingTasks,
            },
            Dense = new DenseEmbeddingStatusDto
            {
                State = dense.State,
                Embedded = dense.Embedded,
                Total = dense.Total,
                Pending = dense.Pending,
                Failed = dense.Failed,
                OldestPendingQueuedAt = dense.OldestPendingQueuedAt,
                LastError = dense.LastError,
            },
            Reranker = new RerankerStatusDto
            {
                Enabled = reranker.Enabled,
                State = reranker.State,
                ConsecutiveFailures = reranker.ConsecutiveFailures,
                CircuitOpenUntil = reranker.CircuitOpenUntil,
                LastError = reranker.LastError,
                LastLatencyMs = reranker.LastLatencyMs,
                LastCallAt = reranker.LastCallAt,
            },
            EventBackfill = new EventBackfillStatusDto
            {
                Running = backfill?.Running ?? false,
                LastRunAt = backfill?.LastRunAt,
                LastScanned = backfill?.Last?.Scanned,
                LastInserted = backfill?.Last?.Inserted,
                LastElapsedMs = backfill?.Last is { } last ? (long)last.Elapsed.TotalMilliseconds : null,
            },
            QuarantinedMemories = quarantinedMemories,
        };
    }
}
```

- [ ] **Step 5: Run — expect PASS** (same command).

- [ ] **Step 6: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Events/EventBackfillService.cs src/Cortex.Contained.Agent.Host/Recall/RecallStatusMapper.cs tests/Cortex.Contained.Agent.Host.Tests/Events/EventBackfillServiceTests.cs tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallStatusMapperTests.cs
git commit -m "feat(recall): event backfill status and the recall status mapping" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 12: `RecallControlService`, `IRecallHub` on `AgentHub`, DI

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Recall/RecallControlService.cs`
- Create: `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.Recall.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs`, `src/Cortex.Contained.Contracts/Hub/IAgentHub.cs`, `src/Cortex.Contained.Agent.Host/Program.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallControlServiceTests.cs`, `tests/Cortex.Contained.Agent.Host.Tests/Recall/AgentHubRecallTests.cs`

- [ ] **Step 1: Write the failing tests** — create `tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallControlServiceTests.cs`:

```csharp
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Recall;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Tests.History;
using Cortex.Contained.Contracts.Hub;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Recall;

public sealed class RecallControlServiceTests : IAsyncDisposable
{
    private readonly HistoryHarness h = new();
    private readonly ServiceProvider provider;
    private readonly IMemoryCreationTimes times = Substitute.For<IMemoryCreationTimes>();
    private readonly RecallControlService recall;

    public RecallControlServiceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.Configure<HistoryOptions>(o => o.TimeZone = "UTC");
        services.Configure<RerankerOptions>(_ => { });
        services.Configure<MemoryMcpOptions>(_ => { });
        services.AddRecallSettings(Path.Combine(this.h.Root, "state"));
        this.provider = services.BuildServiceProvider();

        var historyMonitor = this.provider.GetRequiredService<IOptionsMonitor<HistoryOptions>>();
        this.times.ListCreationTimesAsync(Arg.Any<CancellationToken>()).Returns([new MemoryCreationTime("m1", HistoryHarness.T0.AddHours(1))]);
        var quarantine = new MemoryQuarantine(this.h.Store, this.times, TimeProvider.System);
        var memories = Substitute.For<IMemoryManagementService>();
        var purge = new HistoryPurgeService(
            this.h.Store, this.h.Gate, this.h.Projector, [], this.h.Monitor, TimeProvider.System, NullLogger<HistoryPurgeService>.Instance, quarantine);
        this.recall = new RecallControlService(
            this.provider.GetRequiredService<RecallSettingsStore>(),
            historyMonitor,
            this.provider.GetRequiredService<IOptionsMonitor<RerankerOptions>>(),
            this.provider.GetRequiredService<IOptionsMonitor<MemoryMcpOptions>>(),
            new HistoryHealthReporter(this.h.Store, this.h.Store, this.h.Status, new HistoryToolsSwitch(historyMonitor), historyMonitor),
            () => new RerankHealth(false, "off", 0, null, null, null, null),
            quarantine,
            new QuarantineReviewService(quarantine, memories),
            purge);
    }

    public async ValueTask DisposeAsync()
    {
        await this.provider.DisposeAsync();
        await this.h.DisposeAsync();
    }

    [Fact]
    public void Update_EnablesHistory_AppliesLiveAndExplains()
    {
        var result = this.recall.Update(new RecallSettingsPatchDto { HistoryEnabled = true, HistorySearchMode = "hybrid" });

        Assert.True(result.Applied);
        Assert.True(result.Settings.HistoryEnabled);
        Assert.Equal(["historyEnabled", "historySearchMode"], result.Settings.Overridden);
        Assert.Contains(result.Notices, n => n.StartsWith("History search is on.", StringComparison.Ordinal));
        Assert.Contains(result.Notices, n => n.StartsWith("Hybrid history search needs dense embeddings", StringComparison.Ordinal));
        Assert.True(this.recall.GetSettings().HistoryEnabled);
    }

    [Fact]
    public void Update_Invalid_NotAppliedAndSettingsUnchanged()
    {
        var result = this.recall.Update(new RecallSettingsPatchDto { HistoryEnabled = true, RerankTimeoutMs = 5 });

        Assert.False(result.Applied);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Notices);
        Assert.False(result.Settings.HistoryEnabled);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsHistoryAndQuarantine()
    {
        await this.h.SayAsync("a", 0, channel: "x");
        await this.h.Store.PurgeAsync(new MessagePurgeRequest(new HistoryPurgeScope("x", null)) { Now = HistoryHarness.T0.AddDays(2) }, CancellationToken.None);

        var status = await this.recall.GetStatusAsync(CancellationToken.None);

        Assert.Equal(1, status.QuarantinedMemories);
        Assert.False(status.History.Enabled);
        Assert.Equal("off", status.Reranker.State);
        Assert.False(status.EventBackfill.Running);
    }

    [Fact]
    public async Task PreviewPurgeAsync_InvertedRange_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => this.recall.PreviewPurgeAsync(
            new HistoryPurgeRequestDto { From = HistoryHarness.T0, OlderThan = HistoryHarness.T0.AddHours(-1) }, CancellationToken.None));
    }

    [Fact]
    public void Notices_DenseToggledWhileHistoryOn_SaysTheIndexIsRebuilt()
    {
        var before = new RecallSettingsDto { HistoryEnabled = true, HistoryDenseEnabled = false, HistorySearchMode = "keyword" };

        var notices = RecallNotices.For(before, before with { HistoryDenseEnabled = true });

        Assert.Equal(["Dense embeddings are on. The history index is rebuilt and then embedded in the background; hybrid search starts once chunks are embedded."], notices);
    }

    [Fact]
    public void Notices_HistorySwitchedOff_SaysToolsHiddenAndIndexKeptUntilNextDeletion()
    {
        var before = new RecallSettingsDto { HistoryEnabled = true, HistorySearchMode = "keyword" };

        var notice = Assert.Single(RecallNotices.For(before, before with { HistoryEnabled = false }));

        Assert.Contains("erased by the next history deletion", notice, StringComparison.Ordinal);
    }
}
```

and `tests/Cortex.Contained.Agent.Host.Tests/Recall/AgentHubRecallTests.cs`:

```csharp
using System.Reflection;
using System.Runtime.CompilerServices;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Agent.Host.Tests.Recall;

/// <summary>The hub's <see cref="IRecallHub"/> adapter: missing service and invalid input become <see cref="HubException"/>.</summary>
public sealed class AgentHubRecallTests
{
    private readonly AgentHub hub;

    public AgentHubRecallTests()
    {
        this.hub = (AgentHub)RuntimeHelpers.GetUninitializedObject(typeof(AgentHub));
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionAborted.Returns(CancellationToken.None);
        this.hub.Context = context;
    }

    [Fact]
    public void AgentHub_ImplementsRecallHub()
    {
        Assert.Contains(typeof(IRecallHub), typeof(IAgentHub).GetInterfaces());
        Assert.IsAssignableFrom<IRecallHub>(this.hub);
    }

    [Fact]
    public async Task GetRecallSettings_ServiceMissing_ThrowsHubException()
    {
        await Assert.ThrowsAsync<HubException>(() => this.hub.GetRecallSettings());
    }

    [Fact]
    public async Task PreviewHistoryPurge_ServiceMissing_ThrowsHubException()
    {
        typeof(AgentHub).GetField("recall", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this.hub, null);

        await Assert.ThrowsAsync<HubException>(() => this.hub.PreviewHistoryPurge(new HistoryPurgeRequestDto()));
    }
}
```

- [ ] **Step 2: Run — expect a compile failure:**

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Agent.Host.Tests.Recall"
```

- [ ] **Step 3: Create `src/Cortex.Contained.Agent.Host/Recall/RecallControlService.cs`:**

```csharp
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Contracts.Hub;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Recall;

/// <summary>
/// Agent-side facade behind the <see cref="IRecallHub"/> methods: settings (through
/// <see cref="RecallSettingsStore"/>), status, purge preview and quarantine review. The hub stays a thin
/// adapter; every decision here is unit-testable without SignalR.
/// </summary>
public sealed class RecallControlService
{
    private readonly RecallSettingsStore store;
    private readonly IOptionsMonitor<HistoryOptions> history;
    private readonly IOptionsMonitor<RerankerOptions> reranker;
    private readonly IOptionsMonitor<MemoryMcpOptions> memory;
    private readonly HistoryHealthReporter health;
    private readonly Func<RerankHealth> rerankHealth;
    private readonly MemoryQuarantine quarantine;
    private readonly QuarantineReviewService review;
    private readonly HistoryPurgeService purge;
    private readonly EventBackfillService? backfill;

    public RecallControlService(
        RecallSettingsStore store,
        IOptionsMonitor<HistoryOptions> history,
        IOptionsMonitor<RerankerOptions> reranker,
        IOptionsMonitor<MemoryMcpOptions> memory,
        HistoryHealthReporter health,
        Func<RerankHealth> rerankHealth,
        MemoryQuarantine quarantine,
        QuarantineReviewService review,
        HistoryPurgeService purge,
        EventBackfillService? backfill = null)
    {
        this.store = store;
        this.history = history;
        this.reranker = reranker;
        this.memory = memory;
        this.health = health;
        this.rerankHealth = rerankHealth;
        this.quarantine = quarantine;
        this.review = review;
        this.purge = purge;
        this.backfill = backfill;
    }

    /// <summary>The effective settings.</summary>
    public RecallSettingsDto GetSettings() =>
        RecallStatusMapper.Settings(this.history.CurrentValue, this.reranker.CurrentValue, this.memory.CurrentValue.Search, this.store.Current);

    /// <summary>Validates, persists and applies <paramref name="patch"/>; explains consequences.</summary>
    public RecallSettingsUpdateResultDto Update(RecallSettingsPatchDto patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var before = this.GetSettings();
        var applied = this.store.Apply(patch);
        var after = this.GetSettings();
        return new RecallSettingsUpdateResultDto
        {
            Applied = applied.Applied,
            Errors = applied.Errors,
            Notices = applied.Applied ? RecallNotices.For(before, after) : [],
            Settings = after,
        };
    }

    /// <summary>History index, embeddings, reranker, backfill and quarantine state.</summary>
    public async Task<RecallStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var historyHealth = await this.health.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var quarantined = (await this.quarantine.GetQuarantinedIdsAsync(cancellationToken).ConfigureAwait(false)).Count;
        return RecallStatusMapper.Status(this.GetSettings(), historyHealth, this.rerankHealth(), this.backfill?.Status, quarantined);
    }

    /// <summary>What a purge of <paramref name="request"/> would delete.</summary>
    /// <exception cref="ArgumentException">The time range is empty or inverted.</exception>
    public async Task<HistoryPurgePreviewDto> PreviewPurgeAsync(HistoryPurgeRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = new HistoryPurgeScope(string.IsNullOrWhiteSpace(request.ChannelId) ? null : request.ChannelId, request.OlderThan, request.From);
        if (scope.Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(request));
        }

        var preview = await this.purge.PreviewAsync(scope, cancellationToken).ConfigureAwait(false);
        return new HistoryPurgePreviewDto
        {
            Messages = preview.Messages,
            DependentMemories = preview.DependentMemories,
            RangeFrom = preview.RangeFrom,
            RangeTo = preview.RangeTo,
        };
    }

    /// <summary>Quarantined memories with their purge source.</summary>
    public Task<QuarantineReviewListDto> ListQuarantineAsync(CancellationToken cancellationToken) => this.review.ListAsync(cancellationToken);

    /// <summary>Bulk keep.</summary>
    public Task<QuarantineDecisionResultDto> KeepAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken) =>
        this.review.KeepAsync(memoryIds, cancellationToken);

    /// <summary>Bulk delete (one batched delete).</summary>
    public Task<QuarantineDecisionResultDto> DeleteAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken) =>
        this.review.DeleteAsync(memoryIds, cancellationToken);
}

/// <summary>Plain-language consequences of a recall settings change, shown after saving.</summary>
public static class RecallNotices
{
    /// <summary>Notices for the change from <paramref name="before"/> to <paramref name="after"/>.</summary>
    public static IReadOnlyList<string> For(RecallSettingsDto before, RecallSettingsDto after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var notices = new List<string>();
        if (!before.HistoryEnabled && after.HistoryEnabled)
        {
            notices.Add("History search is on. The index is being built in the background; until it completes, searches cover only recent messages (see Status).");
        }
        else if (before.HistoryEnabled && !after.HistoryEnabled)
        {
            notices.Add("History search is off. The recall tools are hidden now; the existing index is erased by the next history deletion, and switching back on rebuilds it.");
        }

        if (after.HistoryEnabled && before.HistoryDenseEnabled != after.HistoryDenseEnabled)
        {
            notices.Add(after.HistoryDenseEnabled
                ? "Dense embeddings are on. The history index is rebuilt and then embedded in the background; hybrid search starts once chunks are embedded."
                : "Dense embeddings are off. The history index is rebuilt without vectors; searches are keyword-only.");
        }

        if (after.HistoryEnabled && !after.HistoryDenseEnabled && after.HistorySearchMode != "keyword")
        {
            notices.Add("Hybrid history search needs dense embeddings; until they are on, history searches stay keyword-only.");
        }

        return notices;
    }
}
```

- [ ] **Step 4: Create `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.Recall.cs`:**

```csharp
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary><see cref="IRecallHub"/>: the Bridge Recall page (settings, status, purge preview, quarantine review).</summary>
public sealed partial class AgentHub
{
    /// <inheritdoc />
    public Task<RecallSettingsDto> GetRecallSettings() => Task.FromResult(this.Recall.GetSettings());

    /// <inheritdoc />
    public Task<RecallSettingsUpdateResultDto> UpdateRecallSettings(RecallSettingsPatchDto patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return Task.FromResult(this.Recall.Update(patch));
    }

    /// <inheritdoc />
    public Task<RecallStatusDto> GetRecallStatus() => this.Recall.GetStatusAsync(Context.ConnectionAborted);

    /// <inheritdoc />
    public async Task<HistoryPurgePreviewDto> PreviewHistoryPurge(HistoryPurgeRequestDto request)
    {
        try
        {
            return await this.Recall.PreviewPurgeAsync(request, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    /// <inheritdoc />
    public Task<QuarantineReviewListDto> ListQuarantineReview() => this.Recall.ListQuarantineAsync(Context.ConnectionAborted);

    /// <inheritdoc />
    public async Task<QuarantineDecisionResultDto> KeepQuarantinedMemories(IReadOnlyList<string> memoryIds)
    {
        try
        {
            return await this.Recall.KeepAsync(memoryIds, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<QuarantineDecisionResultDto> DeleteQuarantinedMemories(IReadOnlyList<string> memoryIds)
    {
        try
        {
            return await this.Recall.DeleteAsync(memoryIds, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    private Recall.RecallControlService Recall =>
        this.recall ?? throw new HubException("Recall settings are not available on this agent.");
}
```

- [ ] **Step 5: Edit `AgentHub.cs`** — add the field after `rerankerAccess`:

```csharp
    private readonly Cortex.Contained.Agent.Host.Recall.RecallControlService? recall;
```

make the constructor's last parameter list end with

```csharp
        Cortex.Contained.Agent.Host.Reranking.RerankerAccess? rerankerAccess = null,
        Cortex.Contained.Agent.Host.Recall.RecallControlService? recall = null)
```

and assign it after `this.rerankerAccess = rerankerAccess;`:

```csharp
        this.recall = recall;
```

- [ ] **Step 6: Edit `src/Cortex.Contained.Contracts/Hub/IAgentHub.cs`** — append `IRecallHub` to the composed list (and to the `<remarks>` list):

```csharp
public interface IAgentHub : IChatHub, IMemoryHub, IHistoryHub, IVoiceIdHub, ICodingHub, IMcpHub, ISystemHub, ISubagentHub, IRecallHub
```

- [ ] **Step 7: Edit `src/Cortex.Contained.Agent.Host/Program.cs`** — after the `IMemoryManagementService` registration (`sp => sp.GetRequiredService<…MemoryManagementService>());`) add:

```csharp

// --- Recall page (Bridge): settings, status, purge preview, quarantine review ---
builder.Services.AddSingleton(sp => new Cortex.Contained.Agent.Host.Memory.QuarantineReviewService(
    sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.MemoryQuarantine>(),
    sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.IMemoryManagementService>()));
builder.Services.AddSingleton(sp => new Cortex.Contained.Agent.Host.Recall.RecallControlService(
    sp.GetRequiredService<Cortex.Contained.Agent.Host.Recall.RecallSettingsStore>(),
    sp.GetRequiredService<IOptionsMonitor<HistoryOptions>>(),
    sp.GetRequiredService<IOptionsMonitor<Cortex.Contained.Agent.Host.Reranking.RerankerOptions>>(),
    sp.GetRequiredService<IOptionsMonitor<MemoryMcpOptions>>(),
    sp.GetRequiredService<HistoryHealthReporter>(),
    () => sp.GetRequiredService<Cortex.Contained.Agent.Host.Reranking.RerankStage>().Snapshot(),
    sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.MemoryQuarantine>(),
    sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.QuarantineReviewService>(),
    sp.GetRequiredService<HistoryPurgeService>(),
    sp.GetRequiredService<Cortex.Contained.Agent.Host.Events.EventBackfillService>()));
```

(SignalR activates `AgentHub` through DI; the optional `recall` parameter is filled because the service is registered.)

- [ ] **Step 8: Run — expect PASS** (same command; 30 tests in `…Tests.Recall` at this point), then build the solution: `dotnet build cortex-contained.sln -p:NuGetAudit=false -nodeReuse:false` → 0 warnings (the Bridge still compiles: `HubClient` calls hub methods by name).

- [ ] **Step 9: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Recall/RecallControlService.cs src/Cortex.Contained.Agent.Host/Hubs/AgentHub.Recall.cs src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs src/Cortex.Contained.Contracts/Hub/IAgentHub.cs src/Cortex.Contained.Agent.Host/Program.cs tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallControlServiceTests.cs tests/Cortex.Contained.Agent.Host.Tests/Recall/AgentHubRecallTests.cs
git commit -m "feat(recall): recall settings, status, preview and review on the agent hub" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 13: Reranker sidecar — honest status (image probe, GPU hint)

**Files:**
- Modify (replace): `src/Cortex.Contained.Bridge/Speech/IRerankerComposeRunner.cs`, `src/Cortex.Contained.Bridge/Speech/RerankerSidecarLifecycle.cs`
- Modify: `src/Cortex.Contained.Bridge/Speech/DockerComposeCommandRunner.cs`
- Test (replace): `tests/Cortex.Contained.Bridge.Tests/Speech/RerankerSidecarLifecycleTests.cs`

- [ ] **Step 1: Write the failing tests** — replace `tests/Cortex.Contained.Bridge.Tests/Speech/RerankerSidecarLifecycleTests.cs` (the four existing tests are kept; the constructor now stubs the image probe and a successful start):

```csharp
using Cortex.Contained.Bridge.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class RerankerSidecarLifecycleTests
{
    private readonly IRerankerComposeRunner runner = Substitute.For<IRerankerComposeRunner>();

    public RerankerSidecarLifecycleTests()
    {
        this.runner.HasRerankerImageAsync(Arg.Any<CancellationToken>()).Returns(true);
        this.runner.StartRerankerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
    }

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

    [Fact]
    public async Task Enabled_ImageMissing_ReportsBuildHintAndDoesNotStart()
    {
        this.runner.HasRerankerImageAsync(Arg.Any<CancellationToken>()).Returns(false);
        var sut = this.Sut();

        var status = await sut.ReconcileAsync(enabled: true, CancellationToken.None);

        Assert.Equal("failed", status.State);
        Assert.Equal(RerankerSidecarLifecycle.MissingImageProblem, status.Problem);
        Assert.Same(status, sut.Status);
        await this.runner.DidNotReceive().StartRerankerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enabled_ComposeFails_ReportsGpuHint()
    {
        this.runner.StartRerankerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var status = await this.Sut().ReconcileAsync(enabled: true, CancellationToken.None);

        Assert.Equal("failed", status.State);
        Assert.Contains("NVIDIA GPU", status.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_StartedButNotRunning_ReportsExit()
    {
        var status = await this.Sut().ReconcileAsync(enabled: true, CancellationToken.None);

        Assert.Equal(RerankerSidecarLifecycle.NotRunningProblem, status.Problem);
    }

    [Fact]
    public async Task Enabled_Running_ReportsRunning()
    {
        this.runner.IsRerankerRunningAsync(Arg.Any<CancellationToken>()).Returns(true);

        var status = await this.Sut().ReconcileAsync(enabled: true, CancellationToken.None);

        Assert.Equal(new RerankerSidecarStatus("running", null, status.At), status);
    }

    [Fact]
    public async Task Disabled_ReportsStopped()
    {
        Assert.Equal("stopped", (await this.Sut().ReconcileAsync(enabled: false, CancellationToken.None)).State);
    }
}
```

- [ ] **Step 2: Run — expect a compile failure** (`HasRerankerImageAsync`, `RerankerSidecarStatus`):

```powershell
dotnet test tests\Cortex.Contained.Bridge.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~RerankerSidecarLifecycleTests"
```

- [ ] **Step 3: Replace `src/Cortex.Contained.Bridge/Speech/IRerankerComposeRunner.cs`:**

```csharp
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

    /// <summary>True if the <c>cortex-reranker:latest</c> image exists locally (compose never pulls it).</summary>
    Task<bool> HasRerankerImageAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Edit `DockerComposeCommandRunner.cs`** — after `IsRerankerRunningAsync` add:

```csharp

    /// <inheritdoc />
    public Task<bool> HasRerankerImageAsync(CancellationToken cancellationToken)
        => this.RunCommandAsync("image inspect cortex-reranker:latest --format {{.Id}}", ShortTimeout, cancellationToken);
```

- [ ] **Step 5: Replace `src/Cortex.Contained.Bridge/Speech/RerankerSidecarLifecycle.cs`** (`ReconcileAsync` now returns and records a `RerankerSidecarStatus`; startup callers that discard the task are unaffected):

```csharp
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>What the Bridge knows about the cortex-reranker sidecar after its last reconcile.</summary>
/// <param name="State"><c>unknown</c> (never reconciled), <c>starting</c>, <c>running</c>, <c>stopped</c> or <c>failed</c>.</param>
/// <param name="Problem">Why it is not running when it should be, in words the owner can act on; null otherwise.</param>
/// <param name="At">When the state was recorded, or null.</param>
public sealed record RerankerSidecarStatus(string State, string? Problem, DateTimeOffset? At)
{
    /// <summary>Never reconciled.</summary>
    public static RerankerSidecarStatus Unknown { get; } = new("unknown", null, null);
}

/// <summary>
/// Converges the cortex-reranker sidecar with <c>Reranker:Enabled</c>: when enabled it always runs
/// `compose up` with the DPAPI-held API key (idempotent; compose recreates the container after a key
/// or image change, which is how a Self-Update image upgrade or rollback reaches the sidecar); when
/// disabled it stops a running sidecar. Failures are logged and reported in <see cref="Status"/>
/// (missing image, compose failure such as no GPU, a container that exits at once), never propagated.
/// </summary>
public sealed partial class RerankerSidecarLifecycle
{
    /// <summary>Shown when <c>cortex-reranker:latest</c> does not exist (compose never pulls it).</summary>
    public const string MissingImageProblem =
        "The cortex-reranker image is not built on this machine. Build it with scripts\\Build-RerankerImage.ps1 " +
        "(or Build-All.ps1 -WithReranker), then switch the reranker on again.";

    /// <summary>Shown when compose could not start the container.</summary>
    public const string StartFailedProblem =
        "docker compose could not start cortex-reranker. The default CUDA image needs an NVIDIA GPU with Docker GPU " +
        "support; without one, build the CPU variant (scripts\\Build-RerankerImage.ps1 -Variant cpu). Details are in the Bridge log.";

    /// <summary>Shown when the container exited right after starting.</summary>
    public const string NotRunningProblem =
        "cortex-reranker started but is not running (it may have exited: GPU unavailable or model load failure). " +
        "Check `docker logs cortex-reranker`.";

    private readonly IRerankerComposeRunner runner;
    private readonly Func<string> apiKey;
    private readonly ILogger<RerankerSidecarLifecycle> logger;
    private readonly TimeProvider time;
    private volatile RerankerSidecarStatus status = RerankerSidecarStatus.Unknown;

    public RerankerSidecarLifecycle(IRerankerComposeRunner runner, Func<string> apiKey, ILogger<RerankerSidecarLifecycle> logger, TimeProvider? time = null)
    {
        this.runner = runner;
        this.apiKey = apiKey;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>The state recorded by the last reconcile.</summary>
    public RerankerSidecarStatus Status => this.status;

    /// <summary>Starts or stops the sidecar to match <paramref name="enabled"/>; returns (and records) the outcome.</summary>
    public async Task<RerankerSidecarStatus> ReconcileAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            if (enabled)
            {
                this.Record("starting", null);
                this.LogConverging();
                if (!await this.runner.HasRerankerImageAsync(cancellationToken).ConfigureAwait(false))
                {
                    this.LogStartFailed();
                    return this.Record("failed", MissingImageProblem);
                }

                if (!await this.runner.StartRerankerAsync(this.apiKey(), cancellationToken).ConfigureAwait(false))
                {
                    this.LogStartFailed();
                    return this.Record("failed", StartFailedProblem);
                }

                return await this.runner.IsRerankerRunningAsync(cancellationToken).ConfigureAwait(false)
                    ? this.Record("running", null)
                    : this.Record("failed", NotRunningProblem);
            }

            if (await this.runner.IsRerankerRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                this.LogStopping();
                await this.runner.StopRerankerAsync(cancellationToken).ConfigureAwait(false);
            }

            return this.Record("stopped", null);
        }
#pragma warning disable CA1031 // A sidecar problem must never take the Bridge down
        catch (Exception ex)
        {
            this.LogReconcileFailed(ex.Message);
            return this.Record("failed", "The reranker could not be reconciled: " + ex.Message);
        }
#pragma warning restore CA1031
    }

    private RerankerSidecarStatus Record(string state, string? problem)
    {
        var next = new RerankerSidecarStatus(state, problem, this.time.GetUtcNow());
        this.status = next;
        return next;
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
```

- [ ] **Step 6: Run — expect PASS (9 tests).**

- [ ] **Step 7: Commit**

```powershell
git add src/Cortex.Contained.Bridge/Speech/IRerankerComposeRunner.cs src/Cortex.Contained.Bridge/Speech/DockerComposeCommandRunner.cs src/Cortex.Contained.Bridge/Speech/RerankerSidecarLifecycle.cs tests/Cortex.Contained.Bridge.Tests/Speech/RerankerSidecarLifecycleTests.cs
git commit -m "feat(reranker): report why the reranker sidecar is not running" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 14: Reranker switch — `cortex.yml` persistence + safe converge

**Files:**
- Create: `src/Cortex.Contained.Bridge/RerankerConfigYamlWriter.cs`, `src/Cortex.Contained.Bridge/Recall/RerankerSwitch.cs`
- Modify: `src/Cortex.Contained.Bridge/Setup/BridgeSettingsWriter.cs`, `src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs`, `src/Cortex.Contained.Bridge/Program.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/Recall/RerankerSwitchTests.cs`

- [ ] **Step 1: Write the failing tests** — create `tests/Cortex.Contained.Bridge.Tests/Recall/RerankerSwitchTests.cs`:

```csharp
using System.Text;
using Cortex.Contained.Bridge.Recall;
using Cortex.Contained.Bridge.Speech;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Recall;

/// <summary>The Bridge-wide reranker switch: persisted in cortex.yml, converged in a safe order.</summary>
public sealed class RerankerSwitchTests : IDisposable
{
    private readonly BridgeConfig config = new();
    private readonly IRerankerComposeRunner runner = Substitute.For<IRerankerComposeRunner>();
    private readonly List<string> calls = [];
    private readonly RerankerSwitch sut;

    public RerankerSwitchTests()
    {
        this.runner.HasRerankerImageAsync(Arg.Any<CancellationToken>()).Returns(true);
        this.runner.StartRerankerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            this.calls.Add("compose-up");
            return true;
        });
        this.runner.IsRerankerRunningAsync(Arg.Any<CancellationToken>()).Returns(true);
        this.runner.StopRerankerAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            this.calls.Add("compose-stop");
            return true;
        });
        this.sut = new RerankerSwitch(
            this.config,
            _ => this.calls.Add("persist"),
            new RerankerSidecarLifecycle(this.runner, () => "k", NullLogger<RerankerSidecarLifecycle>.Instance),
            _ =>
            {
                this.calls.Add("grant:" + this.config.Reranker.Enabled);
                return Task.CompletedTask;
            },
            NullLogger<RerankerSwitch>.Instance);
    }

    public void Dispose() => this.sut.Dispose();

    [Fact]
    public async Task Enable_StartsTheSidecarBeforeGrantingAgents()
    {
        this.sut.Set(true);
        await this.sut.ConvergeAsync(CancellationToken.None);

        Assert.Equal(["persist", "compose-up", "grant:True"], this.calls);
        Assert.Equal("running", this.sut.Current.Sidecar.State);
    }

    [Fact]
    public async Task Disable_WithdrawsTheGrantBeforeStoppingTheSidecar()
    {
        this.config.Reranker.Enabled = true;

        this.sut.Set(false);
        await this.sut.ConvergeAsync(CancellationToken.None);

        Assert.Equal(["persist", "grant:False", "compose-stop"], this.calls);
        Assert.False(this.sut.Current.Enabled);
    }

    [Fact]
    public void PersistSettingsToYaml_WritesTheRerankerSwitch()
    {
        var path = Path.Combine(Path.GetTempPath(), "cortex-" + Guid.NewGuid().ToString("N") + ".yml");
        try
        {
            Cortex.Contained.Bridge.Setup.BridgeSettingsWriter.PersistSettingsToYaml(new BridgeConfig { Reranker = { Enabled = true } }, path);

            Assert.Contains("reranker:" + Environment.NewLine + "  enabled: true", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void AppendRerankerSection_Disabled_WritesFalse()
    {
        var sb = new StringBuilder();

        RerankerConfigYamlWriter.AppendRerankerSection(sb, new RerankerSettingsConfig());

        Assert.Contains("  enabled: false", sb.ToString(), StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run — expect a compile failure:**

```powershell
dotnet test tests\Cortex.Contained.Bridge.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~RerankerSwitchTests"
```

- [ ] **Step 3: Create `src/Cortex.Contained.Bridge/RerankerConfigYamlWriter.cs`** (next to `MemoryConfigYamlWriter.cs`, same namespace):

```csharp
using System.Text;

using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge;

/// <summary>
/// Serializes <see cref="RerankerSettingsConfig"/> (<c>reranker:</c>) into the bridge YAML file, so the
/// Recall page's reranker switch survives restarts and every other settings save keeps it.
/// </summary>
internal static class RerankerConfigYamlWriter
{
    public static void AppendRerankerSection(StringBuilder sb, RerankerSettingsConfig reranker)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(reranker);
        sb.AppendLine();
        sb.AppendLine("reranker:");
        sb.AppendLine(reranker.Enabled ? "  enabled: true" : "  enabled: false");
    }
}
```

- [ ] **Step 4: Edit `Setup/BridgeSettingsWriter.cs`** — after `MemoryConfigYamlWriter.AppendMemorySection(sb, config.Memory);` add:

```csharp

        // Reranker switch (Bridge-wide; the Recall page toggles it)
        RerankerConfigYamlWriter.AppendRerankerSection(sb, config.Reranker);
```

(Before this, every settings save silently dropped a hand-written `reranker: enabled: true`.)

- [ ] **Step 5: Edit `Hosting/CredentialsPusher.cs`** — make the grant push callable by the switch: change `private async Task PushRerankerGrantAsync(CancellationToken cancellationToken)` to `public async Task PushRerankerGrantAsync(CancellationToken cancellationToken)`.

- [ ] **Step 6: Create `src/Cortex.Contained.Bridge/Recall/RerankerSwitch.cs`:**

```csharp
using Cortex.Contained.Bridge.Speech;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Recall;

/// <summary>The Bridge-wide reranker switch as the Recall page shows it.</summary>
/// <param name="Enabled"><c>Reranker:Enabled</c> in the Bridge configuration (shared by every tenant).</param>
/// <param name="Sidecar">The sidecar state after the last reconcile.</param>
public sealed record RerankerSwitchView(bool Enabled, RerankerSidecarStatus Sidecar);

/// <summary>
/// Owns <c>Reranker:Enabled</c>: persists the switch to <c>cortex.yml</c>, then converges the sidecar and the
/// agents' grants in a safe order — on enable the sidecar starts first and the grant follows (so agents do not
/// trip their circuit breaker on a container that is still loading); on disable the grant is withdrawn first
/// and the sidecar stops afterwards.
/// </summary>
public sealed partial class RerankerSwitch : IDisposable
{
    private readonly BridgeConfig config;
    private readonly Action<BridgeConfig> persist;
    private readonly RerankerSidecarLifecycle lifecycle;
    private readonly Func<CancellationToken, Task> pushGrant;
    private readonly ILogger<RerankerSwitch> logger;
    private readonly SemaphoreSlim converge = new(1, 1);

    public RerankerSwitch(
        BridgeConfig config,
        Action<BridgeConfig> persist,
        RerankerSidecarLifecycle lifecycle,
        Func<CancellationToken, Task> pushGrant,
        ILogger<RerankerSwitch> logger)
    {
        this.config = config;
        this.persist = persist;
        this.lifecycle = lifecycle;
        this.pushGrant = pushGrant;
        this.logger = logger;
    }

    /// <summary>The switch and the sidecar state.</summary>
    public RerankerSwitchView Current => new(this.config.Reranker.Enabled, this.lifecycle.Status);

    /// <summary>Persists <paramref name="enabled"/> (synchronously, so a failure surfaces to the caller).</summary>
    public void Set(bool enabled)
    {
        this.config.Reranker.Enabled = enabled;
        this.persist(this.config);
    }

    /// <summary>Converges the sidecar and every agent's grant with the persisted switch. Never throws.</summary>
    public async Task ConvergeAsync(CancellationToken cancellationToken)
    {
        await this.converge.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var enabled = this.config.Reranker.Enabled;
            if (enabled)
            {
                await this.lifecycle.ReconcileAsync(true, cancellationToken).ConfigureAwait(false);
                await this.pushGrant(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await this.pushGrant(cancellationToken).ConfigureAwait(false);
                await this.lifecycle.ReconcileAsync(false, cancellationToken).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Converging runs in the background; failures are logged and shown as sidecar state
        catch (Exception ex)
        {
            this.LogConvergeFailed(ex.Message);
        }
#pragma warning restore CA1031
        finally
        {
            this.converge.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => this.converge.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reranker switch could not converge: {Error}")]
    private partial void LogConvergeFailed(string error);
}
```

- [ ] **Step 7: Edit `src/Cortex.Contained.Bridge/Program.cs`** — after the `RerankerSidecarLifecycle` registration add:

```csharp
// Recall page reranker switch: persists Reranker:Enabled to cortex.yml, then converges sidecar + agent grants.
builder.Services.AddSingleton(sp => new Cortex.Contained.Bridge.Recall.RerankerSwitch(
    sp.GetRequiredService<BridgeConfig>(),
    cfg => Cortex.Contained.Bridge.Setup.BridgeSettingsWriter.PersistSettingsToYaml(cfg, cortexConfigPath),
    sp.GetRequiredService<Cortex.Contained.Bridge.Speech.RerankerSidecarLifecycle>(),
    ct => sp.GetRequiredService<Cortex.Contained.Bridge.Hosting.CredentialsPusher>().PushRerankerGrantAsync(ct),
    sp.GetRequiredService<ILogger<Cortex.Contained.Bridge.Recall.RerankerSwitch>>()));
```

- [ ] **Step 8: Run — expect PASS (4 tests)**, plus `--filter "FullyQualifiedName~MemoryConfigYamlWriterTests|FullyQualifiedName~SubagentSettingsYamlTests|FullyQualifiedName~CredentialsPusherRerankerGrantTests"`.

- [ ] **Step 9: Commit**

```powershell
git add src/Cortex.Contained.Bridge/RerankerConfigYamlWriter.cs src/Cortex.Contained.Bridge/Recall/RerankerSwitch.cs src/Cortex.Contained.Bridge/Setup/BridgeSettingsWriter.cs src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs src/Cortex.Contained.Bridge/Program.cs tests/Cortex.Contained.Bridge.Tests/Recall/RerankerSwitchTests.cs
git commit -m "feat(reranker): persist the reranker switch and converge sidecar and grants" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 15: Purge confirmation wording + `dependentMemories` on existing DELETE endpoints

**Files:**
- Create: `src/Cortex.Contained.Bridge/Recall/HistoryPurgeConfirmation.cs`
- Modify (replace): `src/Cortex.Contained.Bridge/Tenants/HistoryDeletionResponse.cs`
- Modify: `src/Cortex.Contained.Bridge/Tenants/TenantEndpoints.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/Recall/HistoryPurgeConfirmationTests.cs` (create), `tests/Cortex.Contained.Bridge.Tests/HistoryDeletionResponseTests.cs` (replace)

- [ ] **Step 1: Write the failing tests** — create `tests/Cortex.Contained.Bridge.Tests/Recall/HistoryPurgeConfirmationTests.cs`:

```csharp
using Cortex.Contained.Bridge.Recall;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Tests.Recall;

public sealed class HistoryPurgeConfirmationTests
{
    [Fact]
    public void Describe_Review_StatesCountAndChoice()
    {
        var text = HistoryPurgeConfirmation.Describe(new HistoryPurgePreviewDto { Messages = 40, DependentMemories = 3 }, DependentMemoryActions.Review);

        Assert.Equal(
            "This deletes 40 messages and erases them from the search index. 3 memories were created in that period. You chose Review: they are hidden from the agent until you keep or delete them under Recall → Needs review.",
            text);
    }

    [Fact]
    public void Describe_Delete_SaysPermanent()
    {
        var text = HistoryPurgeConfirmation.Describe(new HistoryPurgePreviewDto { Messages = 40, DependentMemories = 3 }, DependentMemoryActions.Delete);

        Assert.EndsWith("3 memories were created in that period. You chose Delete: they are erased permanently together with the history.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_NoMemoriesOrNoMessages_SaysSo()
    {
        Assert.EndsWith("No memories were created in that period.", HistoryPurgeConfirmation.Describe(new HistoryPurgePreviewDto { Messages = 1 }, DependentMemoryActions.Delete), StringComparison.Ordinal);
        Assert.Equal("Nothing matches: no messages would be deleted.", HistoryPurgeConfirmation.Describe(new HistoryPurgePreviewDto(), DependentMemoryActions.Review));
    }
}
```

and replace `tests/Cortex.Contained.Bridge.Tests/HistoryDeletionResponseTests.cs` (the old "never deleted automatically" assertion is gone on purpose — D10 is amended):

```csharp
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Tests;

public class HistoryDeletionResponseTests
{
    [Fact]
    public void From_ReviewChosen_ConfirmationStatesTheChoiceAndWhereToReview()
    {
        var response = HistoryDeletionResponse.From(new HistoryPurgeResultDto { Deleted = 12, ErasureComplete = true, QuarantinedMemories = 3 });

        Assert.Equal(12, response.DeletedCount);
        Assert.Equal(DependentMemoryActions.Review, response.DependentMemories);
        Assert.Contains("You chose Review: 3 memories", response.Message, StringComparison.Ordinal);
        Assert.Contains("Recall → Needs review", response.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void From_DeleteChosen_ConfirmationStatesHowManyWereErased()
    {
        var response = HistoryDeletionResponse.From(new HistoryPurgeResultDto
        {
            Deleted = 12, ErasureComplete = true, DeletedMemories = 2, DependentMemories = DependentMemoryActions.Delete,
        });

        Assert.Equal(2, response.DeletedMemories);
        Assert.Equal(DependentMemoryActions.Delete, response.DependentMemories);
        Assert.Contains("You chose Delete: 2 memories created in that period were erased with the history.", response.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void From_DeleteChosenButPending_SaysTheyAreHiddenUntilCleanupFinishes()
    {
        var response = HistoryDeletionResponse.From(new HistoryPurgeResultDto
        {
            Deleted = 1, ErasureComplete = false, CleanupPending = ["dependent-memories"], QuarantinedMemories = 2, DependentMemories = DependentMemoryActions.Delete,
        });

        Assert.Contains("are hidden now and are erased when the cleanup finishes", response.Message, StringComparison.Ordinal);
        Assert.Contains("finish automatically", response.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void From_PendingCleanup_SaysItFinishesAutomatically()
    {
        var response = HistoryDeletionResponse.From(new HistoryPurgeResultDto
        {
            Deleted = 1, ErasureComplete = false, CleanupPending = ["messages-wal"],
        });

        Assert.False(response.ErasureComplete);
        Assert.Equal(["messages-wal"], response.CleanupPending);
        Assert.Contains("finish automatically", response.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("memories", response.Message, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run — expect a compile failure / failing assertions:**

```powershell
dotnet test tests\Cortex.Contained.Bridge.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~HistoryPurgeConfirmationTests|FullyQualifiedName~HistoryDeletionResponseTests"
```

- [ ] **Step 3: Create `src/Cortex.Contained.Bridge/Recall/HistoryPurgeConfirmation.cs`:**

```csharp
using System.Globalization;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Recall;

/// <summary>
/// The confirmation the owner reads before a history deletion: how many messages go, how many dependent
/// memories exist, and what the chosen option does with them. Built from the agent's preview.
/// </summary>
public static class HistoryPurgeConfirmation
{
    /// <summary>The text for <paramref name="dependentMemories"/> (<c>review</c> or <c>delete</c>).</summary>
    public static string Describe(HistoryPurgePreviewDto preview, string dependentMemories)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Messages == 0)
        {
            return "Nothing matches: no messages would be deleted.";
        }

        var text = string.Create(CultureInfo.InvariantCulture, $"This deletes {preview.Messages} messages and erases them from the search index.");
        if (preview.DependentMemories == 0)
        {
            return text + " No memories were created in that period.";
        }

        return text + (DependentMemoryActions.IsDelete(dependentMemories)
            ? string.Create(CultureInfo.InvariantCulture, $" {preview.DependentMemories} memories were created in that period. You chose Delete: they are erased permanently together with the history.")
            : string.Create(CultureInfo.InvariantCulture, $" {preview.DependentMemories} memories were created in that period. You chose Review: they are hidden from the agent until you keep or delete them under Recall → Needs review."));
    }
}
```

- [ ] **Step 4: Replace `src/Cortex.Contained.Bridge/Tenants/HistoryDeletionResponse.cs`** (new members are appended with defaults, so the existing JSON fields keep their names):

```csharp
using System.Globalization;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Tenants;

/// <summary>Body of <c>PUT /api/tenants/{tenantId}/history/recall-tools</c>.</summary>
public sealed record RecallToolsRequest(bool Enabled);

/// <summary>
/// JSON returned by the history DELETE endpoints and <c>POST /api/tenants/{tenantId}/recall/purge</c>. Keeps the
/// historical <c>deletedCount</c> field and adds the erasure status plus a ready-to-show confirmation that states
/// what happened to the memories created in the purged period (the owner's per-purge choice).
/// </summary>
public sealed record HistoryDeletionResponse(
    int DeletedCount,
    bool ErasureComplete,
    IReadOnlyList<string> CleanupPending,
    int QuarantinedMemories,
    string Message,
    int DeletedMemories = 0,
    string DependentMemories = DependentMemoryActions.Review)
{
    /// <summary>Builds the response from the agent's purge result.</summary>
    public static HistoryDeletionResponse From(HistoryPurgeResultDto result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var delete = DependentMemoryActions.IsDelete(result.DependentMemories);
        var message = string.Create(CultureInfo.InvariantCulture, $"Deleted {result.Deleted} messages.");
        if (result.DeletedMemories > 0)
        {
            message += string.Create(
                CultureInfo.InvariantCulture,
                $" You chose Delete: {result.DeletedMemories} memories created in that period were erased with the history.");
        }

        if (result.QuarantinedMemories > 0)
        {
            message += delete
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $" You chose Delete: {result.QuarantinedMemories} memories created in that period are hidden now and are erased when the cleanup finishes.")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $" You chose Review: {result.QuarantinedMemories} memories created in that period are hidden from the agent until you keep or delete them under Recall → Needs review.");
        }

        if (!result.ErasureComplete)
        {
            message += " Some cleanup is still running and will finish automatically.";
        }

        return new HistoryDeletionResponse(
            result.Deleted,
            result.ErasureComplete,
            result.CleanupPending,
            result.QuarantinedMemories,
            message,
            result.DeletedMemories,
            delete ? DependentMemoryActions.Delete : DependentMemoryActions.Review);
    }
}
```

- [ ] **Step 5: Edit `Tenants/TenantEndpoints.cs`.** For `DELETE /api/tenants/{tenantId}/history`, replace the comment + lambda header

```csharp
        // DELETE /api/tenants/{tenantId}/history — delete messages older than a date (or all)
        app.MapDelete("/api/tenants/{tenantId}/history", async (
            string tenantId, string? olderThan, TenantRouter router) =>
        {
```

with

```csharp
        // DELETE /api/tenants/{tenantId}/history — delete messages older than a date (or all).
        // ?dependentMemories=review (default) | delete chooses what happens to memories created in that period.
        app.MapDelete("/api/tenants/{tenantId}/history", async (
            string tenantId, string? olderThan, string? dependentMemories, TenantRouter router) =>
        {
            if (!DependentMemoryActions.IsValid(dependentMemories))
            {
                return Results.BadRequest(new { error = "dependentMemories must be review or delete" });
            }

```

and its purge call `new HistoryPurgeRequestDto { OlderThan = cutoff }` with `new HistoryPurgeRequestDto { OlderThan = cutoff, DependentMemories = dependentMemories ?? DependentMemoryActions.Review }`.

For `DELETE /api/tenants/{tenantId}/channels/{channelId}/history`, replace

```csharp
        // DELETE /api/tenants/{tenantId}/channels/{channelId}/history — clear messages for a single channel
        app.MapDelete("/api/tenants/{tenantId}/channels/{channelId}/history", async (
            string tenantId, string channelId, string? olderThan, TenantRouter router) =>
        {
```

with

```csharp
        // DELETE /api/tenants/{tenantId}/channels/{channelId}/history — clear messages for a single channel
        // (?dependentMemories=review|delete as above).
        app.MapDelete("/api/tenants/{tenantId}/channels/{channelId}/history", async (
            string tenantId, string channelId, string? olderThan, string? dependentMemories, TenantRouter router) =>
        {
            if (!DependentMemoryActions.IsValid(dependentMemories))
            {
                return Results.BadRequest(new { error = "dependentMemories must be review or delete" });
            }

```

and its request `new HistoryPurgeRequestDto { ChannelId = decodedChannelId, OlderThan = cutoff == DateTimeOffset.MaxValue ? null : cutoff }` with
`new HistoryPurgeRequestDto { ChannelId = decodedChannelId, OlderThan = cutoff == DateTimeOffset.MaxValue ? null : cutoff, DependentMemories = dependentMemories ?? DependentMemoryActions.Review }`.

- [ ] **Step 6: Run — expect PASS (7 tests).**

- [ ] **Step 7: Commit**

```powershell
git add src/Cortex.Contained.Bridge/Recall/HistoryPurgeConfirmation.cs src/Cortex.Contained.Bridge/Tenants/HistoryDeletionResponse.cs src/Cortex.Contained.Bridge/Tenants/TenantEndpoints.cs tests/Cortex.Contained.Bridge.Tests/Recall/HistoryPurgeConfirmationTests.cs tests/Cortex.Contained.Bridge.Tests/HistoryDeletionResponseTests.cs
git commit -m "feat(bridge): purge confirmations state the dependent-memory choice and counts" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 16: Bridge Recall endpoints + `HubClient` calls

**Files:**
- Create: `src/Cortex.Contained.Bridge/Hub/HubClient.Recall.cs`, `src/Cortex.Contained.Bridge/Recall/IRecallAgentClient.cs`, `src/Cortex.Contained.Bridge/Endpoints/RecallEndpoints.cs`
- Modify: `src/Cortex.Contained.Bridge/Program.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/Recall/RecallEndpointsTests.cs`

- [ ] **Step 1: Write the failing tests** — create `tests/Cortex.Contained.Bridge.Tests/Recall/RecallEndpointsTests.cs`:

```csharp
using System.Text.Json;
using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Bridge.Recall;
using Cortex.Contained.Bridge.Speech;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Recall;

/// <summary>The Recall page API: authorization, disconnected agents, validation and relaying.</summary>
public sealed class RecallEndpointsTests
{
    private readonly IRecallAgentClient agent = Substitute.For<IRecallAgentClient>();

    private static int StatusOf(IResult result)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        return status.StatusCode ?? 200;
    }

    private static JsonElement BodyOf(IResult result)
    {
        var value = result.GetType().GetProperty("Value")!.GetValue(result);
        return JsonSerializer.SerializeToElement(value, value!.GetType(), JsonSerializerOptions.Web);
    }

    [Fact]
    public void AllRecallRoutes_RequireAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();
        app.MapRecallEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        Assert.Equal(10, endpoints.Count);
        Assert.All(endpoints, endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>()));
        Assert.All(endpoints, endpoint => Assert.Contains("/recall", endpoint.RoutePattern.RawText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetSettings_AgentDisconnected_Returns503()
    {
        Assert.Equal(503, StatusOf(await RecallEndpoints.GetSettingsAsync(null, CancellationToken.None)));
    }

    [Fact]
    public async Task UpdateSettings_RejectedByAgent_Returns400WithErrors()
    {
        this.agent.UpdateSettingsAsync(Arg.Any<RecallSettingsPatchDto>(), Arg.Any<CancellationToken>())
            .Returns(new RecallSettingsUpdateResultDto { Applied = false, Errors = ["rerankTimeoutMs must be in [100, 30000]."], Settings = new RecallSettingsDto() });

        var result = await RecallEndpoints.UpdateSettingsAsync(this.agent, new RecallSettingsPatchDto { RerankTimeoutMs = 1 }, CancellationToken.None);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal("rerankTimeoutMs must be in [100, 30000].", BodyOf(result).GetProperty("errors")[0].GetString());
    }

    [Fact]
    public async Task UpdateSettings_Applied_Returns200()
    {
        this.agent.UpdateSettingsAsync(Arg.Any<RecallSettingsPatchDto>(), Arg.Any<CancellationToken>())
            .Returns(new RecallSettingsUpdateResultDto { Applied = true, Settings = new RecallSettingsDto { HistoryEnabled = true } });

        var result = await RecallEndpoints.UpdateSettingsAsync(this.agent, new RecallSettingsPatchDto { HistoryEnabled = true }, CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
    }

    [Theory]
    [InlineData("purge", null, null, "dependentMemories must be review or delete")]
    [InlineData("review", "2026-11-02T00:00:00Z", "2026-11-01T00:00:00Z", "from must be before olderThan")]
    public async Task Purge_InvalidBody_Returns400WithoutCallingTheAgent(string action, string? from, string? olderThan, string error)
    {
        var body = new HistoryPurgeRequestDto
        {
            DependentMemories = action,
            From = from is null ? null : DateTimeOffset.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            OlderThan = olderThan is null ? null : DateTimeOffset.Parse(olderThan, System.Globalization.CultureInfo.InvariantCulture),
        };

        var result = await RecallEndpoints.PurgeAsync(this.agent, body, CancellationToken.None);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal(error, BodyOf(result).GetProperty("error").GetString());
        await this.agent.DidNotReceive().PurgeAsync(Arg.Any<HistoryPurgeRequestDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purge_DeleteChosen_NormalizesAndReportsTheChoice()
    {
        this.agent.PurgeAsync(Arg.Any<HistoryPurgeRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new HistoryPurgeResultDto { Deleted = 4, ErasureComplete = true, DeletedMemories = 2, DependentMemories = DependentMemoryActions.Delete });

        var result = await RecallEndpoints.PurgeAsync(this.agent, new HistoryPurgeRequestDto { ChannelId = " ch ", DependentMemories = "DELETE" }, CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        await this.agent.Received(1).PurgeAsync(
            Arg.Is<HistoryPurgeRequestDto>(r => r.ChannelId == "ch" && r.DependentMemories == DependentMemoryActions.Delete), Arg.Any<CancellationToken>());
        Assert.Contains("You chose Delete: 2 memories", BodyOf(result).GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_ReturnsCountsAndBothConfirmations()
    {
        this.agent.PreviewPurgeAsync(Arg.Any<HistoryPurgeRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new HistoryPurgePreviewDto { Messages = 12, DependentMemories = 3 });

        var body = BodyOf(await RecallEndpoints.PreviewPurgeAsync(this.agent, new HistoryPurgeRequestDto(), CancellationToken.None));

        Assert.Equal(12, body.GetProperty("messages").GetInt32());
        Assert.Equal(3, body.GetProperty("dependentMemories").GetInt32());
        Assert.Contains("You chose Review", body.GetProperty("confirmations").GetProperty("review").GetString(), StringComparison.Ordinal);
        Assert.Contains("erased permanently", body.GetProperty("confirmations").GetProperty("delete").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Relay_AgentRejects_Returns400_TransportFails_Returns503()
    {
        Assert.Equal(400, StatusOf(await RecallEndpoints.RelayAsync<int>(() => throw new HubException("bad"), _ => Results.Ok())));
        Assert.Equal(503, StatusOf(await RecallEndpoints.RelayAsync<int>(() => throw new InvalidOperationException("gone"), _ => Results.Ok())));
    }

    [Fact]
    public async Task Decide_EmptyOrTooManyIds_Returns400()
    {
        var tooMany = Enumerable.Range(0, QuarantineLimits.MaxBatch + 1).Select(i => "m" + i).ToList();

        Assert.Equal(400, StatusOf(await RecallEndpoints.DecideAsync(this.agent, new QuarantineIdsRequest([]), keep: true, CancellationToken.None)));
        Assert.Equal(400, StatusOf(await RecallEndpoints.DecideAsync(this.agent, new QuarantineIdsRequest(tooMany), keep: false, CancellationToken.None)));
        await this.agent.DidNotReceive().DeleteAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Decide_Delete_RelaysTheIds()
    {
        this.agent.DeleteAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(new QuarantineDecisionResultDto { Affected = 2 });

        var result = await RecallEndpoints.DecideAsync(this.agent, new QuarantineIdsRequest(["a", "b"]), keep: false, CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        await this.agent.Received(1).DeleteAsync(Arg.Is<IReadOnlyList<string>>(ids => ids.Count == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void SetReranker_PersistsThenStartsConverging()
    {
        var config = new BridgeConfig();
        var persisted = new List<bool>();
        var runner = Substitute.For<IRerankerComposeRunner>();
        using var reranker = new RerankerSwitch(
            config,
            c => persisted.Add(c.Reranker.Enabled),
            new RerankerSidecarLifecycle(runner, () => "k", NullLogger<RerankerSidecarLifecycle>.Instance),
            _ => Task.CompletedTask,
            NullLogger<RerankerSwitch>.Instance);
        var converging = 0;

        var result = RecallEndpoints.SetReranker(reranker, new RerankerToggleRequest(true), _ => converging++);

        Assert.Equal(200, StatusOf(result));
        Assert.True(config.Reranker.Enabled);
        Assert.Equal([true], persisted);
        Assert.Equal(1, converging);
    }
}
```

- [ ] **Step 2: Run — expect a compile failure:**

```powershell
dotnet test tests\Cortex.Contained.Bridge.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~RecallEndpointsTests"
```

- [ ] **Step 3: Create `src/Cortex.Contained.Bridge/Hub/HubClient.Recall.cs`:**

```csharp
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cortex.Contained.Bridge.Hub;

/// <summary>Recall-page calls into the agent (<see cref="IRecallHub"/> plus the purge and backfill methods).</summary>
public sealed partial class HubClient
{
    /// <summary>The agent's effective recall settings.</summary>
    public async Task<RecallSettingsDto> GetRecallSettingsAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<RecallSettingsDto>(nameof(IAgentHub.GetRecallSettings), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates, persists and applies a recall settings change on the agent.</summary>
    public async Task<RecallSettingsUpdateResultDto> UpdateRecallSettingsAsync(RecallSettingsPatchDto patch, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<RecallSettingsUpdateResultDto>(nameof(IAgentHub.UpdateRecallSettings), patch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>History index, embeddings, reranker, backfill and quarantine state.</summary>
    public async Task<RecallStatusDto> GetRecallStatusAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<RecallStatusDto>(nameof(IAgentHub.GetRecallStatus), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What a history deletion would remove.</summary>
    public async Task<HistoryPurgePreviewDto> PreviewHistoryPurgeAsync(HistoryPurgeRequestDto request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<HistoryPurgePreviewDto>(nameof(IAgentHub.PreviewHistoryPurge), request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Quarantined memories with the purge that quarantined each.</summary>
    public async Task<QuarantineReviewListDto> ListQuarantineReviewAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<QuarantineReviewListDto>(nameof(IAgentHub.ListQuarantineReview), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Bulk keep of quarantined memories.</summary>
    public async Task<QuarantineDecisionResultDto> KeepQuarantinedMemoriesAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<QuarantineDecisionResultDto>(nameof(IAgentHub.KeepQuarantinedMemories), memoryIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Bulk delete of quarantined memories (one batched delete on the agent).</summary>
    public async Task<QuarantineDecisionResultDto> DeleteQuarantinedMemoriesAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<QuarantineDecisionResultDto>(nameof(IAgentHub.DeleteQuarantinedMemories), memoryIds, cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Create `src/Cortex.Contained.Bridge/Recall/IRecallAgentClient.cs`:**

```csharp
using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Recall;

/// <summary>The agent calls the Recall endpoints make (a seam over <see cref="HubClient"/> for tests).</summary>
public interface IRecallAgentClient
{
    Task<RecallSettingsDto> GetSettingsAsync(CancellationToken cancellationToken);

    Task<RecallSettingsUpdateResultDto> UpdateSettingsAsync(RecallSettingsPatchDto patch, CancellationToken cancellationToken);

    Task<RecallStatusDto> GetStatusAsync(CancellationToken cancellationToken);

    Task<HistoryPurgePreviewDto> PreviewPurgeAsync(HistoryPurgeRequestDto request, CancellationToken cancellationToken);

    Task<HistoryPurgeResultDto> PurgeAsync(HistoryPurgeRequestDto request, CancellationToken cancellationToken);

    Task<QuarantineReviewListDto> ListQuarantineAsync(CancellationToken cancellationToken);

    Task<QuarantineDecisionResultDto> KeepAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken);

    Task<QuarantineDecisionResultDto> DeleteAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken);
}

/// <summary>Adapts a connected <see cref="HubClient"/> to <see cref="IRecallAgentClient"/>.</summary>
public sealed class HubRecallAgentClient(HubClient client) : IRecallAgentClient
{
    /// <summary>The adapter for <paramref name="client"/>, or null when it is missing or disconnected.</summary>
    public static IRecallAgentClient? For(HubClient? client) => client is { IsConnected: true } ? new HubRecallAgentClient(client) : null;

    public Task<RecallSettingsDto> GetSettingsAsync(CancellationToken cancellationToken) => client.GetRecallSettingsAsync(cancellationToken);

    public Task<RecallSettingsUpdateResultDto> UpdateSettingsAsync(RecallSettingsPatchDto patch, CancellationToken cancellationToken) =>
        client.UpdateRecallSettingsAsync(patch, cancellationToken);

    public Task<RecallStatusDto> GetStatusAsync(CancellationToken cancellationToken) => client.GetRecallStatusAsync(cancellationToken);

    public Task<HistoryPurgePreviewDto> PreviewPurgeAsync(HistoryPurgeRequestDto request, CancellationToken cancellationToken) =>
        client.PreviewHistoryPurgeAsync(request, cancellationToken);

    public Task<HistoryPurgeResultDto> PurgeAsync(HistoryPurgeRequestDto request, CancellationToken cancellationToken) =>
        client.PurgeHistoryAsync(request, cancellationToken);

    public Task<QuarantineReviewListDto> ListQuarantineAsync(CancellationToken cancellationToken) => client.ListQuarantineReviewAsync(cancellationToken);

    public Task<QuarantineDecisionResultDto> KeepAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken) =>
        client.KeepQuarantinedMemoriesAsync(memoryIds, cancellationToken);

    public Task<QuarantineDecisionResultDto> DeleteAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken) =>
        client.DeleteQuarantinedMemoriesAsync(memoryIds, cancellationToken);
}
```

- [ ] **Step 5: Create `src/Cortex.Contained.Bridge/Endpoints/RecallEndpoints.cs`:**

```csharp
using Cortex.Contained.Bridge.Recall;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// The Recall page API (all require authorization). Per tenant, relayed to that tenant's agent:
/// <c>/api/tenants/{tenantId}/recall/settings</c> (GET, PUT), <c>/status</c>, <c>/purge/preview</c>,
/// <c>/purge</c>, <c>/quarantine</c>, <c>/quarantine/keep</c>, <c>/quarantine/delete</c>. Bridge-wide:
/// <c>/api/recall/reranker</c> (GET, PUT) — the reranker sidecar is shared by every tenant.
/// The handlers are internal static seams so they are tested without a web host.
/// </summary>
internal static class RecallEndpoints
{
    /// <summary>Longest channel id accepted in a purge body.</summary>
    internal const int MaxChannelIdLength = 256;

    /// <summary>Maps the Recall endpoints onto <paramref name="app"/>.</summary>
    public static void MapRecallEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tenants/{tenantId}/recall/settings", (string tenantId, [FromServices] TenantRouter router, CancellationToken ct) =>
            GetSettingsAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), ct)).RequireAuthorization();

        app.MapPut("/api/tenants/{tenantId}/recall/settings", (string tenantId, RecallSettingsPatchDto patch, [FromServices] TenantRouter router, CancellationToken ct) =>
            UpdateSettingsAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), patch, ct)).RequireAuthorization();

        app.MapGet("/api/tenants/{tenantId}/recall/status", (string tenantId, [FromServices] TenantRouter router, [FromServices] RerankerSwitch reranker, CancellationToken ct) =>
            GetStatusAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), reranker.Current, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/recall/purge/preview", (string tenantId, HistoryPurgeRequestDto body, [FromServices] TenantRouter router, CancellationToken ct) =>
            PreviewPurgeAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), body, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/recall/purge", (string tenantId, HistoryPurgeRequestDto body, [FromServices] TenantRouter router, CancellationToken ct) =>
            PurgeAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), body, ct)).RequireAuthorization();

        app.MapGet("/api/tenants/{tenantId}/recall/quarantine", (string tenantId, [FromServices] TenantRouter router, CancellationToken ct) =>
            ListQuarantineAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/recall/quarantine/keep", (string tenantId, QuarantineIdsRequest body, [FromServices] TenantRouter router, CancellationToken ct) =>
            DecideAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), body, keep: true, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/recall/quarantine/delete", (string tenantId, QuarantineIdsRequest body, [FromServices] TenantRouter router, CancellationToken ct) =>
            DecideAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), body, keep: false, ct)).RequireAuthorization();

        app.MapGet("/api/recall/reranker", ([FromServices] RerankerSwitch reranker) => Results.Ok(reranker.Current)).RequireAuthorization();

        app.MapPut("/api/recall/reranker", (RerankerToggleRequest body, [FromServices] RerankerSwitch reranker) =>
            SetReranker(reranker, body, converge => _ = Task.Run(() => converge(CancellationToken.None), CancellationToken.None))).RequireAuthorization();
    }

    internal static Task<IResult> GetSettingsAsync(IRecallAgentClient? agent, CancellationToken ct) =>
        agent is null ? Task.FromResult(NotConnected()) : RelayAsync(() => agent.GetSettingsAsync(ct), Results.Ok);

    internal static Task<IResult> UpdateSettingsAsync(IRecallAgentClient? agent, RecallSettingsPatchDto? patch, CancellationToken ct)
    {
        if (patch is null)
        {
            return Task.FromResult(BadRequest("Request body is required"));
        }

        return agent is null
            ? Task.FromResult(NotConnected())
            : RelayAsync(
                () => agent.UpdateSettingsAsync(patch, ct),
                result => result.Applied ? Results.Ok(result) : Results.Json(result, statusCode: 400));
    }

    internal static Task<IResult> GetStatusAsync(IRecallAgentClient? agent, RerankerSwitchView reranker, CancellationToken ct) =>
        agent is null
            ? Task.FromResult(Results.Json(new { connected = false, reranker }, statusCode: 503))
            : RelayAsync(() => agent.GetStatusAsync(ct), status => Results.Ok(new { connected = true, agent = status, reranker }));

    internal static Task<IResult> PreviewPurgeAsync(IRecallAgentClient? agent, HistoryPurgeRequestDto? body, CancellationToken ct)
    {
        if (ValidatePurge(body) is { } error)
        {
            return Task.FromResult(BadRequest(error));
        }

        return agent is null
            ? Task.FromResult(NotConnected())
            : RelayAsync(
                () => agent.PreviewPurgeAsync(Normalize(body!), ct),
                preview => Results.Ok(new
                {
                    preview.Messages,
                    preview.DependentMemories,
                    preview.RangeFrom,
                    preview.RangeTo,
                    Confirmations = new
                    {
                        Review = HistoryPurgeConfirmation.Describe(preview, DependentMemoryActions.Review),
                        Delete = HistoryPurgeConfirmation.Describe(preview, DependentMemoryActions.Delete),
                    },
                }));
    }

    internal static Task<IResult> PurgeAsync(IRecallAgentClient? agent, HistoryPurgeRequestDto? body, CancellationToken ct)
    {
        if (ValidatePurge(body) is { } error)
        {
            return Task.FromResult(BadRequest(error));
        }

        return agent is null
            ? Task.FromResult(NotConnected())
            : RelayAsync(() => agent.PurgeAsync(Normalize(body!), ct), result => Results.Ok(HistoryDeletionResponse.From(result)));
    }

    internal static Task<IResult> ListQuarantineAsync(IRecallAgentClient? agent, CancellationToken ct) =>
        agent is null ? Task.FromResult(NotConnected()) : RelayAsync(() => agent.ListQuarantineAsync(ct), Results.Ok);

    internal static Task<IResult> DecideAsync(IRecallAgentClient? agent, QuarantineIdsRequest? body, bool keep, CancellationToken ct)
    {
        if (body?.MemoryIds is not { Count: > 0 } ids)
        {
            return Task.FromResult(BadRequest("memoryIds must list at least one memory"));
        }

        if (ids.Count > QuarantineLimits.MaxBatch)
        {
            return Task.FromResult(BadRequest($"At most {QuarantineLimits.MaxBatch} memories per request"));
        }

        return agent is null
            ? Task.FromResult(NotConnected())
            : RelayAsync(() => keep ? agent.KeepAsync(ids, ct) : agent.DeleteAsync(ids, ct), Results.Ok);
    }

    /// <summary>Persists the switch, starts converging (sidecar + grants) and returns the new view.</summary>
    internal static IResult SetReranker(RerankerSwitch reranker, RerankerToggleRequest? body, Action<Func<CancellationToken, Task>> startConverge)
    {
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(startConverge);
        if (body is null)
        {
            return BadRequest("Request body is required");
        }

        try
        {
            reranker.Set(body.Enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Results.Json(new { error = "Could not save cortex.yml: " + ex.Message }, statusCode: 500);
        }

        startConverge(reranker.ConvergeAsync);
        return Results.Ok(reranker.Current);
    }

    /// <summary>Null when <paramref name="body"/> is a usable purge request; otherwise the error.</summary>
    internal static string? ValidatePurge(HistoryPurgeRequestDto? body)
    {
        if (body is null)
        {
            return "Request body is required";
        }

        if (!DependentMemoryActions.IsValid(body.DependentMemories))
        {
            return "dependentMemories must be review or delete";
        }

        if (body.From is { } from && body.OlderThan is { } olderThan && from >= olderThan)
        {
            return "from must be before olderThan";
        }

        if (body.ChannelId is { } channel && (channel.Length > MaxChannelIdLength || channel.Any(char.IsControl)))
        {
            return "channelId is invalid";
        }

        return null;
    }

    /// <summary>Runs an agent call: agent-side rejections become 400, a lost connection 503.</summary>
    internal static async Task<IResult> RelayAsync<T>(Func<Task<T>> call, Func<T, IResult> ok)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(ok);
        T value;
        try
        {
            value = await call().ConfigureAwait(false);
        }
        catch (HubException ex)
        {
            return BadRequest(ex.Message);
        }
#pragma warning disable CA1031 // Any transport failure is reported as "agent not available", like the other tenant endpoints
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Json(new { error = "Agent not available: " + ex.Message }, statusCode: 503);
        }
#pragma warning restore CA1031

        return ok(value);
    }

    private static HistoryPurgeRequestDto Normalize(HistoryPurgeRequestDto body) => body with
    {
        ChannelId = string.IsNullOrWhiteSpace(body.ChannelId) ? null : body.ChannelId.Trim(),
        DependentMemories = DependentMemoryActions.IsDelete(body.DependentMemories) ? DependentMemoryActions.Delete : DependentMemoryActions.Review,
    };

    private static IResult BadRequest(string error) => Results.Json(new { error }, statusCode: 400);

    private static IResult NotConnected() => Results.Json(new { error = "Agent not connected" }, statusCode: 503);
}

/// <summary>Body of the quarantine keep/delete endpoints.</summary>
public sealed record QuarantineIdsRequest(IReadOnlyList<string>? MemoryIds);

/// <summary>Body of <c>PUT /api/recall/reranker</c>.</summary>
public sealed record RerankerToggleRequest(bool Enabled);

/// <summary>Limits shared by the quarantine endpoints and the agent.</summary>
public static class QuarantineLimits
{
    /// <summary>Largest id list per request (the agent enforces the same bound).</summary>
    public const int MaxBatch = 1000;
}
```

- [ ] **Step 6: Edit `src/Cortex.Contained.Bridge/Program.cs`** — after `app.MapMemoryEndpoints(cortexConfigPath);` add (top-level, no indentation):

```csharp

// --- Recall page API (per-tenant recall settings/status/purge/quarantine + Bridge-wide reranker switch) ---
app.MapRecallEndpoints();
```

- [ ] **Step 7: Run — expect PASS (12 tests)**, then all Bridge recall tests:

```powershell
dotnet test tests\Cortex.Contained.Bridge.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Bridge.Tests.Recall|FullyQualifiedName~HistoryDeletionResponseTests|FullyQualifiedName~RerankerSidecarLifecycleTests"
```

Expected: 32 passed.

- [ ] **Step 8: Commit**

```powershell
git add src/Cortex.Contained.Bridge/Hub/HubClient.Recall.cs src/Cortex.Contained.Bridge/Recall/IRecallAgentClient.cs src/Cortex.Contained.Bridge/Endpoints/RecallEndpoints.cs src/Cortex.Contained.Bridge/Program.cs tests/Cortex.Contained.Bridge.Tests/Recall/RecallEndpointsTests.cs
git commit -m "feat(bridge): recall settings, status, purge and review endpoints" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---
### Task 17: UI — Recall page (settings, reranker, status, delete history, needs review)

**Files:**
- Modify: `src/Cortex.Contained.Bridge/wwwroot/js/components/layout.js`, `src/Cortex.Contained.Bridge/wwwroot/app.html`
- Create: `src/Cortex.Contained.Bridge/wwwroot/js/components/recall-purge.js`, `src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-recall.js`

No JS test harness exists in this repo (see gotchas); verification is `node --check` + build + the manual smoke in Task 20.

- [ ] **Step 1: Route.** In `layout.js` replace

```js
            // /tenants/{id}/settings|history|memory
            const match = path.match(/^\/tenants\/([^/]+)\/(settings|history|memory)$/);
```

with

```js
            // /tenants/{id}/settings|history|memory|recall
            const match = path.match(/^\/tenants\/([^/]+)\/(settings|history|memory|recall)$/);
```

and in the `tenantTabs()` doc comment `(breadcrumb back-link + Settings/History/Memory).` → `(breadcrumb back-link + Settings/History/Memory/Recall).` (The Bridge already serves `app.html` for `/tenants/{**slug}`.)

- [ ] **Step 2: Create `src/Cortex.Contained.Bridge/wwwroot/js/components/recall-purge.js`:**

```js
"use strict";

/**
 * Shared history-deletion helper for the History, channel-history and Recall pages.
 * Every deletion first asks the agent for a preview (messages + dependent memories) and shows the
 * confirmation for the owner's choice of what happens to memories created in that period:
 * "review" (hidden until kept or deleted, the default) or "delete" (erased with the history).
 */
const recallPurge = {
    base(tenantId) {
        return `/api/tenants/${encodeURIComponent(tenantId)}/recall`;
    },

    /** Builds the request body. scope: { channelId?, from?, olderThan? } with datetime-local strings or ISO. */
    body(scope, dependentMemories) {
        const iso = (v) => (v ? new Date(v).toISOString() : null);
        return {
            channelId: scope.channelId || null,
            from: iso(scope.from),
            olderThan: iso(scope.olderThan),
            dependentMemories: dependentMemories === "delete" ? "delete" : "review",
        };
    },

    async preview(tenantId, scope, dependentMemories) {
        return api.post(`${this.base(tenantId)}/purge/preview`, this.body(scope, dependentMemories));
    },

    async run(tenantId, scope, dependentMemories) {
        return api.post(`${this.base(tenantId)}/purge`, this.body(scope, dependentMemories));
    },

    /** The confirmation sentence for the chosen option (server-built from the preview). */
    confirmation(preview, dependentMemories) {
        if (!preview) return "";
        return dependentMemories === "delete" ? preview.confirmations?.delete : preview.confirmations?.review;
    },

    /** "from … to …" in the browser's locale, or "". */
    range(preview) {
        if (!preview?.rangeFrom) return "";
        const f = (ts) => new Date(ts).toLocaleString();
        return `${f(preview.rangeFrom)} – ${f(preview.rangeTo)}`;
    },
};
```

- [ ] **Step 3: Create `src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-recall.js`:**

```js
"use strict";

/**
 * Per-tenant Recall page: history search / embeddings / reranker / memory-search settings, live status
 * of the recall stack, history deletion with the dependent-memory choice, and the review of quarantined
 * memories (bulk keep / delete).
 */
function tenantRecallPage() {
    return {
        get tenantId() { return Alpine.store("router").tenantId; },
        get base() { return `/api/tenants/${encodeURIComponent(this.tenantId)}/recall`; },

        // ── Settings ─────────────────────────────────────────────
        settings: null,
        form: null,
        saving: false,
        notices: [],
        settingsError: "",

        // ── Reranker (Bridge-wide) ───────────────────────────────
        reranker: null,
        rerankerBusy: false,

        // ── Status ───────────────────────────────────────────────
        status: null,
        connected: true,
        statusTimer: null,
        backfillRunning: false,

        // ── Delete history ───────────────────────────────────────
        channels: [],
        purge: { channelId: "", time: "all", from: "", olderThan: "", dependentMemories: "review" },
        preview: null,
        previewError: "",
        previewing: false,
        showPurgeModal: false,
        purging: false,

        // ── Needs review ─────────────────────────────────────────
        quarantine: [],
        selected: {},
        expanded: {},
        reviewBusy: false,
        showBulkDeleteModal: false,

        async init() {
            await Promise.all([this.loadSettings(), this.loadStatus(), this.loadChannels(), this.loadQuarantine()]);
            this.statusTimer = setInterval(() => this.loadStatus(), 5000);
            await this.refreshPreview();
        },

        destroy() {
            if (this.statusTimer) clearInterval(this.statusTimer);
        },

        // ── Settings ─────────────────────────────────────────────
        async loadSettings() {
            try {
                this.settings = await api.get(`${this.base}/settings`);
                this.form = { ...this.settings };
                this.settingsError = "";
            } catch (e) {
                this.settingsError = e.message;
            }
        },

        isOverridden(name) {
            return (this.settings?.overridden || []).includes(name);
        },

        changedPatch() {
            const patch = {};
            for (const key of [
                "historyEnabled", "historySearchMode", "historyDenseEnabled", "memorySearchMode",
                "rerankHistoryTopN", "rerankHistoryMaxDocChars", "rerankMemoryTopN", "rerankMemoryMaxDocChars", "rerankTimeoutMs",
            ]) {
                let value = this.form[key];
                if (typeof this.settings[key] === "number") value = Number(value);
                if (value !== this.settings[key]) patch[key] = value;
            }
            return patch;
        },

        get dirty() {
            return this.form && this.settings && Object.keys(this.changedPatch()).length > 0;
        },

        async saveSettings(reset = false) {
            this.saving = true;
            this.notices = [];
            try {
                const body = reset ? { resetToDefaults: true } : this.changedPatch();
                const result = await api.put(`${this.base}/settings`, body);
                this.settings = result.settings;
                this.form = { ...result.settings };
                this.notices = result.notices || [];
                this.settingsError = "";
                Alpine.store("toast").success(reset ? "Recall settings reset to defaults" : "Recall settings saved");
                await this.loadStatus();
            } catch (e) {
                this.settingsError = e.body?.errors?.join(" ") || e.message;
                Alpine.store("toast").error("Not saved: " + this.settingsError);
            }
            this.saving = false;
        },

        async setReranker(enabled) {
            this.rerankerBusy = true;
            try {
                this.reranker = await api.put("/api/recall/reranker", { enabled });
                Alpine.store("toast").success(enabled
                    ? "Reranker switched on; the sidecar is starting (see Status)"
                    : "Reranker switched off");
            } catch (e) {
                Alpine.store("toast").error("Reranker: " + e.message);
            }
            this.rerankerBusy = false;
            setTimeout(() => this.loadStatus(), 1500);
        },

        // ── Status ───────────────────────────────────────────────
        async loadStatus() {
            try {
                const data = await api.get(`${this.base}/status`);
                this.status = data?.agent || null;
                this.reranker = data?.reranker || this.reranker;
                this.connected = true;
                this.backfillRunning = this.status?.eventBackfill?.running || false;
            } catch (e) {
                this.connected = false;
                this.reranker = e.body?.reranker || this.reranker;
            }
        },

        percent(part, total) {
            return total > 0 ? Math.round((100 * part) / total) : 0;
        },

        formatDateTime(ts) {
            return ts ? new Date(ts).toLocaleString() : "—";
        },

        sidecarBadge() {
            const state = this.reranker?.sidecar?.state;
            return state === "running" ? "bg-success"
                : state === "failed" ? "bg-danger"
                : state === "starting" ? "bg-warning text-dark"
                : "bg-secondary";
        },

        async runBackfill() {
            if (!confirm("Run the event backfill now? Recall and history deletions pause while it runs (usually seconds).")) return;
            this.backfillRunning = true;
            try {
                const r = await api.post(`/api/tenants/${encodeURIComponent(this.tenantId)}/history/event-backfill`);
                Alpine.store("toast").success(`Event backfill: ${r.inserted} of ${r.scanned} messages received an event (${r.elapsedMs} ms)`);
            } catch (e) {
                Alpine.store("toast").error("Event backfill failed: " + e.message);
            }
            await this.loadStatus();
        },

        // ── Delete history ───────────────────────────────────────
        async loadChannels() {
            try {
                const data = await api.get(`/api/tenants/${encodeURIComponent(this.tenantId)}/channels`);
                this.channels = Array.isArray(data) ? data : [];
            } catch {
                this.channels = [];
            }
        },

        purgeScope() {
            return {
                channelId: this.purge.channelId || null,
                from: this.purge.time === "between" ? this.purge.from : null,
                olderThan: this.purge.time === "all" ? null : this.purge.olderThan,
            };
        },

        purgeScopeError() {
            if (this.purge.time !== "all" && !this.purge.olderThan) return "Choose the end date.";
            if (this.purge.time === "between" && !this.purge.from) return "Choose the start date.";
            if (this.purge.time === "between" && new Date(this.purge.from) >= new Date(this.purge.olderThan)) {
                return "The start date must be before the end date.";
            }
            return "";
        },

        async refreshPreview() {
            this.preview = null;
            this.previewError = this.purgeScopeError();
            if (this.previewError) return;
            this.previewing = true;
            try {
                this.preview = await recallPurge.preview(this.tenantId, this.purgeScope(), this.purge.dependentMemories);
            } catch (e) {
                this.previewError = e.message;
            }
            this.previewing = false;
        },

        get confirmation() {
            return recallPurge.confirmation(this.preview, this.purge.dependentMemories);
        },

        get previewRange() {
            return recallPurge.range(this.preview);
        },

        async confirmPurge() {
            this.purging = true;
            try {
                const resp = await recallPurge.run(this.tenantId, this.purgeScope(), this.purge.dependentMemories);
                Alpine.store("toast").success(resp?.message ?? "History deleted");
                this.showPurgeModal = false;
                await Promise.all([this.loadChannels(), this.loadQuarantine(), this.loadStatus()]);
                await this.refreshPreview();
            } catch (e) {
                Alpine.store("toast").error("Delete failed: " + e.message);
            }
            this.purging = false;
        },

        prettyName(id) { return prettifyChannelId(id); },

        // ── Needs review ─────────────────────────────────────────
        async loadQuarantine() {
            try {
                const data = await api.get(`${this.base}/quarantine`);
                this.quarantine = data?.items || [];
            } catch {
                this.quarantine = [];
            }
            const ids = new Set(this.quarantine.map((q) => q.memory.memoryId));
            for (const id of Object.keys(this.selected)) {
                if (!ids.has(id)) delete this.selected[id];
            }
        },

        get selectedIds() {
            return Object.keys(this.selected).filter((id) => this.selected[id]);
        },

        get allSelected() {
            return this.quarantine.length > 0 && this.selectedIds.length === this.quarantine.length;
        },

        toggleAll(checked) {
            this.selected = {};
            if (checked) {
                for (const q of this.quarantine) this.selected[q.memory.memoryId] = true;
            }
        },

        preview300(text) {
            return text && text.length > 300 ? text.slice(0, 300) + "…" : text;
        },

        purgeSource(q) {
            const where = q.purgeChannelId ? this.prettyName(q.purgeChannelId) : "all channels";
            return `From a deletion of ${where} on ${this.formatDateTime(q.purgedAt)}`;
        },

        async decide(ids, keep) {
            if (ids.length === 0) return;
            this.reviewBusy = true;
            try {
                const r = await api.post(`${this.base}/quarantine/${keep ? "keep" : "delete"}`, { memoryIds: ids });
                const skipped = r.skipped?.length ? ` (${r.skipped.length} no longer in review)` : "";
                Alpine.store("toast").success(`${keep ? "Kept" : "Deleted"} ${r.affected} memories${skipped}`);
                this.showBulkDeleteModal = false;
                await Promise.all([this.loadQuarantine(), this.loadStatus()]);
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
            this.reviewBusy = false;
        },
    };
}
```

- [ ] **Step 4: Edit `app.html` — navigation.**
  1. Sidebar: after the tenant **Memory** link (the `<a>` whose label is `Memory`, ending `</a>`) add:

```html
                                <a :href="`/tenants/${encodeURIComponent(t.tenantId)}/recall`"
                                   class="sidebar-item sidebar-sub-item"
                                   :class="{ active: page === 'tenant-recall' && $store.router.tenantId === t.tenantId }"
                                   @click.prevent="nav(`/tenants/${encodeURIComponent(t.tenantId)}/recall`)">
                                    <span class="sidebar-item-icon"><i class="bi bi-search-heart"></i></span>
                                    <span class="sidebar-item-label">Recall</span>
                                </a>
```

  2. Sub-nav guard: `['tenant-settings','tenant-history','tenant-channel-history','tenant-memory'].includes($store.router.page)` → `['tenant-settings','tenant-history','tenant-channel-history','tenant-memory','tenant-recall'].includes($store.router.page)`.
  3. Tab row: after the **Memory** tab `<button>` add:

```html
                        <button class="btn btn-sm"
                                :class="isActive('recall') ? 'btn-primary' : 'btn-outline-secondary'"
                                :disabled="isActive('recall')" @click="navSub('recall')">
                            <i class="bi bi-search-heart me-1"></i>Recall
                        </button>
```

  4. Scripts: after `<script src="/js/components/channel-helpers.js"></script>` add `<script src="/js/components/recall-purge.js"></script>`; after `<script src="/js/pages/tenant-memory.js"></script>` add `<script src="/js/pages/tenant-recall.js"></script>`.

- [ ] **Step 5: Edit `app.html` — the page.** Insert immediately before `        <!-- ── Global Settings ────────────────────────────────── -->` (i.e. after the closing `</template>` of the Tenant Memory page):

```html
        <!-- ── Tenant Recall (history search, embeddings, reranker, deletion, review) ── -->
        <template x-if="$store.router.page === 'tenant-recall'">
        <div x-data="tenantRecallPage()" class="container pb-3" style="max-width: 860px;">
            <!-- Sub-nav is rendered above by the shared tenantTabs() block. -->

            <div x-show="!connected" class="alert alert-warning py-2">
                <i class="bi bi-plug me-1"></i>The agent is not connected; settings and status are unavailable.
            </div>

            <!-- ═══ Settings ═══ -->
            <div class="card mb-3" x-show="form">
                <div class="card-body">
                    <h6 class="text-uppercase text-secondary border-bottom pb-2 mb-3">Recall settings</h6>
                    <template x-if="form">
                    <div>
                        <div class="form-check form-switch mb-2">
                            <input class="form-check-input" type="checkbox" id="recall-history-enabled" x-model="form.historyEnabled">
                            <label class="form-check-label" for="recall-history-enabled">History search (<code>history_search</code>)</label>
                            <span x-show="isOverridden('historyEnabled')" class="badge bg-info text-dark ms-1">custom</span>
                        </div>
                        <div class="row g-2 mb-2">
                            <div class="col-sm-6">
                                <label class="form-label small mb-1" for="recall-history-mode">Default history search mode
                                    <span x-show="isOverridden('historySearchMode')" class="badge bg-info text-dark ms-1">custom</span></label>
                                <select id="recall-history-mode" class="form-select form-select-sm" x-model="form.historySearchMode">
                                    <option value="auto">Hybrid (keyword + semantic when embeddings are available)</option>
                                    <option value="hybrid">Hybrid (same as auto)</option>
                                    <option value="keyword">Keyword only</option>
                                </select>
                            </div>
                            <div class="col-sm-6 d-flex align-items-end">
                                <div class="form-check form-switch">
                                    <input class="form-check-input" type="checkbox" id="recall-dense" x-model="form.historyDenseEnabled">
                                    <label class="form-check-label" for="recall-dense">Dense embeddings for history</label>
                                    <span x-show="isOverridden('historyDenseEnabled')" class="badge bg-info text-dark ms-1">custom</span>
                                </div>
                            </div>
                        </div>
                        <p class="small text-body-secondary mb-3">
                            Turning dense embeddings on or off rebuilds the history index; until it finishes, searches cover only recent messages.
                        </p>

                        <div class="mb-3" style="max-width: 360px;">
                            <label class="form-label small mb-1" for="recall-memory-mode">Default memory search mode
                                <span x-show="isOverridden('memorySearchMode')" class="badge bg-info text-dark ms-1">custom</span></label>
                            <select id="recall-memory-mode" class="form-select form-select-sm" x-model="form.memorySearchMode">
                                <option value="dense">Semantic (dense)</option>
                                <option value="hybrid">Hybrid (keyword + semantic)</option>
                                <option value="keyword">Keyword only</option>
                            </select>
                        </div>

                        <div class="fw-semibold small mb-1">Reranker budget (this tenant)</div>
                        <div class="row g-2 mb-2">
                            <div class="col-6 col-md">
                                <label class="form-label small mb-0" for="rr-h-topn">History top N</label>
                                <input id="rr-h-topn" type="number" min="1" max="50" class="form-control form-control-sm" x-model.number="form.rerankHistoryTopN">
                            </div>
                            <div class="col-6 col-md">
                                <label class="form-label small mb-0" for="rr-h-chars">History chars</label>
                                <input id="rr-h-chars" type="number" min="200" max="4000" step="100" class="form-control form-control-sm" x-model.number="form.rerankHistoryMaxDocChars">
                            </div>
                            <div class="col-6 col-md">
                                <label class="form-label small mb-0" for="rr-m-topn">Memory top N</label>
                                <input id="rr-m-topn" type="number" min="1" max="50" class="form-control form-control-sm" x-model.number="form.rerankMemoryTopN">
                            </div>
                            <div class="col-6 col-md">
                                <label class="form-label small mb-0" for="rr-m-chars">Memory chars</label>
                                <input id="rr-m-chars" type="number" min="200" max="4000" step="100" class="form-control form-control-sm" x-model.number="form.rerankMemoryMaxDocChars">
                            </div>
                            <div class="col-6 col-md">
                                <label class="form-label small mb-0" for="rr-timeout">Timeout (ms)</label>
                                <input id="rr-timeout" type="number" min="100" max="30000" step="100" class="form-control form-control-sm" x-model.number="form.rerankTimeoutMs">
                            </div>
                        </div>

                        <div x-show="settingsError" class="alert alert-danger py-1 px-2 small" x-text="settingsError"></div>
                        <template x-for="n in notices" :key="n">
                            <div class="alert alert-info py-1 px-2 small mb-1" x-text="n"></div>
                        </template>

                        <div class="d-flex gap-2 mt-2">
                            <button class="btn btn-sm btn-primary" :disabled="!dirty || saving" @click="saveSettings()">
                                <span x-show="saving" class="spinner-border spinner-border-sm me-1"></span>Save
                            </button>
                            <button class="btn btn-sm btn-outline-secondary" :disabled="saving || !(settings?.overridden?.length)"
                                    @click="saveSettings(true)" title="Drop every custom value; the configured defaults apply again">
                                Reset to defaults
                            </button>
                        </div>
                    </div>
                    </template>
                </div>
            </div>

            <!-- ═══ Reranker (Bridge-wide) ═══ -->
            <div class="card mb-3">
                <div class="card-body">
                    <h6 class="text-uppercase text-secondary border-bottom pb-2 mb-3">Reranker (shared by all tenants)</h6>
                    <div class="d-flex align-items-center gap-2 flex-wrap">
                        <div class="form-check form-switch mb-0">
                            <input class="form-check-input" type="checkbox" id="recall-reranker" :checked="reranker?.enabled"
                                   :disabled="rerankerBusy" @change="setReranker($event.target.checked)">
                            <label class="form-check-label" for="recall-reranker">Rerank hybrid results (cortex-reranker sidecar)</label>
                        </div>
                        <span class="badge" :class="sidecarBadge()" x-text="'sidecar: ' + (reranker?.sidecar?.state || 'unknown')"></span>
                    </div>
                    <div x-show="reranker?.sidecar?.problem" class="alert alert-danger py-1 px-2 small mt-2 mb-0" x-text="reranker?.sidecar?.problem"></div>
                </div>
            </div>

            <!-- ═══ Status ═══ -->
            <div class="card mb-3" x-show="status">
                <div class="card-body small">
                    <h6 class="text-uppercase text-secondary border-bottom pb-2 mb-3">Status <span class="fw-normal text-lowercase">(refreshes every 5 s)</span></h6>
                    <template x-if="status">
                    <div>
                        <div class="fw-semibold">History index</div>
                        <div class="mb-2">
                            <span class="badge" :class="status.history.enabled ? 'bg-success' : 'bg-secondary'" x-text="status.history.enabled ? 'on' : 'off'"></span>
                            <span class="badge bg-secondary" x-text="'storage: ' + status.history.storage"></span>
                            <span class="badge" :class="status.history.buildComplete ? 'bg-success' : 'bg-warning text-dark'"
                                  x-text="status.history.buildComplete ? 'build complete' : (status.history.buildInProgress ? 'building…' : 'not built')"></span>
                            <span x-show="status.history.halted" class="badge bg-danger">halted</span>
                            <span class="ms-1" x-text="'generation ' + (status.history.generation ?? '—') + ' · checkpoint lag ' + status.history.checkpointLag + ' · journal backlog ' + status.history.journalBacklog"></span>
                        </div>
                        <div x-show="status.history.lastError" class="text-danger mb-2" x-text="'Last error: ' + status.history.lastError"></div>
                        <div x-show="status.history.pendingErasureTasks > 0" class="text-warning mb-2"
                             x-text="'Erasure still running: ' + status.history.pendingErasureSteps.join(', ')"></div>

                        <div class="fw-semibold">Dense embeddings</div>
                        <div class="mb-2">
                            <span class="badge" :class="status.dense.state === 'on' ? 'bg-success' : (status.dense.state === 'off' ? 'bg-secondary' : 'bg-danger')" x-text="status.dense.state"></span>
                            <span class="ms-1" x-text="status.dense.embedded + ' / ' + status.dense.total + ' chunks embedded · ' + status.dense.pending + ' pending · ' + status.dense.failed + ' failed'"></span>
                            <div class="progress mt-1" style="height: 6px;" x-show="status.dense.total > 0">
                                <div class="progress-bar" :style="'width:' + percent(status.dense.embedded, status.dense.total) + '%'"></div>
                            </div>
                            <div x-show="status.dense.oldestPendingQueuedAt" class="text-body-secondary" x-text="'Oldest pending since ' + formatDateTime(status.dense.oldestPendingQueuedAt)"></div>
                            <div x-show="status.dense.lastError" class="text-danger" x-text="'Last error: ' + status.dense.lastError"></div>
                        </div>

                        <div class="fw-semibold">Reranker (this agent)</div>
                        <div class="mb-2">
                            <span class="badge" :class="status.reranker.state === 'ok' ? 'bg-success' : (status.reranker.state === 'off' ? 'bg-secondary' : 'bg-danger')" x-text="status.reranker.state"></span>
                            <span class="ms-1" x-show="status.reranker.lastLatencyMs != null" x-text="'last call ' + Math.round(status.reranker.lastLatencyMs) + ' ms at ' + formatDateTime(status.reranker.lastCallAt)"></span>
                            <span class="ms-1" x-show="status.reranker.consecutiveFailures > 0" x-text="status.reranker.consecutiveFailures + ' consecutive failures'"></span>
                            <div x-show="status.reranker.lastError" class="text-danger" x-text="'Last error: ' + status.reranker.lastError"></div>
                        </div>

                        <div class="fw-semibold">Event backfill</div>
                        <div class="d-flex align-items-center gap-2 mb-2">
                            <span x-text="status.eventBackfill.lastRunAt
                                ? 'Last run ' + formatDateTime(status.eventBackfill.lastRunAt) + ': ' + status.eventBackfill.lastInserted + ' of ' + status.eventBackfill.lastScanned + ' messages backfilled'
                                : 'Not run since the agent started'"></span>
                            <button class="btn btn-sm btn-outline-secondary" :disabled="backfillRunning" @click="runBackfill()">
                                <span x-show="backfillRunning" class="spinner-border spinner-border-sm me-1"></span>Run event backfill
                            </button>
                        </div>

                        <div>
                            <span class="fw-semibold">Needs review:</span>
                            <span x-text="status.quarantinedMemories + ' memories'"></span>
                            <a href="#recall-review" class="ms-1" x-show="status.quarantinedMemories > 0">review below</a>
                        </div>
                    </div>
                    </template>
                </div>
            </div>

            <!-- ═══ Delete history ═══ -->
            <div class="card mb-3">
                <div class="card-body">
                    <h6 class="text-uppercase text-secondary border-bottom pb-2 mb-3">Delete history</h6>
                    <div class="row g-2 mb-2">
                        <div class="col-sm-6">
                            <label class="form-label small mb-1" for="purge-channel">Channel</label>
                            <select id="purge-channel" class="form-select form-select-sm" x-model="purge.channelId" @change="refreshPreview()">
                                <option value="">All channels</option>
                                <template x-for="ch in channels" :key="ch.id">
                                    <option :value="ch.id" x-text="prettyName(ch.id) + ' (' + ch.messageCount + ')'"></option>
                                </template>
                            </select>
                        </div>
                        <div class="col-sm-6">
                            <label class="form-label small mb-1" for="purge-time">Time</label>
                            <select id="purge-time" class="form-select form-select-sm" x-model="purge.time" @change="refreshPreview()">
                                <option value="all">Everything</option>
                                <option value="older">Older than…</option>
                                <option value="between">Between…</option>
                            </select>
                        </div>
                    </div>
                    <div class="row g-2 mb-2" x-show="purge.time !== 'all'">
                        <div class="col-sm-6" x-show="purge.time === 'between'">
                            <label class="form-label small mb-1" for="purge-from">From</label>
                            <input id="purge-from" type="datetime-local" class="form-control form-control-sm" x-model="purge.from" @change="refreshPreview()">
                        </div>
                        <div class="col-sm-6">
                            <label class="form-label small mb-1" for="purge-until" x-text="purge.time === 'between' ? 'Until' : 'Older than'"></label>
                            <input id="purge-until" type="datetime-local" class="form-control form-control-sm" x-model="purge.olderThan" @change="refreshPreview()">
                        </div>
                    </div>
                    <div class="mb-2">
                        <div class="small fw-semibold mb-1">Memories created in that period</div>
                        <div class="form-check">
                            <input class="form-check-input" type="radio" id="dep-review" value="review" x-model="purge.dependentMemories">
                            <label class="form-check-label small" for="dep-review">Review them (hidden from the agent until I keep or delete them)</label>
                        </div>
                        <div class="form-check">
                            <input class="form-check-input" type="radio" id="dep-delete" value="delete" x-model="purge.dependentMemories">
                            <label class="form-check-label small" for="dep-delete">Delete them with the history (permanent)</label>
                        </div>
                    </div>
                    <div class="small mb-2">
                        <span x-show="previewing" class="spinner-border spinner-border-sm me-1"></span>
                        <span x-show="previewError" class="text-danger" x-text="previewError"></span>
                        <span x-show="preview && !previewError" x-text="confirmation"></span>
                        <div x-show="previewRange" class="text-body-secondary" x-text="previewRange"></div>
                    </div>
                    <button class="btn btn-sm btn-outline-danger" :disabled="!preview || preview.messages === 0 || previewing" @click="showPurgeModal = true">
                        <i class="bi bi-trash me-1"></i>Delete…
                    </button>
                </div>
            </div>

            <!-- Purge confirmation modal -->
            <div x-show.important="showPurgeModal" class="modal d-block" tabindex="-1" style="background: rgba(0,0,0,0.5);">
                <div class="modal-dialog">
                    <div class="modal-content">
                        <div class="modal-header">
                            <h5 class="modal-title">Delete history</h5>
                            <button type="button" class="btn-close" @click="showPurgeModal = false"></button>
                        </div>
                        <div class="modal-body">
                            <p x-text="confirmation"></p>
                            <p class="small text-body-secondary mb-0" x-text="previewRange"></p>
                        </div>
                        <div class="modal-footer">
                            <button class="btn btn-outline-secondary btn-sm" @click="showPurgeModal = false">Cancel</button>
                            <button class="btn btn-danger btn-sm" @click="confirmPurge()" :disabled="purging">
                                <span x-show="!purging" x-text="purge.dependentMemories === 'delete' ? 'Delete history and memories' : 'Delete history'"></span>
                                <span x-show="purging"><span class="spinner-border spinner-border-sm"></span> Deleting...</span>
                            </button>
                        </div>
                    </div>
                </div>
            </div>

            <!-- ═══ Needs review ═══ -->
            <div class="card mb-3 border-warning" id="recall-review">
                <div class="card-body">
                    <h6 class="text-uppercase text-secondary border-bottom pb-2 mb-2">
                        Needs review (<span x-text="quarantine.length"></span>)
                    </h6>
                    <p class="small text-body-secondary">
                        Memories created while history you deleted was recent. They are hidden from the agent until you keep or delete them.
                    </p>
                    <div x-show="quarantine.length === 0" class="text-body-secondary small">Nothing to review.</div>
                    <div x-show="quarantine.length > 0" class="d-flex align-items-center gap-2 mb-2">
                        <div class="form-check mb-0">
                            <input class="form-check-input" type="checkbox" id="review-all" :checked="allSelected" @change="toggleAll($event.target.checked)">
                            <label class="form-check-label small" for="review-all">Select all</label>
                        </div>
                        <button class="btn btn-sm btn-outline-success" :disabled="selectedIds.length === 0 || reviewBusy" @click="decide(selectedIds, true)">
                            <i class="bi bi-check-lg"></i> Keep selected (<span x-text="selectedIds.length"></span>)
                        </button>
                        <button class="btn btn-sm btn-outline-danger" :disabled="selectedIds.length === 0 || reviewBusy" @click="showBulkDeleteModal = true">
                            <i class="bi bi-trash"></i> Delete selected (<span x-text="selectedIds.length"></span>)
                        </button>
                    </div>
                    <template x-for="q in quarantine" :key="'rq-' + q.memory.memoryId">
                        <div class="d-flex gap-2 align-items-start border-top pt-2 mt-2">
                            <input class="form-check-input mt-1" type="checkbox" :id="'rq-' + q.memory.memoryId" x-model="selected[q.memory.memoryId]">
                            <div class="flex-grow-1">
                                <div x-show="q.memory.title" class="fw-semibold" x-text="q.memory.title"></div>
                                <div style="white-space: pre-wrap; font-size:var(--text-base);"
                                     x-text="expanded[q.memory.memoryId] ? q.memory.content : preview300(q.memory.content)"></div>
                                <a href="#" class="small" x-show="q.memory.content && q.memory.content.length > 300"
                                   @click.prevent="expanded[q.memory.memoryId] = !expanded[q.memory.memoryId]"
                                   x-text="expanded[q.memory.memoryId] ? 'Show less' : 'Show all'"></a>
                                <div class="small text-body-secondary">
                                    <span x-text="'Created ' + formatDateTime(q.memory.createdAt)"></span> ·
                                    <span x-text="purgeSource(q)"></span>
                                </div>
                            </div>
                            <div class="d-flex gap-1 flex-shrink-0">
                                <button class="btn btn-sm btn-outline-success" :disabled="reviewBusy" @click="decide([q.memory.memoryId], true)" title="Keep">
                                    <i class="bi bi-check-lg"></i>
                                </button>
                                <button class="btn btn-sm btn-outline-danger" :disabled="reviewBusy" @click="decide([q.memory.memoryId], false)" title="Delete">
                                    <i class="bi bi-trash"></i>
                                </button>
                            </div>
                        </div>
                    </template>
                </div>
            </div>

            <!-- Bulk delete confirmation -->
            <div x-show.important="showBulkDeleteModal" class="modal d-block" tabindex="-1" style="background: rgba(0,0,0,0.5);">
                <div class="modal-dialog modal-sm">
                    <div class="modal-content">
                        <div class="modal-header">
                            <h5 class="modal-title">Delete memories</h5>
                            <button type="button" class="btn-close" @click="showBulkDeleteModal = false"></button>
                        </div>
                        <div class="modal-body" x-text="'Delete ' + selectedIds.length + ' memories permanently?'"></div>
                        <div class="modal-footer">
                            <button class="btn btn-secondary btn-sm" @click="showBulkDeleteModal = false">Cancel</button>
                            <button class="btn btn-danger btn-sm" :disabled="reviewBusy" @click="decide(selectedIds, false)">Delete</button>
                        </div>
                    </div>
                </div>
            </div>
        </div>
        </template>
```

- [ ] **Step 6: Verify**

```powershell
node --check src\Cortex.Contained.Bridge\wwwroot\js\components\recall-purge.js
node --check src\Cortex.Contained.Bridge\wwwroot\js\pages\tenant-recall.js
node --check src\Cortex.Contained.Bridge\wwwroot\js\components\layout.js
dotnet build src\Cortex.Contained.Bridge -p:NuGetAudit=false
```

Expected: no `node` output, build succeeded with 0 warnings.

- [ ] **Step 7: Commit**

```powershell
git add src/Cortex.Contained.Bridge/wwwroot/js/components/layout.js src/Cortex.Contained.Bridge/wwwroot/js/components/recall-purge.js src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-recall.js src/Cortex.Contained.Bridge/wwwroot/app.html
git commit -m "feat(ui): tenant Recall page for recall settings, status, deletion and review" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 18: UI — History/channel Clear modals use preview + choice; Memory page link

**Files:**
- Modify: `src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-history.js`, `src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-channel-history.js`, `src/Cortex.Contained.Bridge/wwwroot/app.html`

- [ ] **Step 1: `tenant-history.js`** — replace everything from `        // ── Clear all history modal` up to (not including) the closing `    };` of the returned object with:

```js
        // ── Clear all history modal ──────────────────────────────────
        showClearModal: false,
        clearOlderThan: "",
        clearDependentMemories: "review", // "review" (quarantine) or "delete" — the owner's choice per purge
        clearPreview: null,
        clearPreviewError: "",
        clearing: false,

        async openClearModal() {
            const d = new Date();
            d.setDate(d.getDate() - 7);
            this.clearOlderThan = d.toISOString().slice(0, 16);
            this.clearDependentMemories = "review";
            this.showClearModal = true;
            await this.refreshClearPreview();
        },

        clearScope() {
            return { olderThan: this.clearOlderThan || null };
        },

        async refreshClearPreview() {
            this.clearPreview = null;
            this.clearPreviewError = "";
            try {
                this.clearPreview = await recallPurge.preview(this.tenantId, this.clearScope(), this.clearDependentMemories);
            } catch (e) {
                this.clearPreviewError = e.message;
            }
        },

        get clearConfirmation() {
            return recallPurge.confirmation(this.clearPreview, this.clearDependentMemories);
        },

        async confirmClear() {
            this.clearing = true;
            try {
                const resp = await recallPurge.run(this.tenantId, this.clearScope(), this.clearDependentMemories);
                Alpine.store("toast").success(resp?.message ?? `Deleted ${resp?.deletedCount ?? 0} messages`);
                this.showClearModal = false;
                await this.loadChannels();
            } catch (e) {
                Alpine.store("toast").error("Failed to clear history: " + e.message);
            }
            this.clearing = false;
        },
```

- [ ] **Step 2: `tenant-channel-history.js`** — replace everything from `        // ── Clear-this-channel modal` up to (not including) `        // ── Pagination` with:

```js
        // ── Clear-this-channel modal ──────────────────────────────────
        showClearModal: false,
        clearScope: "older", // "older" or "all"
        clearOlderThan: "",
        clearDependentMemories: "review", // "review" (quarantine) or "delete" — the owner's choice per purge
        clearPreview: null,
        clearPreviewError: "",
        clearing: false,

        async openClearModal() {
            // Default to 7 days ago — matches the "Clear History" modal on the overview.
            const d = new Date();
            d.setDate(d.getDate() - 7);
            this.clearOlderThan = d.toISOString().slice(0, 16);
            this.clearScope = "older";
            this.clearDependentMemories = "review";
            this.showClearModal = true;
            await this.refreshClearPreview();
        },

        purgeScope() {
            return {
                channelId: this.channelId,
                olderThan: this.clearScope === "older" ? this.clearOlderThan || null : null,
            };
        },

        async refreshClearPreview() {
            this.clearPreview = null;
            this.clearPreviewError = "";
            try {
                this.clearPreview = await recallPurge.preview(this.tenantId, this.purgeScope(), this.clearDependentMemories);
            } catch (e) {
                this.clearPreviewError = e.message;
            }
        },

        get clearConfirmation() {
            return recallPurge.confirmation(this.clearPreview, this.clearDependentMemories);
        },

        async confirmClear() {
            this.clearing = true;
            try {
                const resp = await recallPurge.run(this.tenantId, this.purgeScope(), this.clearDependentMemories);
                Alpine.store("toast").success(resp?.message ?? "Channel history cleared");
                this.showClearModal = false;
                this.msgOffset = 0;
                await this.loadMessages();
            } catch (e) {
                Alpine.store("toast").error("Failed to clear channel history: " + e.message);
            }
            this.clearing = false;
        },
```

- [ ] **Step 3: `app.html` — "Clear all history" modal body.** Replace

```html
                            <p>Delete all messages older than:</p>
                            <input type="datetime-local" class="form-control" x-model="clearOlderThan">
                            <p class="small text-body-secondary mt-2 mb-0">
                                Deleted messages are also erased from the agent's search index.
                                Memories the agent created during that period are hidden until you
                                review them under Memory → Needs review; they are never deleted automatically.
                            </p>
```

with

```html
                            <p>Delete all messages older than:</p>
                            <input type="datetime-local" class="form-control" x-model="clearOlderThan" @change="refreshClearPreview()">
                            <div class="small fw-semibold mt-3 mb-1">Memories created in that period</div>
                            <div class="form-check">
                                <input class="form-check-input" type="radio" id="clear-all-dep-review" value="review" x-model="clearDependentMemories">
                                <label class="form-check-label small" for="clear-all-dep-review">Review them (hidden until I keep or delete them)</label>
                            </div>
                            <div class="form-check">
                                <input class="form-check-input" type="radio" id="clear-all-dep-delete" value="delete" x-model="clearDependentMemories">
                                <label class="form-check-label small" for="clear-all-dep-delete">Delete them with the history (permanent)</label>
                            </div>
                            <p class="small mt-2 mb-0">
                                <span x-show="clearPreviewError" class="text-danger" x-text="clearPreviewError"></span>
                                <span x-show="!clearPreviewError" x-text="clearConfirmation"></span>
                            </p>
```

- [ ] **Step 4: `app.html` — "Clear channel history" modal.** Add `@change="refreshClearPreview()"` to the `clear-scope-older` radio (`value="older" x-model="clearScope"` → `value="older" x-model="clearScope" @change="refreshClearPreview()"`). Then replace

```html
                            <input type="datetime-local" class="form-control mb-2"
                                   x-model="clearOlderThan" :disabled="clearScope !== 'older'">
                            <div class="form-check">
                                <input class="form-check-input" type="radio" id="clear-scope-all"
                                       value="all" x-model="clearScope">
                                <label class="form-check-label" for="clear-scope-all">
                                    All messages in this channel
                                </label>
                            </div>
                            <p class="small text-body-secondary mt-2 mb-0">
                                Deleted messages are also erased from the agent's search index.
                                Memories the agent created during that period are hidden until you
                                review them under Memory → Needs review; they are never deleted automatically.
                            </p>
```

with

```html
                            <input type="datetime-local" class="form-control mb-2"
                                   x-model="clearOlderThan" :disabled="clearScope !== 'older'" @change="refreshClearPreview()">
                            <div class="form-check">
                                <input class="form-check-input" type="radio" id="clear-scope-all"
                                       value="all" x-model="clearScope" @change="refreshClearPreview()">
                                <label class="form-check-label" for="clear-scope-all">
                                    All messages in this channel
                                </label>
                            </div>
                            <div class="small fw-semibold mt-3 mb-1">Memories created in that period</div>
                            <div class="form-check">
                                <input class="form-check-input" type="radio" id="clear-ch-dep-review" value="review" x-model="clearDependentMemories">
                                <label class="form-check-label small" for="clear-ch-dep-review">Review them (hidden until I keep or delete them)</label>
                            </div>
                            <div class="form-check">
                                <input class="form-check-input" type="radio" id="clear-ch-dep-delete" value="delete" x-model="clearDependentMemories">
                                <label class="form-check-label small" for="clear-ch-dep-delete">Delete them with the history (permanent)</label>
                            </div>
                            <p class="small mt-2 mb-0">
                                <span x-show="clearPreviewError" class="text-danger" x-text="clearPreviewError"></span>
                                <span x-show="!clearPreviewError" x-text="clearConfirmation"></span>
                            </p>
```

- [ ] **Step 5: `app.html` — Memory page "Needs review" card text.** Replace

```html
                    <small class="text-body-secondary d-block mb-1">
                        Created while history you deleted was recent. Hidden from the agent until you
                        keep or delete them. Never deleted automatically.
                    </small>
```

with

```html
                    <small class="text-body-secondary d-block mb-1">
                        Created while history you deleted was recent. Hidden from the agent until you
                        keep or delete them.
                        <a :href="`/tenants/${encodeURIComponent(tenantId)}/recall#recall-review`"
                           @click.prevent="Alpine.store('router').navigate(`/tenants/${encodeURIComponent(tenantId)}/recall`)">Review them all on the Recall page</a>
                        (select several, keep or delete in bulk).
                    </small>
```

- [ ] **Step 6: Verify** — `node --check` both page scripts; `Select-String -Path src\Cortex.Contained.Bridge\wwwroot\app.html -Pattern 'never deleted automatically'` → no match; `dotnet build src\Cortex.Contained.Bridge -p:NuGetAudit=false` → 0 warnings.

- [ ] **Step 7: Commit**

```powershell
git add src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-history.js src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-channel-history.js src/Cortex.Contained.Bridge/wwwroot/app.html
git commit -m "feat(ui): history clear dialogs preview the deletion and ask about dependent memories" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 19: Docs — API reference, history locator, reranker, setup guide, help, master spec D3/D10

**Files:**
- Modify: `docs/api-reference.md`, `docs/history-locator.md`, `docs/reranker.md`, `docs/setup-guide.md`, `docs/superpowers/specs/2026-11-21-episodic-history-memory-design.md`, `src/Cortex.Contained.Bridge/wwwroot/help-content/manifest.json`
- Create: `src/Cortex.Contained.Bridge/wwwroot/help-content/settings/recall-settings.md`

- [ ] **Step 1: `docs/api-reference.md` — Bridge table.** Replace the six rows from `| DELETE | `/api/tenants/{tenantId}/history` |` through `| POST | `/api/tenants/{tenantId}/memories/{memoryId}/keep` |` with:

```markdown
| DELETE | `/api/tenants/{tenantId}/history` | Yes | Delete all (or `?olderThan=`) history through the erasure workflow. `?dependentMemories=review` (default) quarantines the memories created in that period, `delete` erases them with the history. Returns `{ deletedCount, erasureComplete, cleanupPending, quarantinedMemories, deletedMemories, dependentMemories, message }` |
| DELETE | `/api/tenants/{tenantId}/channels/{channelId}/history` | Yes | Same for one channel (previously `204`; now returns the same body; same `dependentMemories` query) |
| PUT | `/api/tenants/{tenantId}/history/recall-tools` | Yes | Body `{ enabled }`: show/hide `history_search` / recall-mode `history_read` at runtime (no effect unless `History:Enabled`) |
| POST | `/api/tenants/{tenantId}/history/event-backfill` | Yes | Run the explicit event backfill (maintenance window). Returns `{ scanned, inserted, elapsedMs }`. Driven by `scripts/Invoke-HistoryBackfill.ps1` and the Recall page |
| GET | `/api/tenants/{tenantId}/memories/quarantine` | Yes | Memories quarantined after a history deletion (D10), awaiting review |
| POST | `/api/tenants/{tenantId}/memories/{memoryId}/keep` | Yes | Keep a quarantined memory (visible to the agent again) |
| GET | `/api/tenants/{tenantId}/recall/settings` | Yes | Effective recall settings of the tenant's agent: `{ historyEnabled, historySearchMode, historyDenseEnabled, memorySearchMode, rerankHistoryTopN, rerankHistoryMaxDocChars, rerankMemoryTopN, rerankMemoryMaxDocChars, rerankTimeoutMs, overridden[] }`. `503` while the agent is disconnected |
| PUT | `/api/tenants/{tenantId}/recall/settings` | Yes | Patch (null members unchanged; `resetToDefaults: true` drops every override first). Persisted in the agent's state volume (`recall-settings.json`) and applied live. Returns `{ applied, errors[], notices[], settings }`; `400` with the same body when validation fails |
| GET | `/api/tenants/{tenantId}/recall/status` | Yes | `{ connected, agent: { settings, history, dense, reranker, eventBackfill, quarantinedMemories }, reranker: { enabled, sidecar: { state, problem, at } } }` (`503` with `connected: false` and the reranker block while disconnected) |
| POST | `/api/tenants/{tenantId}/recall/purge/preview` | Yes | Body `{ channelId?, from?, olderThan?, dependentMemories? }`. Deletes nothing. Returns `{ messages, dependentMemories, rangeFrom, rangeTo, confirmations: { review, delete } }` |
| POST | `/api/tenants/{tenantId}/recall/purge` | Yes | Same body; deletes through the erasure workflow (`from` inclusive, `olderThan` exclusive; `from ≥ olderThan` is `400`). Returns the history DELETE body |
| GET | `/api/tenants/{tenantId}/recall/quarantine` | Yes | `{ items: [{ memory, purgeId, purgedAt, purgeChannelId, windowFrom, windowTo }] }`, newest first |
| POST | `/api/tenants/{tenantId}/recall/quarantine/keep` | Yes | Body `{ memoryIds: [...] }` (1–1000). Keeps the quarantined ones; returns `{ affected, skipped[] }` |
| POST | `/api/tenants/{tenantId}/recall/quarantine/delete` | Yes | Same body; erases the quarantined ones in one batch (`DeleteManyAsync`); ids not in quarantine are skipped |
| GET | `/api/recall/reranker` | Yes | Bridge-wide reranker switch and sidecar state `{ enabled, sidecar: { state, problem, at } }` |
| PUT | `/api/recall/reranker` | Yes | Body `{ enabled }`. Persists `reranker: enabled` in `cortex.yml`, then (in the background) starts the sidecar and grants every agent, or withdraws the grant and stops the sidecar |
```

- [ ] **Step 2: `docs/api-reference.md` — hub methods.** Replace the `#### `PurgeHistory(HistoryPurgeRequestDto)` -> `HistoryPurgeResultDto`` section (up to `#### `SetHistoryRecallTools(enabled)` -> `bool``) with:

```markdown
#### `PurgeHistory(HistoryPurgeRequestDto)` -> `HistoryPurgeResultDto`

Delete history (optional `channelId`; optional `from` (inclusive) and `olderThan` (exclusive);
none = everything) through the erasure workflow. `dependentMemories` is `review` (default:
curated memories created in the purged period are quarantined) or `delete` (they are erased
with the history by the `dependent-memories` erasure step; until it completes they stay
quarantined). Returns `{ deleted, erasureComplete, cleanupPending[], quarantinedMemories,
deletedMemories, dependentMemories }`. An inverted range or an unknown action is a `HubException`.
All other delete methods (`DeleteConversation`, `ClearAllMessages`, `DeleteMessagesOlderThan`,
`DeleteChannelMessagesOlderThan`, `ClearAll`, `ImportMessages` with `clearExisting`) use the
same workflow (always `review`).

#### Recall (`IRecallHub`)

| Method | Description |
|--------|-------------|
| `GetRecallSettings()` -> `RecallSettingsDto` | Effective settings (configured defaults + persisted overrides; `overridden[]` names the overrides) |
| `UpdateRecallSettings(RecallSettingsPatchDto)` -> `RecallSettingsUpdateResultDto` | Validate, persist (`<state>/recall-settings.json`) and apply live; returns errors or notices (index rebuild, tools hidden, …) |
| `GetRecallStatus()` -> `RecallStatusDto` | History index build, dense embedding progress, reranker health, event backfill, quarantined count |
| `PreviewHistoryPurge(HistoryPurgeRequestDto)` -> `HistoryPurgePreviewDto` | Messages and dependent memories a purge would affect; deletes nothing |
| `ListQuarantineReview()` -> `QuarantineReviewListDto` | Quarantined memories with the purge (id, time, channel, window) that quarantined each |
| `KeepQuarantinedMemories(ids)` / `DeleteQuarantinedMemories(ids)` -> `QuarantineDecisionResultDto` | Bulk keep / bulk erase (one `DeleteManyAsync` batch); at most 1000 ids; ids not in quarantine are returned as `skipped` |
```

- [ ] **Step 3: `docs/history-locator.md`.**
  1. Configuration table: replace the `Enabled` row with these two rows:

```markdown
| `Enabled` | `false` | Runs the projector and exposes the recall tools. Switchable at runtime on the Recall page (see below). |
| `DefaultSearchMode` | `Auto` | What `history_search mode=auto` (the default) means: `Auto`/`Hybrid` = hybrid when embeddings are available, `Keyword` = keyword-only unless the model asks for `mode=hybrid`. Query time. |
```

  2. After the paragraph that ends `…reports `buildComplete=false`.` (just before `### Time zone in the agent container`) insert:

```markdown
### Runtime settings (Bridge → tenant → Recall)

The Recall page changes `History:Enabled`, `History:DefaultSearchMode`, `History:Dense:Enabled`,
`MemoryMcp:Search:DefaultMode` and the reranker budgets (`Reranker:History|Memory:TopN|MaxDocChars`,
`Reranker:TimeoutMs`) per tenant without a restart. The agent stores the owner's choices in
`<state>/recall-settings.json` (`RecallSettingsStore`; the state volume, outside the tool sandbox) and
overlays them on the configured values through `IPostConfigureOptions` plus a change token, so every
`IOptionsMonitor` consumer sees them at once. A value that is not overridden keeps its configured
default (`appsettings.json`, `History__*` …); **Reset to defaults** deletes every override. The compose
file is not used for this: the Launcher rewrites it on every start.

- **Switching History on** starts the projector within a second (the hosted service polls the switch):
  it resumes the current generation or builds one; watch **Status** until the build completes.
- **Switching History off** hides the recall tools immediately and pauses the projector. The index stays
  on disk until the next erasure pass deletes it (as in *Rollout → Rollback*); maintenance prunes the
  unconsumed journal, so switching back on rebuilds the index (the projector notices that its checkpoint
  lies below `JournalPrunedThrough`).
- **Dense on/off** changes the config fingerprint: the index is rebuilt (keyword-only tail until it cuts
  over), then embedded in the background.
- Search modes and reranker budgets apply to the next search.
```

  3. Deletion section: replace the opening sentence `All delete paths (Bridge history page, channel history, tenant import with *clear existing*, `DeleteConversation`) go through `HistoryPurgeService`:` with:

```markdown
All delete paths (Bridge history page, channel history, the Recall page's **Delete history**, tenant
import with *clear existing*, `DeleteConversation`) go through `HistoryPurgeService`. A purge covers one
channel or all, and everything, everything older than a date, or a date range (`from` inclusive,
`olderThan` exclusive). The UI first asks for a preview (`PreviewHistoryPurge`: messages and dependent
memories) and states the owner's choice for the dependent memories before confirming:
```

  4. In the step list: `` `smoke-artifacts` (overlapping `runs/*` directories of the smoke and `history-eval` workspaces), `vacuum` `` → insert `` `dependent-memories` (only when the owner chose to delete them), `` before `` `vacuum` ``; and `` `cleanupPending` and the number of quarantined memories. `` → `` `cleanupPending` and the number of quarantined or deleted memories. ``
  5. Replace the whole `### Memory quarantine (D10)` section (up to `## Rollout`) with:

```markdown
### Memory quarantine (D10)

Curated memories may have been extracted from deleted conversations. Memories whose `CreatedAt`
falls in a purged window (clustered with a 6 h gap, extended by 24 h of extraction slack, capped at
the purge time) are the purge's **dependent memories**. The owner chooses per purge:

- **Review** (default): they are hidden from `memory_search`, subagent seeding, consolidation and
  compaction until reviewed. The Recall page's **Needs review** list shows each one with its content,
  creation time and the purge (time, channel) that quarantined it, with per-item and bulk **Keep** /
  **Delete** (bulk delete is one `DeleteManyAsync` batch: one keyword-index rewrite and WAL truncate).
  The Memory page links there.
- **Delete**: the `dependent-memories` erasure step erases them physically with the history (memory
  row, content file, vectors, keyword rows; `secure_delete`). Memories the owner kept earlier are never
  deleted. If the step cannot finish, it stays in `PendingErasure` and the memories stay quarantined
  until it does.

The confirmation names the chosen option and the number of memories affected.
```

  6. Replace the five `## Rollout` steps with:

```markdown
1. Deploy with `History:Enabled=false` (default) — the journal is written, nothing else runs.
2. Switch **History search** on in Bridge → tenant → **Recall** (or set `History__Enabled=true` /
   `history: enabled: true` and restart). The projector builds generation 1 in bounded batches; watch
   **Status** (or `/health`) until `buildComplete=true`.
3. Add one line to the agent's self-notes: *"history_search finds when/where something was said;
   check current facts elsewhere before acting on a hit."*
4. Run the smoke evaluation and the behaviour gate below before relying on the tools.
5. Rollback: switch History search off on the Recall page (or `History__Enabled=false`) — the tools are
   hidden at once and the next erasure pass deletes every index generation.
```

- [ ] **Step 4: `docs/reranker.md`** — replace `Bridge: `Reranker:Enabled` (false). Agent (`Reranker` section):` (first line under `## Configuration`) with:

```markdown
Bridge: `Reranker:Enabled` (false) — switch it on Bridge → tenant → **Recall → Reranker (shared by all
tenants)**. The switch is written to `cortex.yml` (`reranker: enabled:`); on enable the Bridge first runs
`compose up` for the sidecar and then grants every agent, on disable it withdraws the grant first and then
stops the sidecar. The page shows the sidecar state (`starting`, `running`, `stopped`, `failed`) and, when it
failed, why: the `cortex-reranker:latest` image is not built (build it with `scripts\Build-RerankerImage.ps1`
or `Build-All.ps1 -WithReranker`), compose could not start it (the default CUDA image needs an NVIDIA GPU
with Docker GPU support; build `-Variant cpu` otherwise), or the container exited right after starting.
The agent's own state (`ok`, `circuit-open`, last latency, last error) is shown next to it.

Agent (`Reranker` section; the budgets and the timeout are per-tenant runtime settings on the same page,
stored in the agent's `recall-settings.json`):
```

- [ ] **Step 5: `docs/setup-guide.md`** — after `Memory thresholds can also be changed at runtime via the web UI settings page.` insert:

```markdown
### Recall settings (history search, embeddings, reranker)

Per tenant, under **Tenants → *tenant* → Recall** (no restart, no compose or `cortex.yml` edits):

| Setting | Shipped default | What it does |
|---------|-----------------|--------------|
| History search | off | Builds the history index and gives the agent `history_search` / `event_get`. Switching it on builds the index in the background; switching it off hides the tools at once. |
| Default history search mode | hybrid when embeddings are available | `keyword` keeps searches to exact words unless the agent asks for hybrid. |
| Dense embeddings for history | off | Embeds history chunks for meaning-based (hybrid) search. Changing it rebuilds the index. |
| Default memory search mode | dense (semantic) | `hybrid` or `keyword` for `memory_search mode=auto`. |
| Reranker budget | top 15, 1200 chars, 2000 ms | How many hybrid results the reranker reorders and how long it may take. |
| Reranker (shared) | off | Starts the `cortex-reranker` sidecar for all tenants; needs the image (`scripts\Build-RerankerImage.ps1`). |

Your choices are stored in the agent's state volume (`recall-settings.json`), so they survive restarts
and updates; values you never changed keep following the shipped defaults, and **Reset to defaults**
returns to them. The same page shows the index build, embedding and reranker status, runs the event
backfill, deletes history (one channel or all; everything, older than a date or between two dates) with a
preview and your choice for the memories created in that period (**review** them or **delete** them with
the history), and lists the memories waiting for review with bulk keep/delete. Details:
[history-locator.md](history-locator.md#runtime-settings-bridge--tenant--recall), [reranker.md](reranker.md).
```

- [ ] **Step 6: Help article.** Create `src/Cortex.Contained.Bridge/wwwroot/help-content/settings/recall-settings.md`:

```markdown
# Recall Settings

Found under **Tenants → *your tenant* → Recall**. Recall is how the assistant finds past
conversations (`history_search`) and stored memories (`memory_search`). **Changes take effect
immediately** — no restart needed — and apply to this tenant only (except the reranker switch, which is
shared by all tenants).

## Settings

- **History search** — lets the assistant search its own conversation history. When you switch it on,
  the history index is built in the background (watch **Status**); until it is complete, searches cover
  only recent messages. Switching it off hides the search tools at once.
- **Default history search mode** — *Hybrid* finds matching words and similar meaning (when dense
  embeddings are on); *Keyword only* finds exact words.
- **Dense embeddings for history** — needed for hybrid history search. Changing it rebuilds the
  history index.
- **Default memory search mode** — *Semantic*, *Hybrid* or *Keyword only* for memory searches.
- **Reranker budget** — how many hybrid results the reranker reorders, how much text it reads per result,
  and how long it may take.

Values you changed are marked *custom*. **Reset to defaults** removes all of them.

## Reranker (shared by all tenants)

Starts the `cortex-reranker` sidecar and lets every tenant use it. If it fails, the page says why — most
often the image is not built yet (`scripts\Build-RerankerImage.ps1`) or the machine has no usable GPU
(build the CPU variant with `-Variant cpu`).

## Status

Shows the history index build, the embedding progress, the reranker's health, the last event backfill
(with a button to run it) and how many memories wait for review. It refreshes every 5 seconds.

## Delete history

Choose a channel (or all), and everything, everything older than a date, or a date range. Before you
confirm, Cortex shows how many messages will be deleted and how many memories were created in that
period, and you choose what happens to those memories:

- **Review them** — they are hidden from the assistant until you keep or delete them (below).
- **Delete them with the history** — they are erased permanently together with the messages.

## Needs review

Memories created in a deleted period, with the deletion they came from. Keep or delete them one by one,
or select several (or all) and keep or delete them together.
```

and in `help-content/manifest.json` (Settings Reference) add after the `memory-settings` entry (mind the comma):

```json
        { "id": "recall-settings", "title": "Recall Settings", "file": "settings/recall-settings.md" }
```

- [ ] **Step 7: Master spec D3/D10** (`docs/superpowers/specs/2026-11-21-episodic-history-memory-design.md`) — exact replacements:
  - D3: `Curated memories derived from the deleted range are **flagged for review** (not auto-deleted).` → `Curated memories derived from the deleted range are **flagged for review** by default; the owner may instead choose, per purge, to **delete them with the history** (amended 2026-11-22, Recall settings UI).`
  - D10: `until the owner keeps or deletes them. They are never auto-deleted, and the delete confirmation says so.` → `until the owner keeps or deletes them. They are never auto-deleted unless the owner explicitly chooses, for that purge, to delete them with the history (owner opt-in, amended 2026-11-22); the delete confirmation shows a preview count and states the chosen option.`
  - §5.2 row: `| Curated memories created within a purged window | **Quarantined** (D10) until reviewed in the Memory UI; never auto-deleted |` → `| Curated memories created within a purged window | **Quarantined** (D10) until reviewed on the Recall page (default), or **erased** with the history when the owner chose that for the purge |`
  - §11 step 5: `   - Never auto-deleted.` → `   - Not auto-deleted, unless the owner chose **Delete** for this purge: then the `dependent-memories` erasure step erases them (memory row, content file, vectors, keyword rows; one batch), and they stay quarantined until the step completes.`; `   - The delete confirmation states this.` → `   - The delete confirmation shows the preview count and states the chosen option.`

- [ ] **Step 8: Verify** — `Select-String -Path docs\*.md,docs\superpowers\specs\2026-11-21-episodic-history-memory-design.md -Pattern 'never deleted automatically|never auto-deleted\.'` → no match; the JSON parses: `Get-Content src\Cortex.Contained.Bridge\wwwroot\help-content\manifest.json -Raw | ConvertFrom-Json | Out-Null`.

- [ ] **Step 9: Commit**

```powershell
git add docs/api-reference.md docs/history-locator.md docs/reranker.md docs/setup-guide.md docs/superpowers/specs/2026-11-21-episodic-history-memory-design.md src/Cortex.Contained.Bridge/wwwroot/help-content/settings/recall-settings.md src/Cortex.Contained.Bridge/wwwroot/help-content/manifest.json
git commit -m "docs(recall): document the Recall page, runtime settings and the dependent-memory choice" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 20: Final verification

- [ ] **Step 1: Solution build — 0 warnings**

```powershell
$env:MSBUILDDISABLENODEREUSE=1
dotnet build cortex-contained.sln -p:NuGetAudit=false -nodeReuse:false
```

- [ ] **Step 2: Test projects touched by this plan** (rerun a timing-sensitive test alone if it flakes, e.g. `OnDisconnected_ClosesRecoveryGate`):

```powershell
dotnet test tests\Cortex.Contained.Contracts.Tests -p:NuGetAudit=false --no-build
dotnet test tests\Cortex.Contained.Agent.Host.Tests -p:NuGetAudit=false --no-build
dotnet test tests\Cortex.Contained.Bridge.Tests -p:NuGetAudit=false --no-build
dotnet test tests\Cortex.Contained.Integration.Tests -p:NuGetAudit=false --no-build
```

Replay result (scratch worktree, cumulative state): Contracts 260, Agent.Host 2717, Bridge 1912, Integration 14 — all passed, 0 warnings.

- [ ] **Step 3: Static checks** — `node --check` on `recall-purge.js`, `tenant-recall.js`, `tenant-history.js`, `tenant-channel-history.js`, `layout.js`; `git status --short` shows no `version.json` staged and no stray files.

- [ ] **Step 4: Manual smoke (optional, needs a running install; do not deploy from this plan).** After a Self-Update to the branch build: log in, open *tenant → Recall*: settings load (`custom` badges absent on a fresh install); toggle **History search** on → notice + Status shows `building…` then `build complete`; set *Default history search mode* to Hybrid and *Dense embeddings* on → notice about the rebuild, Status shows embedding progress; switch the shared **Reranker** on → sidecar `starting` → `running` (or `failed` with the image/GPU hint); in **Delete history** pick a test channel and a date range → the confirmation line shows the message and memory counts for *Review* and changes when *Delete* is selected; **Needs review** lists quarantined memories with their purge, select-all + **Keep selected** works. `/health` of the agent shows the same history/reranker blocks.

- [ ] **Step 5: Owner's local choice (this machine only, never committed):** on the Recall page set *Default history search mode* = Hybrid and *Dense embeddings* = on (and History search on). These land in the tenant's `/app/state/recall-settings.json`; shipped defaults stay off/auto/dense.

---

## Spec coverage

| Requirement (spec §1 / decision) | Task(s) |
|---|---|
| History search on/off at runtime (R3, R4) | 4, 5, 12, 17 |
| Default history search mode, owner-local hybrid (R1, R6) | 2, 3, 4, 17, 20 |
| Dense embeddings on/off with rebuild warning (R5) | 4, 12 (notices), 17 |
| Reranker on/off (Bridge-wide), persisted, converged, honest errors (R7, R8) | 13, 14, 16, 17 |
| Reranker budgets per tenant (R2) | 3, 4, 17 |
| Memory search default mode (R2) | 3, 4, 17 |
| Status: index build, embeddings, reranker, backfill, pending erasure, quarantine (R13) | 11, 12, 16, 17 |
| Event backfill button | 11, 17 |
| Delete history: channel / all; everything / older than / between dates (R11) | 6, 16, 17, 18 |
| Preview count before confirming; confirmation names the option (R10) | 8, 15, 16, 17, 18 |
| Per-purge dependent memories: review (default) or delete, physical erasure via `DeleteManyAsync` (R9) | 9, 15, 16, 17, 18 |
| Review UI: content preview, created-at, purge source, per-item and bulk keep/delete, select-all (R12) | 7, 10, 16, 17 |
| Typed hub contract `IRecallHub` (R13) | 1, 12 |
| Bridge endpoints under `/api/tenants/{tenantId}/recall/...`, `RequireAuthorization` (R14) | 16 |
| Persistence consistent with agent state; not compose/env (R1) | 3, 4 |
| Master spec D3/D10 amended; docs (API, history locator, reranker, setup, help) | 19 |
