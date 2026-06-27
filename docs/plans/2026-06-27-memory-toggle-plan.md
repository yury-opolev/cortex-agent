# Built-in Memory Enable/Disable Toggle — Implementation Plan (Slice 2 of 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a single built-in-memory enable switch (default `true`) that, when off, hides the 5 memory tools, skips fact-extraction + compaction, and stops the `embeddings` Docker sidecar live — defaulting to current behavior for existing configs.

**Architecture:** Same uniform pattern as Slice 1. A new `Enabled` flag on `MemorySettingsConfig` (Bridge config) and on the `MemoryConfig` SignalR DTO. The flag is pushed to the Agent Host live and held in the runtime-mutable `MemorySettingsStore`. Three agent-side in-process gates read the store: (1) a new `MemoryDisabledToolGate : IConversationToolGate` hides the memory tools, (2) `CompactionOrchestrator.FlushExtractionBuffer` drains-and-discards instead of enqueueing, (3) `MemoryCompactionService` skips its sweep. Bridge-side, a new `EmbeddingsSidecarLifecycle` (mirror of `SttSidecarLifecycle`) starts/stops the `embeddings` sidecar, which gains `profiles: [memory]`. A `POST /api/memory/toggle` persists the flag, pushes it to the agent, and reconciles the sidecar; the web UI Memory tab renders the switch.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, SignalR, xUnit + NSubstitute, vanilla JS + Alpine (`wwwroot/js/pages/global-settings.js`), `docker compose` CLI shell-out.

**Parent spec:** `docs/specs/2026-06-27-enable-disable-toggles-voice-memory-design.md`. **Predecessor:** Slice 1 (`docs/plans/2026-06-27-speech-toggles-plan.md`, already shipped). Slice 3 (voice-id) is a separate plan.

## Global Constraints

- .NET 10; `TreatWarningsAsErrors` is on — code must be warning-clean.
- C# style: `this.`-prefixed instance access, braces on all control blocks, one type per file, file-scoped namespaces, `ConfigureAwait(false)` in Bridge service/lifecycle code and Agent library code.
- The new `Enabled` flag defaults to `true` everywhere (config, DTO, store-effective) — existing `cortex.yml` files and all current tests must behave identically.
- Test method naming: `Method_Condition_Expected` (CA1707 suppressed in test projects).
- Bridge persistence is via `BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath)`.
- Sidecar reconcile dispatch is fire-and-forget (`_ = Task.Run(() => lifecycle.ReconcileAsync(...))`) so a slow `docker compose` call never blocks an HTTP save or boot.
- "Memory off" is functional only: existing memories are left on disk (SQLite + sqlite-vec), restored on re-enable.

---

### Task 1: Config flag + SignalR DTO field

**Files:**
- Modify: `src/Cortex.Contained.Contracts/Config/BridgeConfig.cs` (`MemorySettingsConfig` class, ~line 45)
- Modify: `src/Cortex.Contained.Contracts/Hosting/HubTypes.cs` (`MemoryConfig` record/class, ~line 461)
- Test: `tests/Cortex.Contained.Bridge.Tests/Memory/MemoryToggleConfigTests.cs`

**Interfaces:**
- Produces: `MemorySettingsConfig.Enabled` (`bool`, default `true`); `MemoryConfig.Enabled` (`bool`, default `true`).

- [ ] **Step 1: Write the failing test**

`tests/Cortex.Contained.Bridge.Tests/Memory/MemoryToggleConfigTests.cs`:
```csharp
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hosting;

namespace Cortex.Contained.Bridge.Tests.Memory;

public sealed class MemoryToggleConfigTests
{
    [Fact]
    public void MemorySettingsConfig_Enabled_DefaultsTrue()
    {
        Assert.True(new MemorySettingsConfig().Enabled);
    }

    [Fact]
    public void MemoryConfigDto_Enabled_DefaultsTrue()
    {
        Assert.True(new MemoryConfig().Enabled);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "ClassName=Cortex.Contained.Bridge.Tests.Memory.MemoryToggleConfigTests"`
Expected: FAIL to compile — `Enabled` does not exist on either type.

- [ ] **Step 3: Add the flags**

In `BridgeConfig.cs`, add as the first member of `MemorySettingsConfig`:
```csharp
/// <summary>Master built-in-memory switch. When false, memory tools are hidden,
/// fact-extraction + compaction are skipped, and the embeddings sidecar is stopped.</summary>
public bool Enabled { get; set; } = true;
```
In `HubTypes.cs`, add to `MemoryConfig` (first member, matching the existing init/auto-property style of that type):
```csharp
/// <summary>Whether the built-in memory subsystem is enabled. Default true.</summary>
public bool Enabled { get; set; } = true;
```
> Note: if `MemoryConfig` is a positional `record`, instead add `bool Enabled = true` as the first positional parameter AND update every constructor call (grep `new MemoryConfig`); if it is a class with property initializers (as in `BuildMemoryConfig`), the property form above is correct. Check the declaration before editing.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "ClassName=Cortex.Contained.Bridge.Tests.Memory.MemoryToggleConfigTests"`
Expected: PASS (2 cases).

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Contracts/Config/BridgeConfig.cs src/Cortex.Contained.Contracts/Hosting/HubTypes.cs tests/Cortex.Contained.Bridge.Tests/Memory/MemoryToggleConfigTests.cs
git commit -m "feat(memory): add built-in-memory Enabled flag to config + SignalR DTO"
```

---

### Task 2: Runtime store holds `MemoryEnabled`; hub handler wires it

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Memory/MemorySettingsStore.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs` (`UpdateMemoryConfig`, ~line 531)
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Memory/MemorySettingsStoreTests.cs` (create if absent)

**Interfaces:**
- Produces: `MemorySettingsStore.MemoryEnabled` (`bool?`, getter, locked); `MemorySettingsStore.IsMemoryEnabled` (`bool`, computed `=> MemoryEnabled ?? true`); new trailing `bool? memoryEnabled = null` parameter on `MemorySettingsStore.Update(...)`.
- Consumes: `MemoryConfig.Enabled` (Task 1).

- [ ] **Step 1: Write the failing test**

`tests/Cortex.Contained.Agent.Host.Tests/Memory/MemorySettingsStoreTests.cs`:
```csharp
using Cortex.Contained.Agent.Host.Memory;

namespace Cortex.Contained.Agent.Host.Tests.Memory;

public sealed class MemorySettingsStoreTests
{
    [Fact]
    public void IsMemoryEnabled_DefaultsTrue_WhenNeverSet()
    {
        using var store = new MemorySettingsStore();
        Assert.True(store.IsMemoryEnabled);
    }

    [Fact]
    public void Update_SetsMemoryEnabledFalse_IsMemoryEnabledFalse()
    {
        using var store = new MemorySettingsStore();
        store.Update(null, null, null, memoryEnabled: false);
        Assert.False(store.IsMemoryEnabled);
        Assert.False(store.MemoryEnabled);
    }

    [Fact]
    public void Update_MemoryEnabledNull_IsMemoryEnabledTrue()
    {
        using var store = new MemorySettingsStore();
        store.Update(null, null, null, memoryEnabled: null);
        Assert.True(store.IsMemoryEnabled);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=Cortex.Contained.Agent.Host.Tests.Memory.MemorySettingsStoreTests"`. Expected: FAIL to compile (`MemoryEnabled`, `IsMemoryEnabled`, and the param don't exist).

- [ ] **Step 3: Add the field, properties, and Update param**

In `MemorySettingsStore.cs`, add a backing field next to the others (after `ollamaApiKey`):
```csharp
    private bool? memoryEnabled;
```
Add the properties (after the `OllamaApiKey` property, before `Update`):
```csharp
    /// <summary>Master built-in-memory switch. Null = never pushed (treated as enabled).</summary>
    public bool? MemoryEnabled
    {
        get { lock (this.syncLock) return this.memoryEnabled; }
    }

    /// <summary>Effective enablement: true unless explicitly pushed false.</summary>
    public bool IsMemoryEnabled
    {
        get { lock (this.syncLock) return this.memoryEnabled ?? true; }
    }
```
Add a trailing parameter to `Update(...)` (after `string? ollamaApiKey = null`):
```csharp
        string? ollamaApiKey = null,
        bool? memoryEnabled = null)
```
and assign inside the lock (after `this.ollamaApiKey = ollamaApiKey;`):
```csharp
            this.memoryEnabled = memoryEnabled;
```

- [ ] **Step 4: Wire the hub handler**

In `AgentHub.cs` `UpdateMemoryConfig`, pass the new flag through to `store.Update(...)`. Add `config.Enabled` as the new trailing argument on the existing `Update(...)` call (it is the last positional/named arg — match the call's existing argument style):
```csharp
            memoryEnabled: config.Enabled);
```
(If the existing call uses positional args, append `, config.Enabled` as the final argument.)

- [ ] **Step 5: Run to verify pass** — same filter as Step 2. Expected: PASS (3 cases).

- [ ] **Step 6: Commit**

```bash
git add src/Cortex.Contained.Agent.Host/Memory/MemorySettingsStore.cs src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs tests/Cortex.Contained.Agent.Host.Tests/Memory/MemorySettingsStoreTests.cs
git commit -m "feat(memory): MemorySettingsStore.MemoryEnabled + hub handler wiring"
```

---

### Task 3: Tool gate hides the 5 memory tools when disabled

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Tools/MemoryDisabledToolGate.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Program.cs` (register the gate as `IConversationToolGate`, near where `VoiceOnlyToolGate` is registered — grep `VoiceOnlyToolGate`)
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Tools/MemoryDisabledToolGateTests.cs`

**Interfaces:**
- Consumes: `MemorySettingsStore.IsMemoryEnabled` (Task 2); `IConversationToolGate` (existing).
- Produces: `MemoryDisabledToolGate` (registered as `IConversationToolGate`).

The 5 memory tool names (verified in `Program.cs` registration): `memory_ingest`, `memory_get`, `memory_update`, `memory_delete`, `memory_search`.

- [ ] **Step 1: Write the failing test**

`tests/Cortex.Contained.Agent.Host.Tests/Tools/MemoryDisabledToolGateTests.cs`:
```csharp
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Tools;

namespace Cortex.Contained.Agent.Host.Tests.Tools;

public sealed class MemoryDisabledToolGateTests
{
    [Fact]
    public void Enabled_HidesNothing()
    {
        using var store = new MemorySettingsStore(); // default enabled
        var gate = new MemoryDisabledToolGate(store);
        Assert.Empty(gate.GetHiddenTools("any-conversation"));
    }

    [Fact]
    public void Disabled_HidesAllFiveMemoryTools()
    {
        using var store = new MemorySettingsStore();
        store.Update(null, null, null, memoryEnabled: false);
        var gate = new MemoryDisabledToolGate(store);

        var hidden = gate.GetHiddenTools("any-conversation");

        Assert.Equal(5, hidden.Count);
        Assert.Contains("memory_search", hidden);
        Assert.Contains("memory_ingest", hidden);
        Assert.Contains("memory_get", hidden);
        Assert.Contains("memory_update", hidden);
        Assert.Contains("memory_delete", hidden);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=Cortex.Contained.Agent.Host.Tests.Tools.MemoryDisabledToolGateTests"`. Expected: FAIL to compile.

- [ ] **Step 3: Create the gate** (mirror `VoiceOnlyToolGate`)

`src/Cortex.Contained.Agent.Host/Tools/MemoryDisabledToolGate.cs`:
```csharp
using System.Collections.Frozen;
using Cortex.Contained.Agent.Host.Memory;

namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// Hides the built-in memory tool family from every conversation while the
/// memory subsystem is disabled (<see cref="MemorySettingsStore.IsMemoryEnabled"/> is false).
/// Uses the generic <see cref="IConversationToolGate"/> extension point so the model never
/// sees memory tools it cannot service (the embeddings sidecar is stopped when memory is off).
/// </summary>
public sealed class MemoryDisabledToolGate : IConversationToolGate
{
    private static readonly FrozenSet<string> memoryToolNames = FrozenSet.ToFrozenSet(
        [
            "memory_search",
            "memory_ingest",
            "memory_get",
            "memory_update",
            "memory_delete",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly MemorySettingsStore store;

    public MemoryDisabledToolGate(MemorySettingsStore store)
    {
        this.store = store;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> GetHiddenTools(string? conversationId)
    {
        return this.store.IsMemoryEnabled ? FrozenSet<string>.Empty : memoryToolNames;
    }
}
```

- [ ] **Step 4: Register the gate** in `Program.cs` next to the existing `VoiceOnlyToolGate` registration:
```csharp
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Tools.IConversationToolGate, Cortex.Contained.Agent.Host.Tools.MemoryDisabledToolGate>();
```
(Match the exact registration form used for `VoiceOnlyToolGate` — if it is `AddSingleton<IConversationToolGate, VoiceOnlyToolGate>()`, mirror it. `ToolRegistry` already accepts `IEnumerable<IConversationToolGate>`, so multiple gates compose automatically.)

- [ ] **Step 5: Run to verify pass** — same filter. Expected: PASS (2 cases).

- [ ] **Step 6: Commit**

```bash
git add src/Cortex.Contained.Agent.Host/Tools/MemoryDisabledToolGate.cs src/Cortex.Contained.Agent.Host/Program.cs tests/Cortex.Contained.Agent.Host.Tests/Tools/MemoryDisabledToolGateTests.cs
git commit -m "feat(memory): hide memory tools via IConversationToolGate when disabled"
```

---

### Task 4: Skip extraction + compaction when disabled

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Agent/CompactionOrchestrator.cs` (`FlushExtractionBuffer`, ~line 115; constructor ~line 92)
- Modify: `src/Cortex.Contained.Agent.Host/Agent/AgentRuntime.cs` (the `CompactionOrchestrator` construction, ~line 204 — pass the store)
- Modify: `src/Cortex.Contained.Agent.Host/Memory/MemoryCompactionService.cs` (tick loop — skip when disabled)
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Agent/CompactionOrchestratorMemoryGateTests.cs`

**Interfaces:**
- Consumes: `MemorySettingsStore.IsMemoryEnabled` (Task 2).
- Produces: `CompactionOrchestrator` gains an optional `MemorySettingsStore? memorySettingsStore = null` constructor parameter; `FlushExtractionBuffer` no-ops (drains + discards) when the store reports memory disabled.

- [ ] **Step 1: Write the failing test**

`tests/Cortex.Contained.Agent.Host.Tests/Agent/CompactionOrchestratorMemoryGateTests.cs`. Construct a `CompactionOrchestrator` with a substituted `MemoryExtractionService` (or a recording double) and a real `MemorySettingsStore`, append entries to a session's extraction buffer, call `FlushExtractionBuffer`, and assert the extraction service is NOT enqueued when disabled, and the buffer is drained:
```csharp
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Memory;

namespace Cortex.Contained.Agent.Host.Tests.Agent;

public sealed class CompactionOrchestratorMemoryGateTests
{
    [Fact]
    public void FlushExtractionBuffer_MemoryDisabled_DrainsWithoutEnqueue()
    {
        using var store = new MemorySettingsStore();
        store.Update(null, null, null, memoryEnabled: false);

        // EXTRACTION DOUBLE: see note below — MemoryExtractionService is concrete.
        // Use the same construction the existing CompactionOrchestrator tests use
        // (grep tests for `new CompactionOrchestrator(` / `EnqueueBatch`), substituting
        // an extraction service whose EnqueueBatch records calls.
        var (orchestrator, recorder) = TestObjects.BuildOrchestratorWithMemoryStore(store);
        var session = TestObjects.NewSessionWithExtractionEntries(count: 2);

        orchestrator.FlushExtractionBuffer(session, "conv-1");

        Assert.Equal(0, recorder.EnqueuedBatches);
        Assert.Equal(0, session.ExtractionBufferCount); // drained, not retained
    }

    [Fact]
    public void FlushExtractionBuffer_MemoryEnabled_Enqueues()
    {
        using var store = new MemorySettingsStore(); // enabled
        var (orchestrator, recorder) = TestObjects.BuildOrchestratorWithMemoryStore(store);
        var session = TestObjects.NewSessionWithExtractionEntries(count: 2);

        orchestrator.FlushExtractionBuffer(session, "conv-1");

        Assert.Equal(1, recorder.EnqueuedBatches);
    }
}
```
> **Implementer note:** `MemoryExtractionService` is a concrete type, not an interface. Before writing the gate, look at how existing `CompactionOrchestrator`/extraction tests construct it (grep `tests/` for `MemoryExtractionService` and `FlushExtractionBuffer`). If it cannot be cheaply substituted, take the simpler equivalent gate: keep `FlushExtractionBuffer` returning early (`if (!enabled) { session.DrainExtractionBuffer(); return; }`) and test that calling it with memory disabled leaves `session.ExtractionBufferCount == 0` and does not throw with a null extraction service — i.e. construct the orchestrator with `memoryExtraction: null` and `memorySettingsStore` disabled, append entries, flush, assert drained. Drop the `recorder.EnqueuedBatches` assertions in that case. Keep the test meaningful and green; do not invent helper APIs that don't exist.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Add the gate to `CompactionOrchestrator`**

Add the optional store to the constructor (after the existing `memoryExtraction` param) and a field:
```csharp
    private readonly MemorySettingsStore? memorySettingsStore;
```
```csharp
        Memory.MemoryExtractionService? memoryExtraction = null,
        Memory.MemorySettingsStore? memorySettingsStore = null)
```
```csharp
        this.memorySettingsStore = memorySettingsStore;
```
At the top of `FlushExtractionBuffer`, before the enqueue, drain-and-discard when disabled:
```csharp
    public void FlushExtractionBuffer(AgentSession session, string conversationId)
    {
        if (this.memorySettingsStore is { IsMemoryEnabled: false })
        {
            // Memory disabled — discard buffered entries instead of extracting.
            _ = session.DrainExtractionBuffer();
            return;
        }

        var entries = session.DrainExtractionBuffer();
        // ... existing body unchanged ...
```

- [ ] **Step 4: Thread the store from `AgentRuntime`**

In `AgentRuntime.cs` where `CompactionOrchestrator` is constructed (~line 204, currently passing `memoryExtraction`), pass the store. `AgentRuntime` already receives memory services; confirm it has a `MemorySettingsStore` (grep `MemorySettingsStore` in `AgentRuntime.cs` and `Program.cs` AgentRuntime registration). If `AgentRuntime` does not already take the store, add it as an optional constructor parameter `MemorySettingsStore? memorySettingsStore = null`, store it in a field, and forward it; update the DI registration in `Program.cs` to resolve and pass it. Pass it to the orchestrator:
```csharp
            memoryExtraction,
            memorySettingsStore);
```

- [ ] **Step 5: Gate the compaction sweep**

In `MemoryCompactionService.cs`, at the start of the periodic sweep body (inside the tick, before doing work), skip when memory is disabled. The service already has access to settings via `IOptionsMonitor`/store; inject `MemorySettingsStore` if not already present and add:
```csharp
        if (!this.memorySettingsStore.IsMemoryEnabled)
        {
            return; // or `continue;` depending on the loop shape — skip this sweep tick
        }
```
(Match the loop shape: if the body is a method invoked per tick, `return`; if it is an inline `while`/`await foreach` loop, `continue` to the next delay.)

- [ ] **Step 6: Run to verify pass + full agent-host build**

Run: `dotnet build src/Cortex.Contained.Agent.Host && dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~Memory|FullyQualifiedName~CompactionOrchestrator"`
Expected: build OK; extraction-gate + store tests PASS; pre-existing compaction tests still PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Cortex.Contained.Agent.Host/Agent/CompactionOrchestrator.cs src/Cortex.Contained.Agent.Host/Agent/AgentRuntime.cs src/Cortex.Contained.Agent.Host/Memory/MemoryCompactionService.cs tests/Cortex.Contained.Agent.Host.Tests/Agent/CompactionOrchestratorMemoryGateTests.cs
git commit -m "feat(memory): skip extraction + compaction when memory disabled"
```

---

### Task 5: CredentialsPusher maps `Enabled` into the pushed DTO

**Files:**
- Modify: `src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs` (`BuildMemoryConfig`, ~line 337)
- Test: `tests/Cortex.Contained.Bridge.Tests/Memory/BuildMemoryConfigTests.cs` (extend if it exists; else create)

**Interfaces:**
- Consumes: `MemorySettingsConfig.Enabled` (Task 1).
- Produces: `BuildMemoryConfig()` sets `MemoryConfig.Enabled = mem.Enabled`.

- [ ] **Step 1: Write the failing test**

`tests/Cortex.Contained.Bridge.Tests/Memory/BuildMemoryConfigTests.cs` — if a `CredentialsPusher` test harness already exists, follow it; otherwise the simplest unit is to construct a `CredentialsPusher` with the minimal substitutes its constructor needs and assert `BuildMemoryConfig()` round-trips `Enabled`. Inspect the constructor first (grep `public CredentialsPusher(`). Skeleton:
```csharp
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Memory;

public sealed class BuildMemoryConfigTests
{
    [Fact]
    public void BuildMemoryConfig_PropagatesEnabledFalse()
    {
        var pusher = CredentialsPusherTestFactory.Create(config =>
            config.Memory = new MemorySettingsConfig { Enabled = false });

        var dto = pusher.BuildMemoryConfig();

        Assert.False(dto.Enabled);
    }

    [Fact]
    public void BuildMemoryConfig_DefaultEnabledTrue()
    {
        var pusher = CredentialsPusherTestFactory.Create(_ => { });
        Assert.True(pusher.BuildMemoryConfig().Enabled);
    }
}
```
> `BuildMemoryConfig` is `internal` — the Bridge test project already has `InternalsVisibleTo` (Slice 1 tested internal types). If a `CredentialsPusherTestFactory` does not exist, build the pusher inline with NSubstitute substitutes for its dependencies (the constructor takes a config, a secret manager, and an endpoint resolver — substitute each). Keep it minimal: only `config.Memory` matters for this test.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Map the flag** in `BuildMemoryConfig`, add to the object initializer:
```csharp
            Enabled = mem.Enabled,
```

- [ ] **Step 4: Run to verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs tests/Cortex.Contained.Bridge.Tests/Memory/BuildMemoryConfigTests.cs
git commit -m "feat(memory): push Enabled flag to agent via BuildMemoryConfig"
```

---

### Task 6: Embeddings sidecar lifecycle (Bridge) + compose profile

**Files:**
- Modify: `src/Cortex.Contained.Bridge/Speech/DockerComposeCommandRunner.cs` (add embeddings start/stop/isRunning)
- Create: `src/Cortex.Contained.Bridge/Speech/IEmbeddingsComposeRunner.cs`
- Create: `src/Cortex.Contained.Bridge/Speech/EmbeddingsSidecarLifecycle.cs`
- Modify: `src/Cortex.Contained.Bridge/Program.cs` (DI registration ~line 860; startup reconcile ~line 895)
- Modify: `docker-compose.yml` (add `profiles: [memory]` to `embeddings`; remove the `embeddings` entry from `cortex-agent.depends_on`)
- Test: `tests/Cortex.Contained.Bridge.Tests/Speech/EmbeddingsSidecarLifecycleTests.cs`

**Interfaces:**
- Produces: `IEmbeddingsComposeRunner` with `StartEmbeddingsAsync`/`StopEmbeddingsAsync`/`IsEmbeddingsRunningAsync`; `EmbeddingsSidecarLifecycle.ReconcileAsync(bool enabled, CancellationToken)`.

> Naming note: this lives in the `Speech` folder/namespace only because that is where the existing compose-runner seam lives (`DockerComposeCommandRunner` already implements both `IComposeCommandRunner` and `ISttComposeRunner`). Keep it there for consistency; it is the Bridge's single docker-compose seam, not speech-specific.

- [ ] **Step 1: Write the failing tests** (mirror `SttSidecarLifecycleTests.cs` exactly, with embeddings names)

`tests/Cortex.Contained.Bridge.Tests/Speech/EmbeddingsSidecarLifecycleTests.cs`:
```csharp
using Cortex.Contained.Bridge.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class EmbeddingsSidecarLifecycleTests
{
    private static EmbeddingsSidecarLifecycle Sut(IEmbeddingsComposeRunner runner) =>
        new(runner, NullLogger<EmbeddingsSidecarLifecycle>.Instance);

    [Fact]
    public async Task Enabled_NotRunning_Starts()
    {
        var runner = Substitute.For<IEmbeddingsComposeRunner>();
        runner.IsEmbeddingsRunningAsync(Arg.Any<CancellationToken>()).Returns(false);

        await Sut(runner).ReconcileAsync(enabled: true, CancellationToken.None);

        await runner.Received(1).StartEmbeddingsAsync(Arg.Any<CancellationToken>());
        await runner.DidNotReceive().StopEmbeddingsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enabled_AlreadyRunning_NoOp()
    {
        var runner = Substitute.For<IEmbeddingsComposeRunner>();
        runner.IsEmbeddingsRunningAsync(Arg.Any<CancellationToken>()).Returns(true);

        await Sut(runner).ReconcileAsync(enabled: true, CancellationToken.None);

        await runner.DidNotReceive().StartEmbeddingsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_Running_Stops()
    {
        var runner = Substitute.For<IEmbeddingsComposeRunner>();
        runner.IsEmbeddingsRunningAsync(Arg.Any<CancellationToken>()).Returns(true);

        await Sut(runner).ReconcileAsync(enabled: false, CancellationToken.None);

        await runner.Received(1).StopEmbeddingsAsync(Arg.Any<CancellationToken>());
        await runner.DidNotReceive().StartEmbeddingsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_NotRunning_NoOp()
    {
        var runner = Substitute.For<IEmbeddingsComposeRunner>();
        runner.IsEmbeddingsRunningAsync(Arg.Any<CancellationToken>()).Returns(false);

        await Sut(runner).ReconcileAsync(enabled: false, CancellationToken.None);

        await runner.DidNotReceive().StopEmbeddingsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_RunnerThrows_DoesNotPropagate()
    {
        var runner = Substitute.For<IEmbeddingsComposeRunner>();
        runner.IsEmbeddingsRunningAsync(Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("docker exploded"));

        await Sut(runner).ReconcileAsync(enabled: true, CancellationToken.None);
    }
}
```

- [ ] **Step 2: Run to verify failure** (types don't exist).

- [ ] **Step 3: Add the seam interface**

`src/Cortex.Contained.Bridge/Speech/IEmbeddingsComposeRunner.cs`:
```csharp
namespace Cortex.Contained.Bridge.Speech;

/// <summary>Narrow seam over the docker-compose CLI for the cortex-embeddings sidecar.</summary>
public interface IEmbeddingsComposeRunner
{
    /// <summary>`docker compose --profile memory up -d embeddings`. Returns true on exit 0.</summary>
    Task<bool> StartEmbeddingsAsync(CancellationToken cancellationToken);

    /// <summary>`docker compose --profile memory stop embeddings`. Returns true on exit 0.</summary>
    Task<bool> StopEmbeddingsAsync(CancellationToken cancellationToken);

    /// <summary>True if the `cortex-embeddings` container is currently running.</summary>
    Task<bool> IsEmbeddingsRunningAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement on `DockerComposeCommandRunner`**

Add `IEmbeddingsComposeRunner` to the class's implements list (line 12). Add the container-name const next to `SttContainerName`:
```csharp
    private const string EmbeddingsContainerName = "cortex-embeddings";
```
Add the methods (mirror `StartSttAsync`/`StopSttAsync`/`IsSttRunningAsync`, profile `memory`, service `embeddings`):
```csharp
    /// <inheritdoc />
    public Task<bool> StartEmbeddingsAsync(CancellationToken cancellationToken)
        => this.RunCommandAsync(
            $"compose -f \"{this.composeFilePath}\" --profile memory up -d embeddings",
            StartTimeout,
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> StopEmbeddingsAsync(CancellationToken cancellationToken)
        => this.RunCommandAsync(
            $"compose -f \"{this.composeFilePath}\" --profile memory stop embeddings",
            ShortTimeout,
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsEmbeddingsRunningAsync(CancellationToken cancellationToken)
        => this.IsContainerRunningAsync(EmbeddingsContainerName, cancellationToken);
```

- [ ] **Step 5: Create the lifecycle** (mirror `SttSidecarLifecycle.cs`)

`src/Cortex.Contained.Bridge/Speech/EmbeddingsSidecarLifecycle.cs`:
```csharp
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Converges the cortex-embeddings sidecar with the built-in-memory enable flag:
/// start-if-down when enabled, stop-if-up when disabled. Failures are logged, never propagated.
/// </summary>
public sealed partial class EmbeddingsSidecarLifecycle
{
    private readonly IEmbeddingsComposeRunner runner;
    private readonly ILogger<EmbeddingsSidecarLifecycle> logger;

    public EmbeddingsSidecarLifecycle(IEmbeddingsComposeRunner runner, ILogger<EmbeddingsSidecarLifecycle> logger)
    {
        this.runner = runner;
        this.logger = logger;
    }

    public async Task ReconcileAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            var running = await this.runner.IsEmbeddingsRunningAsync(cancellationToken).ConfigureAwait(false);
            if (enabled && !running)
            {
                this.LogStarting();
                await this.runner.StartEmbeddingsAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (!enabled && running)
            {
                this.LogStopping();
                await this.runner.StopEmbeddingsAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            this.LogReconcileFailed(ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "memory enabled — starting cortex-embeddings")]
    private partial void LogStarting();

    [LoggerMessage(Level = LogLevel.Information, Message = "memory disabled — stopping cortex-embeddings")]
    private partial void LogStopping();

    [LoggerMessage(Level = LogLevel.Warning, Message = "cortex-embeddings reconcile failed: {Error}")]
    private partial void LogReconcileFailed(string error);
}
```

- [ ] **Step 6: Register + startup reconcile** in `Program.cs`

Registration (next to the STT runner/lifecycle registrations ~line 860):
```csharp
builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.IEmbeddingsComposeRunner>(
    sp => sp.GetRequiredService<Cortex.Contained.Bridge.Speech.DockerComposeCommandRunner>());
builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.EmbeddingsSidecarLifecycle>();
```
Startup fire-and-forget reconcile (next to the STT startup block ~line 895):
```csharp
// --- Embeddings sidecar lifecycle: converge with memory enable flag at startup ---
{
    var embeddingsLifecycle = app.Services.GetRequiredService<Cortex.Contained.Bridge.Speech.EmbeddingsSidecarLifecycle>();
    var memEnabled = app.Services.GetRequiredService<BridgeConfig>().Memory.Enabled;
    _ = Task.Run(() => embeddingsLifecycle.ReconcileAsync(memEnabled, CancellationToken.None));
}
```

- [ ] **Step 7: docker-compose changes**

In `docker-compose.yml`, add to the `embeddings` service (mirror how `stt` declares `profiles: [voice]`):
```yaml
    profiles: [memory]
```
And in the `cortex-agent` service, REMOVE the `embeddings` dependency from `depends_on` (leave `voice-id` until Slice 3):
```yaml
    depends_on:
      voice-id:
        condition: service_started
```
> Rationale: with `profiles: [memory]`, `embeddings` is no longer part of a default `docker compose up`, so a `depends_on: embeddings` would block `cortex-agent` from starting whenever memory is enabled-but-not-yet-started-by-the-Bridge (or disabled). The Bridge lifecycle owns starting it; memory access is lazy/tool-driven, so the agent does not need to wait for it at boot.

- [ ] **Step 8: Build + test + commit**

Run: `dotnet build src/Cortex.Contained.Bridge && dotnet test tests/Cortex.Contained.Bridge.Tests --filter "ClassName=Cortex.Contained.Bridge.Tests.Speech.EmbeddingsSidecarLifecycleTests"`
Expected: build OK; 5 lifecycle tests PASS.
```bash
git add src/Cortex.Contained.Bridge/Speech/ src/Cortex.Contained.Bridge/Program.cs docker-compose.yml tests/Cortex.Contained.Bridge.Tests/Speech/EmbeddingsSidecarLifecycleTests.cs
git commit -m "feat(memory): embeddings sidecar lifecycle + memory compose profile (live start/stop)"
```

---

### Task 7: Toggle endpoint + expose flag in settings GET

**Files:**
- Create: `src/Cortex.Contained.Bridge/Endpoints/MemoryToggleApply.cs`
- Modify: `src/Cortex.Contained.Bridge/Endpoints/MemoryEndpoints.cs` (add `POST /api/memory/toggle`; add `enabled` to the GET payload, ~line 172)
- Modify: `src/Cortex.Contained.Bridge/Endpoints/SettingsEndpoints.cs` (add `memory = new { enabled = ... }` to `/api/settings`)
- Modify: `src/Cortex.Contained.Bridge/SetupHelpers.cs` (add a `MemoryToggleRequest` request type near `SpeechTogglesRequest`, ~line 1119)
- Test: `tests/Cortex.Contained.Bridge.Tests/Memory/MemoryToggleApplyTests.cs`

**Interfaces:**
- Produces: `static void MemoryToggleApply.Apply(MemorySettingsConfig, bool? enabled)`; `POST /api/memory/toggle` accepting `{ enabled: bool }`, returning `{ success, enabled }`; `MemoryToggleRequest { bool? Enabled }`.
- Consumes: `EmbeddingsSidecarLifecycle` (Task 6); `MemorySettingsConfig.Enabled` (Task 1); the existing agent push (`UpdateMemoryConfigAsync` used by `PUT /api/memory/settings`).

- [ ] **Step 1: Write the failing test**

`tests/Cortex.Contained.Bridge.Tests/Memory/MemoryToggleApplyTests.cs`:
```csharp
using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Memory;

public sealed class MemoryToggleApplyTests
{
    [Fact]
    public void Apply_SetsEnabledFalse()
    {
        var mem = new MemorySettingsConfig(); // true by default
        MemoryToggleApply.Apply(mem, enabled: false);
        Assert.False(mem.Enabled);
    }

    [Fact]
    public void Apply_NullLeavesUnchanged()
    {
        var mem = new MemorySettingsConfig { Enabled = false };
        MemoryToggleApply.Apply(mem, enabled: null);
        Assert.False(mem.Enabled);
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Add the apply helper**

`src/Cortex.Contained.Bridge/Endpoints/MemoryToggleApply.cs`:
```csharp
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>Applies an optional built-in-memory enable toggle to <see cref="MemorySettingsConfig"/>. Null = leave as-is.</summary>
public static class MemoryToggleApply
{
    public static void Apply(MemorySettingsConfig memory, bool? enabled)
    {
        if (enabled.HasValue)
        {
            memory.Enabled = enabled.Value;
        }
    }
}
```

- [ ] **Step 4: Run to verify pass.**

- [ ] **Step 5: Add the request type** in `SetupHelpers.cs` (mirror `SpeechTogglesRequest`):
```csharp
/// <summary>Built-in-memory enable-toggle request from the settings page.</summary>
public sealed class MemoryToggleRequest
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}
```

- [ ] **Step 6: Add the endpoint** in `MemoryEndpoints.cs` (mirror the speech toggles endpoint: apply → persist → push to agent → reconcile sidecar):
```csharp
        // Built-in-memory master toggle. Persists to YAML, pushes the flag to the
        // agent, and converges the embeddings sidecar live.
        app.MapPost("/api/memory/toggle", async (HttpContext ctx, BridgeConfig config, TenantRouter tenantRouter, IServiceProvider sp) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<MemoryToggleRequest>();
            if (body is null)
            {
                return Results.Json(new { error = "body is required" }, statusCode: 400);
            }

            MemoryToggleApply.Apply(config.Memory, body.Enabled);
            BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath);

            // Push the updated memory config to the agent so the in-process gates flip live.
            var client = tenantRouter.GetDefaultClient();
            if (client is { IsConnected: true })
            {
                var pusher = sp.GetRequiredService<CredentialsPusher>();
                try
                {
                    await client.UpdateMemoryConfigAsync(pusher.BuildMemoryConfig(), CancellationToken.None);
                }
                catch
                {
                    // best-effort; the agent re-syncs on next connect
                }
            }

            var embeddings = sp.GetRequiredService<Cortex.Contained.Bridge.Speech.EmbeddingsSidecarLifecycle>();
            _ = Task.Run(() => embeddings.ReconcileAsync(config.Memory.Enabled, CancellationToken.None));

            return Results.Ok(new { success = true, enabled = config.Memory.Enabled });
        }).RequireAuthorization();
```
> Verify the exact way `PUT /api/memory/settings` obtains the tenant client and pusher (grep `UpdateMemoryConfigAsync` and `CredentialsPusher` in `MemoryEndpoints.cs`) and match it — DI shapes (whether `CredentialsPusher` is injected directly vs. resolved from `sp`) must mirror the existing memory-settings save path. Adjust the snippet to the real signatures.

- [ ] **Step 7: Expose `enabled` in GETs**

In `MemoryEndpoints.cs` `GET /api/memory/settings`, add `Enabled = mem.Enabled` to the fallback `MemoryConfig` projection (so the UI can read it even when the agent is disconnected). In `SettingsEndpoints.cs` `/api/settings`, add a `memory` projection alongside `speech`:
```csharp
        memory = new { enabled = config.Memory.Enabled },
```

- [ ] **Step 8: Build + test + commit**

Run: `dotnet build src/Cortex.Contained.Bridge && dotnet test tests/Cortex.Contained.Bridge.Tests --filter "ClassName=Cortex.Contained.Bridge.Tests.Memory.MemoryToggleApplyTests"`
Expected: build OK; tests PASS.
```bash
git add src/Cortex.Contained.Bridge/Endpoints/ src/Cortex.Contained.Bridge/SetupHelpers.cs tests/Cortex.Contained.Bridge.Tests/Memory/MemoryToggleApplyTests.cs
git commit -m "feat(memory): /api/memory/toggle endpoint + expose enabled in settings"
```

---

### Task 8: Web UI memory toggle

**Files:**
- Modify: `src/Cortex.Contained.Bridge/wwwroot/js/pages/global-settings.js`
- Modify: `src/Cortex.Contained.Bridge/wwwroot/app.html` (Memory tab, ~line 1515)

**Interfaces:**
- Consumes: `GET /api/settings` → `memory.enabled`; `POST /api/memory/toggle`.

- [ ] **Step 1: Add state + load + save** in `global-settings.js` (mirror the speech-toggle functions ~line 106 / 552 / 558)

Add to the component state:
```javascript
// Built-in memory master toggle
memoryEnabled: true,
savingMemoryToggle: false,
```
In the settings-load handler (where `loadSpeechTogglesFromSettings`/`_applySpeechToggles` run), apply memory:
```javascript
loadMemoryToggleFromSettings(data) {
    if (data?.memory) {
        this.memoryEnabled = !!data.memory.enabled;
    }
},

async saveMemoryToggle() {
    this.savingMemoryToggle = true;
    try {
        const data = await api.post("/api/memory/toggle", { enabled: this.memoryEnabled });
        if (data?.success) {
            this.memoryEnabled = !!data.enabled;
            Alpine.store("toast").success("Memory " + (data.enabled ? "enabled" : "disabled"));
        } else {
            Alpine.store("toast").error("Failed to save memory toggle");
            await this.loadSettings();
        }
    } catch (e) {
        Alpine.store("toast").error("Failed to save memory toggle: " + e.message);
        await this.loadSettings();
    } finally {
        this.savingMemoryToggle = false;
    }
},
```
Call `loadMemoryToggleFromSettings(data)` wherever `loadSpeechTogglesFromSettings(data)` is called after the `/api/settings` fetch.

- [ ] **Step 2: Add markup** in `app.html` Memory tab (mirror the `speech-enabled` checkbox), placed at the TOP of the Memory tab so the existing threshold/compaction controls read as subordinate detail:
```html
<div class="form-check mb-3">
    <input class="form-check-input" type="checkbox" id="memory-enabled"
           :checked="memoryEnabled"
           :disabled="savingMemoryToggle"
           @change="memoryEnabled = $event.target.checked; saveMemoryToggle()">
    <label class="form-check-label" for="memory-enabled">Built-in memory enabled</label>
    <div class="form-text">When off, the agent runs with no long-term memory and the embeddings container is stopped. Existing memories are preserved and restored when re-enabled.</div>
</div>
```

- [ ] **Step 3: Manual verification** (no JS test harness in this repo)

Run the Bridge (`.\scripts\Start-Cortex.ps1 -BridgeOnly`), open `http://127.0.0.1:5080`, Settings → Memory:
- Toggle **memory off** → `docker ps` shows `cortex-embeddings` stopping; the agent's tool list no longer advertises `memory_*` (confirm via a chat turn or agent logs).
- Toggle **memory on** → `cortex-embeddings` starts; memory tools return.
- Reload page → state persists (written to `cortex.yml`).

- [ ] **Step 4: Commit**

```bash
git add src/Cortex.Contained.Bridge/wwwroot/
git commit -m "feat(memory): web UI built-in-memory toggle"
```

---

## Slice 2 self-review (run before handoff)

- **Spec coverage:** memory master switch ✅ (Task 1/2/7), tools hidden ✅ (Task 3), extraction+compaction skipped ✅ (Task 4), live sidecar start/stop ✅ (Task 6), pushed to agent live ✅ (Task 5/7), persistence ✅ (Task 7), UI ✅ (Task 8), defaults-true ✅ (Task 1). Existing memories preserved (functional disable only) ✅.
- **Boot safety (memory-off):** `cortex-agent.depends_on.embeddings` removed (Task 6) so a memory-off `docker compose up` does not hang waiting for a profile-gated, non-started container. The agent's memory services tolerate a missing embeddings endpoint because access is lazy/tool-driven and the tools are hidden when disabled.
- **Live vs restart:** all gates are runtime (store-backed, pushed over SignalR) + the sidecar reconciles live — no restart-required badge needed.
- **Open verification:** Task 4's extraction-gate test must be adapted to the real `MemoryExtractionService` construction (it is concrete, not an interface) — see the implementer note; keep the test meaningful and green.

---

## Roadmap (remaining slice)

**Slice 3 — Voice-id toggle** — separate plan: `docs/plans/2026-06-27-voiceid-toggle-plan.md`.
