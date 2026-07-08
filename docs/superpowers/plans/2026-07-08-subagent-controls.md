# Subagent Controls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the max-concurrent-subagents cap admin-editable per tenant and applied live (no restart), and add a `sub_agent_stop` agent tool to cancel a running or queued subagent.

**Architecture:** `SubagentRunnerRegistry` becomes the single source of truth for a live-mutable concurrency cap and per-task cancellation. The Bridge persists the cap to `cortex.yml` and pushes it live over the existing `AgentConfigUpdate` hub path; `AgentRuntime.UpdateConfigAsync` applies it to the registry, which dequeues waiting tasks when raised. Cancellation is a per-task `CancellationTokenSource` owned by the registry; the new `sub_agent_stop` tool cancels a running runner's loop or drops a queued task, transitioning it to a new `Cancelled` terminal state.

**Tech Stack:** .NET 10, C#, xUnit + NSubstitute, SQLite (subagent store), SignalR (Bridge↔agent hub), Alpine.js (Bridge web UI).

## Global Constraints

- **C# style:** `this.`-qualified instance members; braces on all blocks; `sealed` where applicable; `readonly` fields; file-scoped namespaces; source-generated `[LoggerMessage]` for logs (structured, no interpolation); `ConfigureAwait(false)` in agent/library code. One type per file.
- **`TreatWarningsAsErrors` is on** solution-wide — a warning fails the build.
- **Concurrency cap bounds:** `[1, 20]` — copied from `AgentConfig.MaxConcurrentSubagents`'s `[Range(1,20)]`. Default `5`.
- **Test naming:** `Method_Condition_Expected`. Global usings for `Xunit`/`NSubstitute` are set in test `.csproj`s.
- **Git:** end every commit message with a trailer line `Claude-Session: https://claude.ai/code/session_01T8WPqhE2k4hmizysyq4uMg`.
- **Build/test commands:** solution build `dotnet build cortex-contained.sln`; agent tests `dotnet test tests/Cortex.Contained.Agent.Host.Tests`; bridge tests `dotnet test tests/Cortex.Contained.Bridge.Tests`; single test `dotnet test <proj> --filter "Name~<TestName>"`.

---

## Task 1: `Cancelled` subagent state + store retention

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Agent/SubagentTask.cs` (enum + `ToStorageValue`/`Parse`)
- Modify: `src/Cortex.Contained.Agent.Host/Agent/SubagentSessionStore.cs` (`UpdateState` completed_at CASE:188, `Cleanup`:311-330)
- Create: `tests/Cortex.Contained.Agent.Host.Tests/SubagentTaskStateTests.cs`
- Modify: `tests/Cortex.Contained.Agent.Host.Tests/SubagentSessionStoreTests.cs`

**Interfaces:**
- Produces: `SubagentTaskState.Cancelled`; storage value `"cancelled"`. Consumed by Tasks 4 and 5.

- [ ] **Step 1: Write the failing enum round-trip test**

Create `tests/Cortex.Contained.Agent.Host.Tests/SubagentTaskStateTests.cs`:

```csharp
using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tests;

public class SubagentTaskStateTests
{
    [Theory]
    [InlineData(SubagentTaskState.Queued, "queued")]
    [InlineData(SubagentTaskState.Running, "running")]
    [InlineData(SubagentTaskState.Revising, "revising")]
    [InlineData(SubagentTaskState.Completed, "completed")]
    [InlineData(SubagentTaskState.Failed, "failed")]
    [InlineData(SubagentTaskState.Cancelled, "cancelled")]
    public void ToStorageValue_And_Parse_RoundTrip(SubagentTaskState state, string expected)
    {
        Assert.Equal(expected, state.ToStorageValue());
        Assert.Equal(state, SubagentTaskStateExtensions.Parse(expected));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "Name~ToStorageValue_And_Parse_RoundTrip"`
Expected: FAIL — `SubagentTaskState` has no `Cancelled` member (compile error).

- [ ] **Step 3: Add the `Cancelled` state**

In `SubagentTask.cs`, add the enum member after `Failed`:

```csharp
    /// <summary>Subagent failed (LLM error, max rounds, crash).</summary>
    Failed,

    /// <summary>Subagent was stopped via sub_agent_stop (running loop cancelled or queued task dropped).</summary>
    Cancelled,
```

In the same file, add to `ToStorageValue` (before the `_ =>` default) and `Parse`:

```csharp
        SubagentTaskState.Failed => "failed",
        SubagentTaskState.Cancelled => "cancelled",
```

```csharp
        "failed" => SubagentTaskState.Failed,
        "cancelled" => SubagentTaskState.Cancelled,
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "Name~ToStorageValue_And_Parse_RoundTrip"`
Expected: PASS (6 cases).

- [ ] **Step 5: Write the failing store-retention test**

Append to `tests/Cortex.Contained.Agent.Host.Tests/SubagentSessionStoreTests.cs` (follow the existing constructor/temp-dir pattern in that file for creating a store; reuse its helper if present):

```csharp
    [Fact]
    public void UpdateState_Cancelled_SetsCompletedAt_AndExcludesFromQueue()
    {
        var task = new SubagentTask
        {
            TaskId = "sa-cancel-1",
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "test",
            Prompt = "do it",
            State = SubagentTaskState.Queued,
        };
        this._store.Create(task);

        this._store.UpdateState("sa-cancel-1", SubagentTaskState.Cancelled, result: "[Subagent stopped]");

        var reloaded = this._store.GetById("sa-cancel-1");
        Assert.NotNull(reloaded);
        Assert.Equal(SubagentTaskState.Cancelled, reloaded!.State);
        Assert.NotNull(reloaded.CompletedAt);            // terminal → completed_at stamped
        Assert.Null(this._store.GetOldestQueued());      // cancelled is not queued
    }
```

Note: match the field name the existing tests use for the store instance (e.g. `_store`). If the class builds a fresh store per test via a temp path, mirror that exactly.

- [ ] **Step 6: Run it to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "Name~UpdateState_Cancelled_SetsCompletedAt_AndExcludesFromQueue"`
Expected: FAIL — `CompletedAt` is null (the `UpdateState` CASE only stamps for `completed`/`failed`).

- [ ] **Step 7: Extend the store's completed_at CASE and cleanup**

In `SubagentSessionStore.cs` `UpdateState`, change the CASE (line ~188):

```csharp
                    completed_at = CASE WHEN $state IN ('completed', 'failed', 'cancelled') THEN $now ELSE completed_at END
```

In `Cleanup`, add `'cancelled'` to both retention queries so stopped tasks are purged like completed/failed:

```csharp
                WHERE state IN ('completed', 'failed', 'cancelled')
```

(cmd1 messages-clear at ~314 and cmd2 delete at ~325 — both `state IN (...)` clauses.)

- [ ] **Step 8: Run it to verify it passes**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "Name~UpdateState_Cancelled"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Cortex.Contained.Agent.Host/Agent/SubagentTask.cs \
        src/Cortex.Contained.Agent.Host/Agent/SubagentSessionStore.cs \
        tests/Cortex.Contained.Agent.Host.Tests/SubagentTaskStateTests.cs \
        tests/Cortex.Contained.Agent.Host.Tests/SubagentSessionStoreTests.cs
git commit -m "feat(subagent): add Cancelled terminal state + store retention

Claude-Session: https://claude.ai/code/session_01T8WPqhE2k4hmizysyq4uMg"
```

---

## Task 2: Live-mutable concurrency cap on the registry

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Agent/SubagentRunnerRegistry.cs`
- Modify: `tests/Cortex.Contained.Agent.Host.Tests/SubagentRunnerRegistryTests.cs`

**Interfaces:**
- Produces: `int MaxConcurrent { get; }`, `void SetMaxConcurrent(int value)` (clamps `[1,20]`, invokes the slots-opened callback only when the cap is raised), `void SetSlotsOpenedCallback(Action callback)`. Consumed by Tasks 4 and 5.
- Consumes: nothing new. Constructor signature `(int maxConcurrent, ILogger<SubagentRunnerRegistry>)` is unchanged (still seeds the initial value; existing tests keep passing).

- [ ] **Step 1: Write the failing tests**

Append to `SubagentRunnerRegistryTests.cs`:

```csharp
    // ── Live cap ─────────────────────────────────────────────────────────

    [Fact]
    public void SetMaxConcurrent_Raise_OpensSlotsAndInvokesCallback()
    {
        // fill the cap of 2
        _registry.TryRegister("task-1", CreateRunner());
        _registry.TryRegister("task-2", CreateRunner());
        Assert.False(_registry.HasAvailableSlot);

        var callbackCount = 0;
        _registry.SetSlotsOpenedCallback(() => callbackCount++);

        _registry.SetMaxConcurrent(4);

        Assert.Equal(4, _registry.MaxConcurrent);
        Assert.True(_registry.HasAvailableSlot);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public void SetMaxConcurrent_Lower_DoesNotInvokeCallback()
    {
        var callbackCount = 0;
        _registry.SetSlotsOpenedCallback(() => callbackCount++);

        _registry.SetMaxConcurrent(1);

        Assert.Equal(1, _registry.MaxConcurrent);
        Assert.Equal(0, callbackCount);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(25, 20)]
    [InlineData(7, 7)]
    public void SetMaxConcurrent_Clamps_To_1_20(int input, int expected)
    {
        _registry.SetMaxConcurrent(input);
        Assert.Equal(expected, _registry.MaxConcurrent);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "Name~SetMaxConcurrent"`
Expected: FAIL — `SetMaxConcurrent` / `MaxConcurrent` / `SetSlotsOpenedCallback` don't exist (compile error).

- [ ] **Step 3: Implement the live cap**

In `SubagentRunnerRegistry.cs`: change the field, add bounds constants, the getter, setter, and callback. Replace `private readonly int maxConcurrent;` with:

```csharp
    private const int MinConcurrent = 1;
    private const int MaxConcurrentLimit = 20;

    private volatile int maxConcurrent;
    private Action? slotsOpenedCallback;
```

Constructor: keep the guard, assign the seed (unchanged behavior):

```csharp
    public SubagentRunnerRegistry(int maxConcurrent, ILogger<SubagentRunnerRegistry> logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);
        this.maxConcurrent = maxConcurrent;
        this.logger = logger;
    }
```

Add members (near `ActiveCount`):

```csharp
    /// <summary>The current live concurrency cap.</summary>
    public int MaxConcurrent => this.maxConcurrent;

    /// <summary>
    /// Register a callback invoked when concurrency slots open (cap raised). The
    /// consumer (SubAgentStartTool) uses it to start queued subagents immediately.
    /// </summary>
    public void SetSlotsOpenedCallback(Action callback) => this.slotsOpenedCallback = callback;

    /// <summary>
    /// Set the live concurrency cap (clamped to [1,20]). Raising it invokes the
    /// slots-opened callback so waiting subagents start without a restart. Lowering
    /// it only caps NEW registrations — running subagents are never force-stopped.
    /// </summary>
    public void SetMaxConcurrent(int value)
    {
        var clamped = Math.Clamp(value, MinConcurrent, MaxConcurrentLimit);
        var previous = this.maxConcurrent;
        this.maxConcurrent = clamped;
        this.LogMaxConcurrentChanged(previous, clamped);

        if (clamped > previous)
        {
            this.slotsOpenedCallback?.Invoke();
        }
    }
```

Add the logger message with the others:

```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-registry] Max concurrent changed: {Previous} -> {Current}")]
    private partial void LogMaxConcurrentChanged(int previous, int current);
```

- [ ] **Step 4: Run to verify pass (and no regressions)**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=Cortex.Contained.Agent.Host.Tests.SubagentRunnerRegistryTests"`
Expected: PASS (new + all existing registry tests).

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Agent.Host/Agent/SubagentRunnerRegistry.cs \
        tests/Cortex.Contained.Agent.Host.Tests/SubagentRunnerRegistryTests.cs
git commit -m "feat(subagent): live-mutable concurrency cap + slots-opened callback

Claude-Session: https://claude.ai/code/session_01T8WPqhE2k4hmizysyq4uMg"
```

---

## Task 3: Per-task cancellation in the registry

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Agent/SubagentRunnerRegistry.cs`
- Modify: `tests/Cortex.Contained.Agent.Host.Tests/SubagentRunnerRegistryTests.cs`

**Interfaces:**
- Produces: `CancellationToken GetCancellationToken(string taskId)` (returns the task's token, or `CancellationToken.None` if unknown), `bool TryCancel(string taskId)` (cancels the task's CTS; returns whether an entry existed). `TryRegister`/`TryGet`/`Remove`/`ActiveCount` keep their existing signatures. Consumed by Tasks 4 and 5.

- [ ] **Step 1: Write the failing tests**

Append to `SubagentRunnerRegistryTests.cs`:

```csharp
    // ── Per-task cancellation ────────────────────────────────────────────

    [Fact]
    public void GetCancellationToken_Registered_ReturnsLiveToken()
    {
        _registry.TryRegister("task-1", CreateRunner());
        var token = _registry.GetCancellationToken("task-1");
        Assert.False(token.IsCancellationRequested);
        Assert.True(token.CanBeCanceled);
    }

    [Fact]
    public void TryCancel_Running_CancelsToken()
    {
        _registry.TryRegister("task-1", CreateRunner());
        var token = _registry.GetCancellationToken("task-1");

        Assert.True(_registry.TryCancel("task-1"));
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void TryCancel_Unknown_ReturnsFalse()
    {
        Assert.False(_registry.TryCancel("nope"));
    }

    [Fact]
    public void GetCancellationToken_Unknown_ReturnsNone()
    {
        Assert.Equal(CancellationToken.None, _registry.GetCancellationToken("nope"));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "Name~TryCancel|Name~GetCancellationToken"`
Expected: FAIL — methods don't exist (compile error).

- [ ] **Step 3: Store a CTS alongside each runner**

In `SubagentRunnerRegistry.cs`, change the dictionary value to an entry record holding the runner + its CTS. Replace:

```csharp
    private readonly ConcurrentDictionary<string, SubagentRunner> runners = new(StringComparer.Ordinal);
```

with:

```csharp
    private readonly ConcurrentDictionary<string, RunnerEntry> runners = new(StringComparer.Ordinal);

    private sealed record RunnerEntry(SubagentRunner Runner, CancellationTokenSource Cts);
```

Update `TryRegister` to create+store a CTS:

```csharp
    public bool TryRegister(string taskId, SubagentRunner runner)
    {
        if (this.runners.Count >= this.maxConcurrent)
        {
            this.LogSlotUnavailable(taskId, this.runners.Count, this.maxConcurrent);
            return false;
        }

        this.runners[taskId] = new RunnerEntry(runner, new CancellationTokenSource());
        this.LogRunnerRegistered(taskId, this.runners.Count);
        return true;
    }
```

Update `Remove` to dispose the CTS:

```csharp
    public bool Remove(string taskId)
    {
        var removed = this.runners.TryRemove(taskId, out var entry);
        if (removed)
        {
            entry!.Cts.Dispose();
            this.LogRunnerRemoved(taskId, this.runners.Count);
        }

        return removed;
    }
```

Update `TryGet` to return the runner:

```csharp
    public SubagentRunner? TryGet(string taskId)
        => this.runners.TryGetValue(taskId, out var entry) ? entry.Runner : null;
```

Add the two new methods:

```csharp
    /// <summary>The cancellation token for a registered task, or <see cref="CancellationToken.None"/>.</summary>
    public CancellationToken GetCancellationToken(string taskId)
        => this.runners.TryGetValue(taskId, out var entry) ? entry.Cts.Token : CancellationToken.None;

    /// <summary>
    /// Cancel a running task's loop. Returns true if a runner was registered under
    /// <paramref name="taskId"/> (its token is cancelled); false if not found.
    /// </summary>
    public bool TryCancel(string taskId)
    {
        if (!this.runners.TryGetValue(taskId, out var entry))
        {
            return false;
        }

        entry.Cts.Cancel();
        this.LogRunnerCancelled(taskId);
        return true;
    }
```

Add the logger message:

```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-registry] Runner cancelled: {TaskId}")]
    private partial void LogRunnerCancelled(string taskId);
```

- [ ] **Step 4: Run to verify pass (and no regressions)**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=Cortex.Contained.Agent.Host.Tests.SubagentRunnerRegistryTests"`
Expected: PASS (new + existing, including `TryGet_Registered_ReturnsRunner`).

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Agent.Host/Agent/SubagentRunnerRegistry.cs \
        tests/Cortex.Contained.Agent.Host.Tests/SubagentRunnerRegistryTests.cs
git commit -m "feat(subagent): per-task cancellation token in registry (TryCancel)

Claude-Session: https://claude.ai/code/session_01T8WPqhE2k4hmizysyq4uMg"
```

---

## Task 4: `sub_agent_stop` tool + runner cancellation wiring

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Tools/BuiltIn/SubAgentStopTool.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Tools/BuiltIn/SubAgentStartTool.cs` (ctor: register slots-opened callback; `FireRunner`: per-task token + cancellation-aware catch/finally)
- Modify: `src/Cortex.Contained.Agent.Host/Program.cs` (register the tool)
- Create: `tests/Cortex.Contained.Agent.Host.Tests/SubAgentStopToolTests.cs`

**Interfaces:**
- Consumes: `SubagentSessionStore` (`GetById`, `UpdateState`), `SubagentRunnerRegistry` (`TryCancel`, `TryRegister`, `GetCancellationToken`), `SubagentTaskState.Cancelled`.
- Produces: an `IAgentTool` named `sub_agent_stop` with param `task_id`.

- [ ] **Step 1: Write the failing tool tests**

Create `tests/Cortex.Contained.Agent.Host.Tests/SubAgentStopToolTests.cs`:

```csharp
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public sealed class SubAgentStopToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sastop-" + Guid.NewGuid().ToString("N"));
    private readonly SubagentSessionStore _store;
    private readonly SubagentRunnerRegistry _registry;
    private readonly SubAgentStopTool _tool;

    public SubAgentStopToolTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SubagentSessionStore(_dir, NullLogger<SubagentSessionStore>.Instance);
        _registry = new SubagentRunnerRegistry(2, NullLogger<SubagentRunnerRegistry>.Instance);
        _tool = new SubAgentStopTool(_store, _registry, NullLogger<SubAgentStopTool>.Instance);
    }

    private SubagentTask Seed(string id, SubagentTaskState state)
    {
        var t = new SubagentTask
        {
            TaskId = id, ParentConversation = "c", ParentChannel = "webchat-default",
            Description = "d", Prompt = "p", State = state,
        };
        _store.Create(t);
        if (state != SubagentTaskState.Queued)
        {
            _store.UpdateState(id, state);
        }
        return t;
    }

    private static ToolExecutionContext Ctx() => new()
    {
        ConversationId = "c", ChannelId = "webchat-default",
    };

    private static SubagentRunner Runner()
    {
        var client = Substitute.For<ILlmClient>();
        var reg = new ToolRegistry([], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);
        return new SubagentRunner(client, reg, 10, NullLogger<SubagentRunner>.Instance);
    }

    [Fact]
    public async Task Stop_RunningRegistered_CancelsToken_ReturnsOk()
    {
        Seed("sa-1", SubagentTaskState.Running);
        _registry.TryRegister("sa-1", Runner());
        var token = _registry.GetCancellationToken("sa-1");

        var result = await _tool.ExecuteAsync("""{"task_id":"sa-1"}""", Ctx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task Stop_Queued_MarksCancelled()
    {
        Seed("sa-2", SubagentTaskState.Queued);

        var result = await _tool.ExecuteAsync("""{"task_id":"sa-2"}""", Ctx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(SubagentTaskState.Cancelled, _store.GetById("sa-2")!.State);
    }

    [Fact]
    public async Task Stop_Unknown_Fails()
    {
        var result = await _tool.ExecuteAsync("""{"task_id":"nope"}""", Ctx(), CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Stop_AlreadyCompleted_ReportsNoChange()
    {
        Seed("sa-3", SubagentTaskState.Completed);

        var result = await _tool.ExecuteAsync("""{"task_id":"sa-3"}""", Ctx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(SubagentTaskState.Completed, _store.GetById("sa-3")!.State);
    }

    [Fact]
    public async Task Stop_MissingTaskId_Fails()
    {
        var result = await _tool.ExecuteAsync("""{}""", Ctx(), CancellationToken.None);
        Assert.False(result.Success);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}
```

Note: verify `ToolExecutionContext`'s exact property names/shape against `SubAgentReadTool` usage (`context.ConversationId`, `context.ChannelId`) and `AgentToolResult.Success`; adjust the `Ctx()` initializer if the type needs more fields.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=Cortex.Contained.Agent.Host.Tests.SubAgentStopToolTests"`
Expected: FAIL — `SubAgentStopTool` doesn't exist (compile error).

- [ ] **Step 3: Implement the tool**

Create `src/Cortex.Contained.Agent.Host/Tools/BuiltIn/SubAgentStopTool.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Stops a subagent task: cancels a running runner's loop, or drops a still-queued
/// task. Transitions the task to <see cref="SubagentTaskState.Cancelled"/>. Symmetric
/// with sub_agent_start / sub_agent_read / sub_agent_send.
/// </summary>
public sealed partial class SubAgentStopTool : IAgentTool
{
    private readonly SubagentSessionStore store;
    private readonly SubagentRunnerRegistry registry;
    private readonly ILogger<SubAgentStopTool> logger;

    public SubAgentStopTool(
        SubagentSessionStore store,
        SubagentRunnerRegistry registry,
        ILogger<SubAgentStopTool> logger)
    {
        this.store = store;
        this.registry = registry;
        this.logger = logger;
    }

    public string Name => "sub_agent_stop";

    public string Description =>
        "Stop a background subagent task you started. Cancels a running subagent's " +
        "work or drops a queued one. Use sub_agent_read first if you need its current " +
        "state or partial result. Provide the task_id returned by sub_agent_start.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "task_id": {
              "type": "string",
              "description": "The task ID of the subagent to stop"
            }
          },
          "required": ["task_id"]
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(
        string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string taskId;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            taskId = doc.RootElement.GetProperty("task_id").GetString() ?? string.Empty;
        }
#pragma warning disable CA1031 // Bad arguments should not crash the agent
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Task.FromResult(AgentToolResult.Fail($"Invalid arguments: {ex.Message}"));
        }

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult(AgentToolResult.Fail("Missing required parameter: task_id"));
        }

        var task = this.store.GetById(taskId);
        if (task is null)
        {
            this.LogNotFound(taskId);
            return Task.FromResult(AgentToolResult.Fail($"No subagent task found with ID '{taskId}'."));
        }

        switch (task.State)
        {
            case SubagentTaskState.Running or SubagentTaskState.Revising:
                if (this.registry.TryCancel(taskId))
                {
                    // The runner's own catch/finally transitions state to Cancelled,
                    // notifies the main agent, and dequeues the next task.
                    this.LogStopRequested(taskId);
                    return Task.FromResult(AgentToolResult.Ok(
                        $"Stopping subagent {taskId}. It will report as stopped shortly."));
                }

                // Store says running but no live runner (e.g. mid-transition) — mark stopped defensively.
                this.store.UpdateState(taskId, SubagentTaskState.Cancelled, result: "[Subagent stopped]");
                this.LogStopRequested(taskId);
                return Task.FromResult(AgentToolResult.Ok($"Subagent {taskId} marked stopped."));

            case SubagentTaskState.Queued:
                this.store.UpdateState(taskId, SubagentTaskState.Cancelled, result: "[Subagent stopped before starting]");
                this.LogQueuedCancelled(taskId);
                return Task.FromResult(AgentToolResult.Ok($"Queued subagent {taskId} cancelled."));

            default:
                var state = task.State.ToStorageValue();
                return Task.FromResult(AgentToolResult.Ok(
                    $"Subagent {taskId} is already {state}; nothing to stop."));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_stop] Stop requested: {TaskId}")]
    private partial void LogStopRequested(string taskId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_stop] Queued task cancelled: {TaskId}")]
    private partial void LogQueuedCancelled(string taskId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[sub_agent_stop] Task not found: {TaskId}")]
    private partial void LogNotFound(string taskId);
}
```

- [ ] **Step 4: Run to verify the tool tests pass**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=Cortex.Contained.Agent.Host.Tests.SubAgentStopToolTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Wire runner cancellation + slots-opened callback in `SubAgentStartTool`**

In `SubAgentStartTool.cs` constructor, after `this.registry = registry;`, register the dequeue callback so a raised cap starts queued tasks:

```csharp
        this.registry = registry;
        this.registry.SetSlotsOpenedCallback(this.StartQueuedTasks);
```

In `FireRunner`, after a successful `TryRegister`, get the per-task token and use it for context retrieval and the run; replace the `Task.Run` body's `catch`/`finally` to handle cancellation. The method becomes:

```csharp
        if (!this.registry.TryRegister(taskId, runner))
        {
            this.store.UpdateState(taskId, SubagentTaskState.Queued);
            this.LogSubAgentQueued(taskId, description);
            return;
        }

        var token = this.registry.GetCancellationToken(taskId);

        _ = Task.Run(async () =>
        {
            try
            {
                var memories = await RetrieveSubagentContextAsync(prompt, token).ConfigureAwait(false);

                var bootstrapContext = LoadBootstrapContext();
                var systemPrompt = BuildSubagentSystemPrompt(memories, bootstrapContext, skillName);

                await runner.RunAsync(
                    this.modelProvider.DefaultModel, systemPrompt, prompt,
                    $"subagent-{taskId}", token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                this.LogSubAgentCancelled(taskId);
                this.store.UpdateState(taskId, SubagentTaskState.Cancelled, result: "[Subagent stopped]");
            }
#pragma warning disable CA1031 // Background task must not crash the process
            catch (Exception ex)
#pragma warning restore CA1031
            {
                this.LogSubAgentCrashed(taskId, ex.Message);
                this.store.UpdateState(taskId, SubagentTaskState.Failed, result: $"[Subagent crashed: {ex.Message}]");
            }
            finally
            {
                // Remove frees the slot (count decreases). Always safe to call.
                this.registry.Remove(taskId);

                // On stop/crash: notify main agent and dequeue next task.
                var task = this.store.GetById(taskId);
                if (task?.State is SubagentTaskState.Failed or SubagentTaskState.Cancelled)
                {
                    try
                    {
                        await this.onCompletion(taskId, task.Result ?? "[Subagent stopped]").ConfigureAwait(false);
                    }
#pragma warning disable CA1031
                    catch { /* must not throw */ }
#pragma warning restore CA1031

                    DequeueNext();
                }
            }
        }, CancellationToken.None); // Task.Run lifetime is independent; cancellation is via the per-task token.
```

Add the logger message alongside the others in `SubAgentStartTool`:

```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_start] Subagent stopped: {TaskId}")]
    private partial void LogSubAgentCancelled(string taskId);
```

- [ ] **Step 6: Register the tool in `Program.cs`**

After the `SubAgentSendTool` registration block, add:

```csharp
builder.Services.AddSingleton<IAgentTool>(sp =>
    new SubAgentStopTool(
        sp.GetRequiredService<SubagentSessionStore>(),
        sp.GetRequiredService<SubagentRunnerRegistry>(),
        sp.GetRequiredService<ILogger<SubAgentStopTool>>()));
```

- [ ] **Step 7: Build + run the affected tests**

Run: `dotnet build src/Cortex.Contained.Agent.Host/Cortex.Contained.Agent.Host.csproj`
Expected: succeeds (0 warnings/errors).
Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=Cortex.Contained.Agent.Host.Tests.SubAgentStopToolTests|ClassName=Cortex.Contained.Agent.Host.Tests.SubagentRunnerRegistryTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Cortex.Contained.Agent.Host/Tools/BuiltIn/SubAgentStopTool.cs \
        src/Cortex.Contained.Agent.Host/Tools/BuiltIn/SubAgentStartTool.cs \
        src/Cortex.Contained.Agent.Host/Program.cs \
        tests/Cortex.Contained.Agent.Host.Tests/SubAgentStopToolTests.cs
git commit -m "feat(subagent): sub_agent_stop tool + runner cancellation wiring

Claude-Session: https://claude.ai/code/session_01T8WPqhE2k4hmizysyq4uMg"
```

---

## Task 5: Live-apply the cap — hub DTO + AgentRuntime + BridgeConfig

**Files:**
- Modify: `src/Cortex.Contained.Contracts/Hub/HubTypes.cs` (`AgentConfigUpdate`)
- Modify: `src/Cortex.Contained.Contracts/Config/BridgeConfig.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Agent/AgentRuntime.cs` (ctor field + `UpdateConfigAsync`:1476)
- Modify: `src/Cortex.Contained.Agent.Host/Program.cs` (`new AgentRuntime(...)`:594-621 — pass the registry)
- Modify: `tests/Cortex.Contained.Agent.Host.Tests/SubagentRunnerRegistryTests.cs` (DTO-applies test lives at registry seam; see step 1)

**Interfaces:**
- Consumes: `SubagentRunnerRegistry.SetMaxConcurrent` (Task 2).
- Produces: `AgentConfigUpdate.MaxConcurrentSubagents` (`int?`), `BridgeConfig.MaxConcurrentSubagents` (`int`, default 5) — consumed by Task 6.

- [ ] **Step 1: Write the failing apply test**

The applied effect is observable on the registry. Append to `SubagentRunnerRegistryTests.cs` a test that mirrors what `UpdateConfigAsync` does (apply the DTO value to the registry):

```csharp
    [Fact]
    public void ApplyConfigValue_SetsLiveCap()
    {
        var update = new Cortex.Contained.Contracts.Hub.AgentConfigUpdate { MaxConcurrentSubagents = 9 };

        if (update.MaxConcurrentSubagents.HasValue)
        {
            _registry.SetMaxConcurrent(update.MaxConcurrentSubagents.Value);
        }

        Assert.Equal(9, _registry.MaxConcurrent);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "Name~ApplyConfigValue_SetsLiveCap"`
Expected: FAIL — `AgentConfigUpdate` has no `MaxConcurrentSubagents` (compile error).

- [ ] **Step 3: Add the DTO + config fields**

In `HubTypes.cs`, add to `AgentConfigUpdate` (after `MaxSubagentRounds`):

```csharp
    /// <summary>
    /// Maximum number of subagents that may run concurrently (1–20). Applied live
    /// to the SubagentRunnerRegistry without a container restart.
    /// </summary>
    public int? MaxConcurrentSubagents { get; init; }
```

In `BridgeConfig.cs`, add (after `MaxSubagentRounds`):

```csharp
    /// <summary>Maximum number of subagents that may run concurrently (1–20). Default 5.</summary>
    public int MaxConcurrentSubagents { get; set; } = 5;
```

- [ ] **Step 4: Run to verify the test passes**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "Name~ApplyConfigValue_SetsLiveCap"`
Expected: PASS.

- [ ] **Step 5: Apply the value in `AgentRuntime.UpdateConfigAsync` and inject the registry**

In `AgentRuntime.cs`, add a field near the other optional dependencies:

```csharp
    private readonly SubagentRunnerRegistry? subagentRegistry;
```

Add a constructor parameter at the end of the optional-parameter list (after `memorySettingsStore`) and assign it:

```csharp
        Memory.MemorySettingsStore? memorySettingsStore = null,
        SubagentRunnerRegistry? subagentRegistry = null)
```

```csharp
        this.subagentRegistry = subagentRegistry;
```

In `UpdateConfigAsync` (line 1476), apply the cap before the log line:

```csharp
        if (config.MaxConcurrentSubagents.HasValue)
        {
            this.subagentRegistry?.SetMaxConcurrent(config.MaxConcurrentSubagents.Value);
        }
```

In `Program.cs`, pass the registry into the `new AgentRuntime(...)` call (append after `memorySettingsStore: ...`):

```csharp
        memorySettingsStore: sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.MemorySettingsStore>(),
        subagentRegistry: sp.GetRequiredService<SubagentRunnerRegistry>()));
```

- [ ] **Step 6: Build the agent project + affected tests**

Run: `dotnet build src/Cortex.Contained.Agent.Host/Cortex.Contained.Agent.Host.csproj`
Expected: succeeds (0 warnings).
Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=Cortex.Contained.Agent.Host.Tests.SubagentRunnerRegistryTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Cortex.Contained.Contracts/Hub/HubTypes.cs \
        src/Cortex.Contained.Contracts/Config/BridgeConfig.cs \
        src/Cortex.Contained.Agent.Host/Agent/AgentRuntime.cs \
        src/Cortex.Contained.Agent.Host/Program.cs \
        tests/Cortex.Contained.Agent.Host.Tests/SubagentRunnerRegistryTests.cs
git commit -m "feat(subagent): apply max-concurrent live via AgentConfigUpdate + BridgeConfig

Claude-Session: https://claude.ai/code/session_01T8WPqhE2k4hmizysyq4uMg"
```

---

## Task 6: Bridge settings — request DTO, endpoint, YAML persistence

**Files:**
- Modify: `src/Cortex.Contained.Bridge/SetupHelpers.cs` (`SettingsUpdateRequest`:~1202)
- Modify: `src/Cortex.Contained.Bridge/Endpoints/SettingsEndpoints.cs` (GET:58, POST:~198)
- Modify: `src/Cortex.Contained.Bridge/Setup/BridgeSettingsWriter.cs` (~172)
- Create: `tests/Cortex.Contained.Bridge.Tests/SubagentSettingsYamlTests.cs`

**Interfaces:**
- Consumes: `BridgeConfig.MaxConcurrentSubagents` (Task 5), `AgentConfigUpdate.MaxConcurrentSubagents` (Task 5), `HubClient.UpdateConfigAsync` (existing).
- Produces: JSON key `maxConcurrentSubagents` on GET/POST `/api/settings`; YAML key `maxConcurrentSubagents:` in `cortex.yml` — consumed by Task 7 (UI) and the agent's config binding.

- [ ] **Step 1: Write the failing YAML test**

Create `tests/Cortex.Contained.Bridge.Tests/SubagentSettingsYamlTests.cs`:

```csharp
using Cortex.Contained.Bridge.Setup;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests;

public sealed class SubagentSettingsYamlTests
{
    [Fact]
    public void PersistSettingsToYaml_WritesMaxConcurrentSubagents()
    {
        var config = new BridgeConfig { MaxConcurrentSubagents = 7 };
        var path = Path.Combine(Path.GetTempPath(), "cortex-" + Guid.NewGuid().ToString("N") + ".yml");

        try
        {
            BridgeSettingsWriter.PersistSettingsToYaml(config, path);
            var yaml = File.ReadAllText(path);
            Assert.Contains("maxConcurrentSubagents: 7", yaml, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }
}
```

Note: confirm `PersistSettingsToYaml` is public with signature `(BridgeConfig config, string path)` (it is called that way in `SettingsEndpoints.cs:207`). If a required non-default field on `BridgeConfig` throws during write, add the minimum needed to the initializer.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "Name~PersistSettingsToYaml_WritesMaxConcurrentSubagents"`
Expected: FAIL — the YAML has no `maxConcurrentSubagents` line.

- [ ] **Step 3: Write the YAML line**

In `BridgeSettingsWriter.cs`, right after the `MaxSubagentRounds` block (~176), add:

```csharp
        // Max concurrent subagents (always emitted — it has a real default of 5).
        sb.AppendLine(CultureInfo.InvariantCulture, $"maxConcurrentSubagents: {config.MaxConcurrentSubagents}");
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "Name~PersistSettingsToYaml_WritesMaxConcurrentSubagents"`
Expected: PASS.

- [ ] **Step 5: Add the request DTO field**

In `SetupHelpers.cs`, add to `SettingsUpdateRequest` (after `MaxSubagentRounds`:1202):

```csharp
    /// <summary>Maximum number of subagents that may run concurrently (1–20). Null = no change.</summary>
    [JsonPropertyName("maxConcurrentSubagents")]
    public int? MaxConcurrentSubagents { get; set; }
```

- [ ] **Step 6: Expose it on GET and apply+push on POST**

In `SettingsEndpoints.cs` GET response object (after `maxSubagentRounds = config.MaxSubagentRounds,`:58):

```csharp
                maxSubagentRounds = config.MaxSubagentRounds,
                maxConcurrentSubagents = config.MaxConcurrentSubagents,
```

In the POST handler, right after the `MaxSubagentRounds` block (~202), add persist + live-push:

```csharp
            // Update max concurrent subagents — persist (restart durability) AND push live.
            if (request.MaxConcurrentSubagents.HasValue)
            {
                var clamped = Math.Clamp(request.MaxConcurrentSubagents.Value, 1, 20);
                config.MaxConcurrentSubagents = clamped;
                changed = true;

                var agent = tenantRouter.GetDefaultClient();
                if (agent is not null && agent.IsConnected)
                {
                    await agent.UpdateConfigAsync(
                        new AgentConfigUpdate { MaxConcurrentSubagents = clamped },
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
```

Ensure `using Cortex.Contained.Contracts.Hub;` is present at the top of `SettingsEndpoints.cs` (for `AgentConfigUpdate`); add it if missing.

- [ ] **Step 7: Build the Bridge + run the test**

Run: `dotnet build src/Cortex.Contained.Bridge/Cortex.Contained.Bridge.csproj`
Expected: succeeds (0 warnings).
Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "Name~PersistSettingsToYaml_WritesMaxConcurrentSubagents"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Cortex.Contained.Bridge/SetupHelpers.cs \
        src/Cortex.Contained.Bridge/Endpoints/SettingsEndpoints.cs \
        src/Cortex.Contained.Bridge/Setup/BridgeSettingsWriter.cs \
        tests/Cortex.Contained.Bridge.Tests/SubagentSettingsYamlTests.cs
git commit -m "feat(bridge): max-concurrent-subagents setting (persist + live push)

Claude-Session: https://claude.ai/code/session_01T8WPqhE2k4hmizysyq4uMg"
```

---

## Task 7: Web UI field (global settings page)

**Files:**
- Modify: `src/Cortex.Contained.Bridge/wwwroot/js/pages/global-settings.js`
- Modify: `src/Cortex.Contained.Bridge/wwwroot/app.html` (~1266, near the `maxSubagentRounds` input)

**Interfaces:**
- Consumes: GET/POST `/api/settings` key `maxConcurrentSubagents` (Task 6).

No automated test (client-side HTML/JS); verified by build + manual smoke in Task 8's checklist. Mirror the existing `maxSubagentRounds` wiring exactly.

- [ ] **Step 1: Add reactive state + load + save in `global-settings.js`**

Mirror every `maxSubagentRounds` site for `maxConcurrentSubagents`:

- In the data object (near line 82): `maxConcurrentSubagents: 5,`
- In the loader (near line 173): `this.maxConcurrentSubagents = data.maxConcurrentSubagents ?? 5;`
- In the "original snapshot" used for dirty-checking (near line 190): `maxConcurrentSubagents: this.maxConcurrentSubagents,`
- In the dirty comparison (near line 230): add `|| this.maxConcurrentSubagents !== o.maxConcurrentSubagents`
- In the payload/diff build (near lines 365–375): mirror the rounds pattern:

```javascript
            if (this.maxConcurrentSubagents !== o.maxConcurrentSubagents) {
                payload.maxConcurrentSubagents = this.maxConcurrentSubagents;
            }
```

  and include `maxConcurrentSubagents: this.maxConcurrentSubagents,` in the post-save snapshot (near line 375).
- If there is a numeric setter/parse handler for rounds (near line 245), add the analog:

```javascript
        setMaxConcurrentSubagents(value) {
            this.maxConcurrentSubagents = Math.min(20, Math.max(1, parseInt(value, 10) || 5));
        },
```

- [ ] **Step 2: Add the input to `app.html`**

Next to the `maxSubagentRounds` input (~1266), add a labeled field (mirror the surrounding markup/classes):

```html
<label>Max concurrent subagents</label>
<input type="number" min="1" max="20" :value="maxConcurrentSubagents"
       @input="setMaxConcurrentSubagents($event.target.value)" />
```

Match the actual event binding the rounds input uses (`@input`/`@change` and setter vs. direct `x-model.number`); use `x-model.number="maxConcurrentSubagents"` instead if that is the existing idiom, and drop the setter.

- [ ] **Step 3: Build the Bridge (packs wwwroot) and eyeball**

Run: `dotnet build src/Cortex.Contained.Bridge/Cortex.Contained.Bridge.csproj`
Expected: succeeds. (Manual UI verification happens in Task 8.)

- [ ] **Step 4: Commit**

```bash
git add src/Cortex.Contained.Bridge/wwwroot/js/pages/global-settings.js \
        src/Cortex.Contained.Bridge/wwwroot/app.html
git commit -m "feat(bridge-ui): max concurrent subagents field on settings page

Claude-Session: https://claude.ai/code/session_01T8WPqhE2k4hmizysyq4uMg"
```

---

## Task 8: Full-solution gate + docs + finalize

**Files:**
- Modify: `docs/self-update.md`? No. Modify: `CLAUDE.md`? No. (No docs required beyond the spec.)
- Verify only.

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build cortex-contained.sln`
Expected: succeeds, 0 warnings (TreatWarningsAsErrors).

- [ ] **Step 2: Run the full agent + bridge test suites**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests`
Run: `dotnet test tests/Cortex.Contained.Bridge.Tests`
Expected: all green.

- [ ] **Step 3: Manual smoke (documented, not automated)**

Note for the reviewer/operator (do NOT block the plan on this — it needs a running install):
- Open the Bridge settings page → confirm "Max concurrent subagents" shows the current value, edit it, save.
- `GET /api/settings` returns the new `maxConcurrentSubagents`; `cortex.yml` contains `maxConcurrentSubagents: N`.
- With the agent connected, raising the value should let a queued subagent start without a restart (agent logs `[subagent-registry] Max concurrent changed`).
- From an agent conversation, start a long subagent, then `sub_agent_stop(task_id)` → it reports stopped and the main agent receives the stopped completion.

- [ ] **Step 4: Final no-op commit if anything was left staged**

```bash
git status
# If the working tree is clean, nothing to do — the feature is committed across Tasks 1–7.
```

---

## Notes for the executor

- **Do not touch coda / `lib/`** — this feature is entirely cortex-side.
- **Nullable optional-dependency convention:** `AgentRuntime` takes memory/registry deps as nullable with `= null` defaults so existing test constructors keep compiling. Follow it.
- **The runner's stopped-notification path** (`FireRunner` catch → `onCompletion`) is fire-and-forget and covered by review, not a brittle async unit test. The registry-level `TryCancel` + token-cancellation tests (Task 3) and the tool tests (Task 4) are the automated safety net.
- **Clamp lives in two places on purpose:** the Bridge endpoint (reject bad input early) and `SubagentRunnerRegistry.SetMaxConcurrent` (defensive, since the hub DTO is also reachable programmatically).
