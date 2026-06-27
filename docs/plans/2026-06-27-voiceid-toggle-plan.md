# Voice-ID (Speaker Identification) Enable/Disable Toggle — Implementation Plan (Slice 3 of 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a voice-id (speaker identification) sub-toggle under the master speech switch (default `true`) that starts/stops the `voice-id` Docker sidecar live, skips speaker verification on the Bridge when off, hides voice-enrollment tools on the agent when off, and falls back to a "restart required" UI badge if a live compose op fails.

**Architecture:** Mirrors the Slice 1 STT/TTS pattern. A new `VoiceIdConfig` (with `Enabled`, default `true`) under `SpeechConfig`; `SpeechToggles.EffectiveVoiceId(speech) = speech.Enabled && speech.VoiceId.Enabled`. The `voice-id` sidecar gains `profiles: [voiceid]` and a `VoiceIdSidecarLifecycle` (mirror of `SttSidecarLifecycle`) reconciles it; `cortex-agent.depends_on.voice-id` is removed. Bridge-side, the voice channel skips verification when `EffectiveVoiceId` is false. Agent-side, a new `VoiceIdEnabled` flag is pushed over SignalR into a small store, and a `VoiceIdDisabledToolGate : IConversationToolGate` hides the voice-enrollment tool family when off. The existing `/api/speech/toggles` endpoint, `SpeechToggleApply`, and the web UI gain a `voiceIdEnabled` field; the endpoint returns a `restartRequired` flag when the live compose reconcile cannot be confirmed.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, SignalR, xUnit + NSubstitute, vanilla JS + Alpine, `docker compose` CLI shell-out.

**Parent spec:** `docs/specs/2026-06-27-enable-disable-toggles-voice-memory-design.md`. **Predecessors:** Slice 1 (speech master/STT/TTS, shipped), Slice 2 (memory, `docs/plans/2026-06-27-memory-toggle-plan.md`).

## Global Constraints

- .NET 10; `TreatWarningsAsErrors` on — warning-clean code.
- C# style: `this.`-prefixed instance access, braces on all control blocks, one type per file, file-scoped namespaces, `ConfigureAwait(false)` in Bridge service/lifecycle and Agent library code.
- The new `Enabled` flag defaults to `true` (config + effective) — existing `cortex.yml` and current tests behave identically.
- Test naming: `Method_Condition_Expected` (CA1707 suppressed in test projects).
- Persistence via `BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath)`.
- Sidecar reconcile is fire-and-forget; the toggle endpoint additionally performs a best-effort *synchronous* probe only to decide the `restartRequired` response flag (never blocking on the full start/stop).

---

### Task 1: `VoiceIdConfig` + effective-state helper

**Files:**
- Modify: `src/Cortex.Contained.Contracts/Config/BridgeConfig.cs` (`SpeechConfig` ~line 233; new `VoiceIdConfig`)
- Modify: `src/Cortex.Contained.Contracts/Config/SpeechToggles.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/Speech/SpeechTogglesVoiceIdTests.cs`

**Interfaces:**
- Produces: `SpeechConfig.VoiceId` (`VoiceIdConfig`, default `new()`); `VoiceIdConfig.Enabled` (`bool`, default `true`); `static bool SpeechToggles.EffectiveVoiceId(SpeechConfig)`.

- [ ] **Step 1: Write the failing test**

`tests/Cortex.Contained.Bridge.Tests/Speech/SpeechTogglesVoiceIdTests.cs`:
```csharp
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class SpeechTogglesVoiceIdTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void EffectiveVoiceId_IsMasterAndSub(bool master, bool sub, bool expected)
    {
        var speech = new SpeechConfig { Enabled = master, VoiceId = new VoiceIdConfig { Enabled = sub } };
        Assert.Equal(expected, SpeechToggles.EffectiveVoiceId(speech));
    }

    [Fact]
    public void EffectiveVoiceId_DefaultsEnabled()
    {
        Assert.True(SpeechToggles.EffectiveVoiceId(new SpeechConfig()));
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "ClassName=Cortex.Contained.Bridge.Tests.Speech.SpeechTogglesVoiceIdTests"`. Expected: FAIL to compile.

- [ ] **Step 3: Add config**

In `BridgeConfig.cs`, add to `SpeechConfig` (after `Tts`):
```csharp
    /// <summary>Speaker-identification (voice-id) settings.</summary>
    public VoiceIdConfig VoiceId { get; set; } = new();
```
Add the new type (own block, mirroring `SttConfig`/`TtsConfig` style — keep it in `BridgeConfig.cs` alongside the other speech sub-configs to match the existing file layout):
```csharp
/// <summary>Speaker-identification (voice-id) settings.</summary>
public sealed class VoiceIdConfig
{
    /// <summary>Whether speaker verification/enrollment is enabled (gated by <see cref="SpeechConfig.Enabled"/>).</summary>
    public bool Enabled { get; set; } = true;
}
```

- [ ] **Step 4: Extend the helper** in `SpeechToggles.cs`:
```csharp
    /// <summary>True when voice-id should run: master AND voice-id flag.</summary>
    public static bool EffectiveVoiceId(SpeechConfig speech) => speech.Enabled && speech.VoiceId.Enabled;
```

- [ ] **Step 5: Run to verify pass** — same filter. Expected: PASS (5 cases).

- [ ] **Step 6: Commit**

```bash
git add src/Cortex.Contained.Contracts/Config/BridgeConfig.cs src/Cortex.Contained.Contracts/Config/SpeechToggles.cs tests/Cortex.Contained.Bridge.Tests/Speech/SpeechTogglesVoiceIdTests.cs
git commit -m "feat(voiceid): VoiceIdConfig.Enabled + EffectiveVoiceId helper"
```

---

### Task 2: Voice-id sidecar lifecycle + compose profile

**Files:**
- Create: `src/Cortex.Contained.Bridge/Speech/IVoiceIdComposeRunner.cs`
- Modify: `src/Cortex.Contained.Bridge/Speech/DockerComposeCommandRunner.cs`
- Create: `src/Cortex.Contained.Bridge/Speech/VoiceIdSidecarLifecycle.cs`
- Modify: `src/Cortex.Contained.Bridge/Program.cs` (DI ~line 860; startup reconcile ~line 895)
- Modify: `docker-compose.yml` (`voice-id` gains `profiles: [voiceid]`; remove `voice-id` from `cortex-agent.depends_on`)
- Test: `tests/Cortex.Contained.Bridge.Tests/Speech/VoiceIdSidecarLifecycleTests.cs`

**Interfaces:**
- Produces: `IVoiceIdComposeRunner` (`StartVoiceIdAsync`/`StopVoiceIdAsync`/`IsVoiceIdRunningAsync`); `VoiceIdSidecarLifecycle.ReconcileAsync(bool enabled, CancellationToken)`; `VoiceIdSidecarLifecycle.TryReconcileNowAsync(bool enabled, CancellationToken) -> Task<bool>` (returns false when a live compose op could not be confirmed — drives the UI `restartRequired` badge).

> **Container/service names** (from `docker-compose.yml`): service `voice-id`, container `cortex-voice-id`.

- [ ] **Step 1: Write the failing tests** (mirror `SttSidecarLifecycleTests.cs`, voice-id names; plus one for the `TryReconcileNowAsync` confirm-path):
```csharp
using Cortex.Contained.Bridge.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class VoiceIdSidecarLifecycleTests
{
    private static VoiceIdSidecarLifecycle Sut(IVoiceIdComposeRunner runner) =>
        new(runner, NullLogger<VoiceIdSidecarLifecycle>.Instance);

    [Fact]
    public async Task Enabled_NotRunning_Starts()
    {
        var runner = Substitute.For<IVoiceIdComposeRunner>();
        runner.IsVoiceIdRunningAsync(Arg.Any<CancellationToken>()).Returns(false);
        await Sut(runner).ReconcileAsync(enabled: true, CancellationToken.None);
        await runner.Received(1).StartVoiceIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_Running_Stops()
    {
        var runner = Substitute.For<IVoiceIdComposeRunner>();
        runner.IsVoiceIdRunningAsync(Arg.Any<CancellationToken>()).Returns(true);
        await Sut(runner).ReconcileAsync(enabled: false, CancellationToken.None);
        await runner.Received(1).StopVoiceIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_NotRunning_NoOp()
    {
        var runner = Substitute.For<IVoiceIdComposeRunner>();
        runner.IsVoiceIdRunningAsync(Arg.Any<CancellationToken>()).Returns(false);
        await Sut(runner).ReconcileAsync(enabled: false, CancellationToken.None);
        await runner.DidNotReceive().StopVoiceIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_RunnerThrows_DoesNotPropagate()
    {
        var runner = Substitute.For<IVoiceIdComposeRunner>();
        runner.IsVoiceIdRunningAsync(Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("boom"));
        await Sut(runner).ReconcileAsync(enabled: true, CancellationToken.None);
    }

    [Fact]
    public async Task TryReconcileNowAsync_StartSucceeds_ReturnsTrue()
    {
        var runner = Substitute.For<IVoiceIdComposeRunner>();
        runner.IsVoiceIdRunningAsync(Arg.Any<CancellationToken>()).Returns(false);
        runner.StartVoiceIdAsync(Arg.Any<CancellationToken>()).Returns(true);
        Assert.True(await Sut(runner).TryReconcileNowAsync(enabled: true, CancellationToken.None));
    }

    [Fact]
    public async Task TryReconcileNowAsync_StartFails_ReturnsFalse()
    {
        var runner = Substitute.For<IVoiceIdComposeRunner>();
        runner.IsVoiceIdRunningAsync(Arg.Any<CancellationToken>()).Returns(false);
        runner.StartVoiceIdAsync(Arg.Any<CancellationToken>()).Returns(false);
        Assert.False(await Sut(runner).TryReconcileNowAsync(enabled: true, CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Seam interface** `IVoiceIdComposeRunner.cs`:
```csharp
namespace Cortex.Contained.Bridge.Speech;

/// <summary>Narrow seam over the docker-compose CLI for the cortex-voice-id sidecar.</summary>
public interface IVoiceIdComposeRunner
{
    /// <summary>`docker compose --profile voiceid up -d voice-id`. Returns true on exit 0.</summary>
    Task<bool> StartVoiceIdAsync(CancellationToken cancellationToken);

    /// <summary>`docker compose --profile voiceid stop voice-id`. Returns true on exit 0.</summary>
    Task<bool> StopVoiceIdAsync(CancellationToken cancellationToken);

    /// <summary>True if the `cortex-voice-id` container is currently running.</summary>
    Task<bool> IsVoiceIdRunningAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement on `DockerComposeCommandRunner`** — add `IVoiceIdComposeRunner` to the implements list, add `private const string VoiceIdContainerName = "cortex-voice-id";`, and:
```csharp
    /// <inheritdoc />
    public Task<bool> StartVoiceIdAsync(CancellationToken cancellationToken)
        => this.RunCommandAsync(
            $"compose -f \"{this.composeFilePath}\" --profile voiceid up -d voice-id",
            StartTimeout,
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> StopVoiceIdAsync(CancellationToken cancellationToken)
        => this.RunCommandAsync(
            $"compose -f \"{this.composeFilePath}\" --profile voiceid stop voice-id",
            ShortTimeout,
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsVoiceIdRunningAsync(CancellationToken cancellationToken)
        => this.IsContainerRunningAsync(VoiceIdContainerName, cancellationToken);
```

- [ ] **Step 5: Lifecycle** `VoiceIdSidecarLifecycle.cs` (mirror `SttSidecarLifecycle`, add `TryReconcileNowAsync` returning a confirm bool):
```csharp
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Converges the cortex-voice-id sidecar with the effective voice-id enable flag:
/// start-if-down when enabled, stop-if-up when disabled. <see cref="ReconcileAsync"/> is the
/// fire-and-forget startup path; <see cref="TryReconcileNowAsync"/> performs the same work but
/// returns whether the live compose op was confirmed (false ⇒ UI shows "restart required").
/// </summary>
public sealed partial class VoiceIdSidecarLifecycle
{
    private readonly IVoiceIdComposeRunner runner;
    private readonly ILogger<VoiceIdSidecarLifecycle> logger;

    public VoiceIdSidecarLifecycle(IVoiceIdComposeRunner runner, ILogger<VoiceIdSidecarLifecycle> logger)
    {
        this.runner = runner;
        this.logger = logger;
    }

    public Task ReconcileAsync(bool enabled, CancellationToken cancellationToken)
        => this.TryReconcileNowAsync(enabled, cancellationToken);

    /// <summary>Reconcile and report whether the desired state was achieved (true) or a live
    /// compose op failed/threw (false). Never throws.</summary>
    public async Task<bool> TryReconcileNowAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            var running = await this.runner.IsVoiceIdRunningAsync(cancellationToken).ConfigureAwait(false);
            if (enabled && !running)
            {
                this.LogStarting();
                return await this.runner.StartVoiceIdAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!enabled && running)
            {
                this.LogStopping();
                return await this.runner.StopVoiceIdAsync(cancellationToken).ConfigureAwait(false);
            }

            return true; // already in desired state
        }
        catch (Exception ex)
        {
            this.LogReconcileFailed(ex.Message);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-id enabled — starting cortex-voice-id")]
    private partial void LogStarting();

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-id disabled — stopping cortex-voice-id")]
    private partial void LogStopping();

    [LoggerMessage(Level = LogLevel.Warning, Message = "cortex-voice-id reconcile failed: {Error}")]
    private partial void LogReconcileFailed(string error);
}
```

- [ ] **Step 6: Register + startup reconcile** in `Program.cs`:
```csharp
builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.IVoiceIdComposeRunner>(
    sp => sp.GetRequiredService<Cortex.Contained.Bridge.Speech.DockerComposeCommandRunner>());
builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.VoiceIdSidecarLifecycle>();
```
```csharp
// --- Voice-id sidecar lifecycle: converge with effective voice-id flag at startup ---
{
    var voiceIdLifecycle = app.Services.GetRequiredService<Cortex.Contained.Bridge.Speech.VoiceIdSidecarLifecycle>();
    var speech = app.Services.GetRequiredService<BridgeConfig>().Speech;
    _ = Task.Run(() => voiceIdLifecycle.ReconcileAsync(SpeechToggles.EffectiveVoiceId(speech), CancellationToken.None));
}
```

- [ ] **Step 7: docker-compose** — add to the `voice-id` service:
```yaml
    profiles: [voiceid]
```
and REMOVE the `voice-id` entry from `cortex-agent.depends_on` (after Slice 2 removed `embeddings`, `depends_on` should now be empty — delete the whole `depends_on:` block if nothing remains).
> Rationale identical to Slice 2: a profile-gated sidecar must not be a hard `depends_on` of `cortex-agent`, or a default `up` blocks. The Bridge lifecycle owns starting it; the agent's speaker embedder client connects lazily.

- [ ] **Step 8: Build + test + commit**

Run: `dotnet build src/Cortex.Contained.Bridge && dotnet test tests/Cortex.Contained.Bridge.Tests --filter "ClassName=Cortex.Contained.Bridge.Tests.Speech.VoiceIdSidecarLifecycleTests"`
Expected: build OK; 6 tests PASS.
```bash
git add src/Cortex.Contained.Bridge/Speech/ src/Cortex.Contained.Bridge/Program.cs docker-compose.yml tests/Cortex.Contained.Bridge.Tests/Speech/VoiceIdSidecarLifecycleTests.cs
git commit -m "feat(voiceid): voice-id sidecar lifecycle + voiceid compose profile (live start/stop)"
```

---

### Task 3: Bridge-side verification gate

**Files:**
- Modify: `src/Cortex.Contained.Bridge/Program.cs` (voice channel registration ~line 520–541, where `SpeakerVerifier = sp.GetService<ISpeakerVerifier>()`)
- Test: `tests/Cortex.Contained.Bridge.Tests/Speech/VoiceIdVerificationGateTests.cs`

**Interfaces:**
- Consumes: `SpeechToggles.EffectiveVoiceId`, `ISpeakerVerifier`.
- Produces: the registered `SpeakerVerifier` is `null` (verification skipped) whenever `EffectiveVoiceId(config.Speech)` is false.

**Approach:** The voice channel already treats `SpeakerVerifier` as optional (`sp.GetService<ISpeakerVerifier>()` is nullable; a null verifier means "no verification"). Gate the resolution on the effective flag so disabling voice-id makes the channel skip verification cleanly without touching channel internals.

- [ ] **Step 1: Write the failing test** — extract the gate decision into a tiny pure helper so it is unit-testable without standing up DI:

`src/Cortex.Contained.Bridge/Speech/VoiceIdVerifierSelector.cs`:
```csharp
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Speech.SpeakerId;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>Chooses the speaker verifier for the voice channel: the real verifier when
/// voice-id is effectively enabled, otherwise null (verification skipped).</summary>
public static class VoiceIdVerifierSelector
{
    public static ISpeakerVerifier? Select(ISpeakerVerifier? verifier, SpeechConfig speech)
        => SpeechToggles.EffectiveVoiceId(speech) ? verifier : null;
}
```
`tests/Cortex.Contained.Bridge.Tests/Speech/VoiceIdVerificationGateTests.cs`:
```csharp
using Cortex.Contained.Bridge.Speech;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Speech.SpeakerId;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class VoiceIdVerificationGateTests
{
    [Fact]
    public void Select_Enabled_ReturnsVerifier()
    {
        var verifier = Substitute.For<ISpeakerVerifier>();
        var speech = new SpeechConfig(); // voice-id on by default
        Assert.Same(verifier, VoiceIdVerifierSelector.Select(verifier, speech));
    }

    [Fact]
    public void Select_Disabled_ReturnsNull()
    {
        var verifier = Substitute.For<ISpeakerVerifier>();
        var speech = new SpeechConfig { VoiceId = new VoiceIdConfig { Enabled = false } };
        Assert.Null(VoiceIdVerifierSelector.Select(verifier, speech));
    }

    [Fact]
    public void Select_MasterOff_ReturnsNull()
    {
        var verifier = Substitute.For<ISpeakerVerifier>();
        var speech = new SpeechConfig { Enabled = false };
        Assert.Null(VoiceIdVerifierSelector.Select(verifier, speech));
    }
}
```
> Confirm the `ISpeakerVerifier` namespace via the facts (`Cortex.Contained.Speech.SpeakerId.ISpeakerVerifier`). Ensure the Bridge test project references the Speech project (it does transitively via Bridge; add a `using` only).

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Use the selector** in `Program.cs` voice channel registration. Replace:
```csharp
    SpeakerVerifier = sp.GetService<Cortex.Contained.Speech.SpeakerId.ISpeakerVerifier>(),
```
with:
```csharp
    SpeakerVerifier = Cortex.Contained.Bridge.Speech.VoiceIdVerifierSelector.Select(
        sp.GetService<Cortex.Contained.Speech.SpeakerId.ISpeakerVerifier>(),
        sp.GetRequiredService<BridgeConfig>().Speech),
```
> This is evaluated when the voice channel/options are built. It reflects the value of the flag at channel-construction time. For a *fully live* flip without restart, the voice channel would need to re-read the flag per verification; that is deferred (see self-review) — the sidecar stop already makes verification fail-safe immediately, and `restartRequired` is surfaced to the user when a live compose op cannot be confirmed.

- [ ] **Step 4: Run to verify pass; commit**

```bash
git add src/Cortex.Contained.Bridge/Speech/VoiceIdVerifierSelector.cs src/Cortex.Contained.Bridge/Program.cs tests/Cortex.Contained.Bridge.Tests/Speech/VoiceIdVerificationGateTests.cs
git commit -m "feat(voiceid): skip Bridge speaker verification when voice-id disabled"
```

---

### Task 4: Agent-side enrollment-tool gate via SignalR push

**Files:**
- Modify: `src/Cortex.Contained.Contracts/Hosting/HubTypes.cs` (`MemoryConfig`? No — add a dedicated push). Add a `SpeakerIdConfig` DTO **or** extend an existing speaker push. See Step 0.
- Modify: `src/Cortex.Contained.Contracts/Hosting/IAgentHub.cs` (+ `IAgentHubClient` if applicable) — add `UpdateSpeakerIdConfig` (or extend an existing speaker push)
- Modify: `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs` — handler
- Create: `src/Cortex.Contained.Agent.Host/SpeakerId/SpeakerIdSettingsStore.cs` (tiny volatile `bool Enabled` store)
- Create: `src/Cortex.Contained.Agent.Host/Tools/VoiceIdDisabledToolGate.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Program.cs` (register store + gate)
- Modify: `src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs` (push the flag on connect + on toggle)
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Tools/VoiceIdDisabledToolGateTests.cs`

**Step 0 — discovery (do FIRST):** grep the contracts + Bridge for an existing speaker/voice-id push channel: `grep -rin "SpeakerId" src/Cortex.Contained.Contracts src/Cortex.Contained.Bridge/Hosting`. The agent currently learns voice-id config from **environment variables** (`SpeakerId__*`), not SignalR. If NO speaker push exists, add the minimal one below. If one exists, extend it with `Enabled` instead of creating a new method.

**Interfaces:**
- Produces: `SpeakerIdSettingsStore` with `bool IsVoiceIdEnabled` (default true) + `void SetEnabled(bool)`; `IAgentHub.UpdateSpeakerIdConfig(SpeakerIdConfig config)`; `SpeakerIdConfig { bool Enabled = true }`; `VoiceIdDisabledToolGate` (registered `IConversationToolGate`) hiding the voice-enrollment tool family when disabled.

The voice-enrollment tool family (from `VoiceOnlyToolGate`): `start_voice_enrollment`, `decline_voice_enrollment`, `cancel_voice_enrollment`, `request_voice_reenrollment`, `confirm_voice_reenrollment`, `forget_voice_enrollment`. (The two delayed-speech tools `speak_after_delay`/`cancel_delayed_speech` are TTS-side, NOT voice-id — do not hide them here.)

- [ ] **Step 1: Write the failing test**

`tests/Cortex.Contained.Agent.Host.Tests/Tools/VoiceIdDisabledToolGateTests.cs`:
```csharp
using Cortex.Contained.Agent.Host.SpeakerId;
using Cortex.Contained.Agent.Host.Tools;

namespace Cortex.Contained.Agent.Host.Tests.Tools;

public sealed class VoiceIdDisabledToolGateTests
{
    [Fact]
    public void Enabled_HidesNothing()
    {
        var store = new SpeakerIdSettingsStore(); // default enabled
        Assert.Empty(new VoiceIdDisabledToolGate(store).GetHiddenTools("voice:abc"));
    }

    [Fact]
    public void Disabled_HidesEnrollmentTools()
    {
        var store = new SpeakerIdSettingsStore();
        store.SetEnabled(false);
        var hidden = new VoiceIdDisabledToolGate(store).GetHiddenTools("voice:abc");

        Assert.Contains("start_voice_enrollment", hidden);
        Assert.Contains("forget_voice_enrollment", hidden);
        Assert.DoesNotContain("speak_after_delay", hidden); // TTS tool, not voice-id
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Create the store** `src/Cortex.Contained.Agent.Host/SpeakerId/SpeakerIdSettingsStore.cs`:
```csharp
namespace Cortex.Contained.Agent.Host.SpeakerId;

/// <summary>Volatile runtime store for the pushed voice-id enable flag. Default enabled
/// (so existing deployments and tests behave identically until a disable is pushed).</summary>
public sealed class SpeakerIdSettingsStore
{
    private volatile bool isVoiceIdEnabled = true;

    /// <summary>Effective voice-id enablement as last pushed from the Bridge.</summary>
    public bool IsVoiceIdEnabled => this.isVoiceIdEnabled;

    /// <summary>Apply a pushed enable flag.</summary>
    public void SetEnabled(bool enabled) => this.isVoiceIdEnabled = enabled;
}
```

- [ ] **Step 4: Create the gate** `src/Cortex.Contained.Agent.Host/Tools/VoiceIdDisabledToolGate.cs` (mirror `MemoryDisabledToolGate`):
```csharp
using System.Collections.Frozen;
using Cortex.Contained.Agent.Host.SpeakerId;

namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>Hides the voice-enrollment tool family while voice-id is disabled
/// (<see cref="SpeakerIdSettingsStore.IsVoiceIdEnabled"/> is false).</summary>
public sealed class VoiceIdDisabledToolGate : IConversationToolGate
{
    private static readonly FrozenSet<string> enrollmentToolNames = FrozenSet.ToFrozenSet(
        [
            "start_voice_enrollment",
            "decline_voice_enrollment",
            "cancel_voice_enrollment",
            "request_voice_reenrollment",
            "confirm_voice_reenrollment",
            "forget_voice_enrollment",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly SpeakerIdSettingsStore store;

    public VoiceIdDisabledToolGate(SpeakerIdSettingsStore store)
    {
        this.store = store;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> GetHiddenTools(string? conversationId)
        => this.store.IsVoiceIdEnabled ? FrozenSet<string>.Empty : enrollmentToolNames;
}
```

- [ ] **Step 5: Run the gate test to pass.** Register the store + gate in `Program.cs` (next to `MemoryDisabledToolGate`/`VoiceOnlyToolGate`):
```csharp
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.SpeakerId.SpeakerIdSettingsStore>();
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Tools.IConversationToolGate, Cortex.Contained.Agent.Host.Tools.VoiceIdDisabledToolGate>();
```

- [ ] **Step 6: Add the SignalR push** (only if Step 0 found none). In `HubTypes.cs`:
```csharp
/// <summary>Pushed speaker-identification (voice-id) settings.</summary>
public sealed class SpeakerIdConfig
{
    /// <summary>Whether voice-id is enabled. Default true.</summary>
    public bool Enabled { get; set; } = true;
}
```
In `IAgentHub.cs` add `Task UpdateSpeakerIdConfig(SpeakerIdConfig config);`. In `AgentHub.cs` add the handler:
```csharp
    public Task UpdateSpeakerIdConfig(SpeakerIdConfig config)
    {
        this.speakerIdSettingsStore.SetEnabled(config.Enabled);
        return Task.CompletedTask;
    }
```
(inject `SpeakerIdSettingsStore` into `AgentHub` — match how it injects other stores). In `CredentialsPusher.cs`, push `new SpeakerIdConfig { Enabled = SpeechToggles.EffectiveVoiceId(this.config.Speech) }` both on initial connect (alongside the memory push) and from the toggle endpoint (Task 5). Match the existing `UpdateMemoryConfigAsync` push shape.

- [ ] **Step 7: Build agent host + bridge + test; commit**

Run: `dotnet build src/Cortex.Contained.Agent.Host && dotnet build src/Cortex.Contained.Bridge && dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=Cortex.Contained.Agent.Host.Tests.Tools.VoiceIdDisabledToolGateTests"`
Expected: builds OK; gate tests PASS.
```bash
git add src/Cortex.Contained.Contracts/Hosting/ src/Cortex.Contained.Agent.Host/SpeakerId/SpeakerIdSettingsStore.cs src/Cortex.Contained.Agent.Host/Tools/VoiceIdDisabledToolGate.cs src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs src/Cortex.Contained.Agent.Host/Program.cs src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs tests/Cortex.Contained.Agent.Host.Tests/Tools/VoiceIdDisabledToolGateTests.cs
git commit -m "feat(voiceid): push VoiceIdEnabled to agent + hide enrollment tools when disabled"
```

---

### Task 5: Extend the toggles endpoint + settings GET + `restartRequired`

**Files:**
- Modify: `src/Cortex.Contained.Bridge/Endpoints/SpeechToggleApply.cs` (add `voiceIdEnabled`)
- Modify: `src/Cortex.Contained.Bridge/SetupHelpers.cs` (`SpeechTogglesRequest` += `VoiceIdEnabled`)
- Modify: `src/Cortex.Contained.Bridge/Endpoints/SpeechEndpoints.cs` (`/api/speech/toggles`: reconcile voice-id, return `voiceIdEnabled` + `restartRequired`)
- Modify: `src/Cortex.Contained.Bridge/Endpoints/SettingsEndpoints.cs` (speech GET += `voiceId`)
- Test: `tests/Cortex.Contained.Bridge.Tests/Speech/SpeechToggleApplyVoiceIdTests.cs`

**Interfaces:**
- Produces: `SpeechToggleApply.Apply(SpeechConfig, bool?, bool?, bool?, bool? voiceIdEnabled)`; request gains `VoiceIdEnabled`; endpoint response gains `voiceIdEnabled` + `restartRequired` (bool).

- [ ] **Step 1: Write the failing test** — extend `SpeechToggleApply` coverage:

`tests/Cortex.Contained.Bridge.Tests/Speech/SpeechToggleApplyVoiceIdTests.cs`:
```csharp
using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class SpeechToggleApplyVoiceIdTests
{
    [Fact]
    public void Apply_SetsVoiceIdOnly()
    {
        var speech = new SpeechConfig();
        SpeechToggleApply.Apply(speech, speechEnabled: null, sttEnabled: null, ttsEnabled: null, voiceIdEnabled: false);
        Assert.True(speech.Enabled);
        Assert.True(speech.Stt.Enabled);
        Assert.True(speech.Tts.Enabled);
        Assert.False(speech.VoiceId.Enabled);
    }

    [Fact]
    public void Apply_NullVoiceId_LeavesUnchanged()
    {
        var speech = new SpeechConfig { VoiceId = new VoiceIdConfig { Enabled = false } };
        SpeechToggleApply.Apply(speech, null, null, null, voiceIdEnabled: null);
        Assert.False(speech.VoiceId.Enabled);
    }
}
```

- [ ] **Step 2: Run to verify failure** (Apply has 4 params today).

- [ ] **Step 3: Extend `SpeechToggleApply.Apply`** — add the parameter + block:
```csharp
    public static void Apply(SpeechConfig speech, bool? speechEnabled, bool? sttEnabled, bool? ttsEnabled, bool? voiceIdEnabled)
    {
        if (speechEnabled.HasValue) { speech.Enabled = speechEnabled.Value; }
        if (sttEnabled.HasValue) { speech.Stt.Enabled = sttEnabled.Value; }
        if (ttsEnabled.HasValue) { speech.Tts.Enabled = ttsEnabled.Value; }
        if (voiceIdEnabled.HasValue) { speech.VoiceId.Enabled = voiceIdEnabled.Value; }
    }
```
(Keep braces on their own lines per house style — the inline form above is shorthand; expand each `if` to multi-line with braces when editing.)

- [ ] **Step 4: Run to verify pass.** Then extend `SpeechTogglesRequest` in `SetupHelpers.cs`:
```csharp
    [JsonPropertyName("voiceIdEnabled")]
    public bool? VoiceIdEnabled { get; set; }
```

- [ ] **Step 5: Wire the endpoint** in `SpeechEndpoints.cs` `/api/speech/toggles`. Update the `Apply` call to pass `body.VoiceIdEnabled`; add a voice-id reconcile that captures the confirm bool for `restartRequired`; push the voice-id flag to the agent; extend the response:
```csharp
            SpeechToggleApply.Apply(config.Speech, body.SpeechEnabled, body.SttEnabled, body.TtsEnabled, body.VoiceIdEnabled);
            BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath);

            var stt = sp.GetRequiredService<Cortex.Contained.Bridge.Speech.SttSidecarLifecycle>();
            var tts = sp.GetRequiredService<Cortex.Contained.Bridge.Speech.DanishTtsLifecycle>();
            var voiceId = sp.GetRequiredService<Cortex.Contained.Bridge.Speech.VoiceIdSidecarLifecycle>();
            _ = Task.Run(() => stt.ReconcileAsync(SpeechToggles.EffectiveStt(config.Speech), CancellationToken.None));
            _ = Task.Run(() => tts.ReconcileAsync(SpeechToggles.EffectiveTts(config.Speech), CancellationToken.None));

            // Voice-id is reconciled inline (short timeout) so we can tell the UI whether a
            // live flip succeeded or a restart is needed.
            var voiceIdEffective = SpeechToggles.EffectiveVoiceId(config.Speech);
            var voiceIdConfirmed = await voiceId.TryReconcileNowAsync(voiceIdEffective, CancellationToken.None);

            // Push voice-id flag to the agent so enrollment tools hide live.
            var client = tenantRouter.GetDefaultClient();
            if (client is { IsConnected: true })
            {
                try { await client.UpdateSpeakerIdConfig(new SpeakerIdConfig { Enabled = voiceIdEffective }, CancellationToken.None); }
                catch { /* best-effort; re-synced on next connect */ }
            }

            return Results.Ok(new
            {
                success = true,
                speechEnabled = config.Speech.Enabled,
                sttEnabled = SpeechToggles.EffectiveStt(config.Speech),
                ttsEnabled = SpeechToggles.EffectiveTts(config.Speech),
                voiceIdEnabled = voiceIdEffective,
                restartRequired = !voiceIdConfirmed,
            });
```
> Match the real way the endpoint accesses `tenantRouter`/the hub client method name and signature (grep `UpdateMemoryConfigAsync` usages). If the client method is `UpdateSpeakerIdConfigAsync(... , CancellationToken)` adjust accordingly. Keep STT/TTS exactly as today.

- [ ] **Step 6: Settings GET** — in `SettingsEndpoints.cs`, add to the `speech` projection:
```csharp
                    voiceId = new { config.Speech.VoiceId.Enabled },
```

- [ ] **Step 7: Build + test + commit**

Run: `dotnet build src/Cortex.Contained.Bridge && dotnet test tests/Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~Speech"`
Expected: build OK; all speech tests PASS (incl. the existing `SpeechToggleApplyTests` — update its `Apply(...)` calls to pass the new 5th arg `voiceIdEnabled: null`).
```bash
git add src/Cortex.Contained.Bridge/Endpoints/ src/Cortex.Contained.Bridge/SetupHelpers.cs tests/Cortex.Contained.Bridge.Tests/Speech/
git commit -m "feat(voiceid): /api/speech/toggles voice-id field + restartRequired"
```

> **Compile-barrier note:** changing `Apply`'s arity breaks the existing `SpeechToggleApplyTests` and the endpoint's existing call. Fix both in this task (the endpoint call in Step 5; the test calls in Step 7) so the Bridge compiles green before committing.

---

### Task 6: Web UI voice-id sub-toggle + restart-required badge

**Files:**
- Modify: `src/Cortex.Contained.Bridge/wwwroot/js/pages/global-settings.js`
- Modify: `src/Cortex.Contained.Bridge/wwwroot/app.html` (speech toggles block ~line 1370)

**Interfaces:**
- Consumes: `GET /api/settings` → `speech.voiceId.enabled`; `POST /api/speech/toggles` (now returns `voiceIdEnabled` + `restartRequired`).

- [ ] **Step 1: Extend state + apply + save** in `global-settings.js`:

Add to state (next to `ttsEnabled`):
```javascript
voiceIdEnabled: true,
voiceRestartRequired: false,
```
Extend `_applySpeechToggles`:
```javascript
_applySpeechToggles(speech) {
    this.speechEnabled = !!speech.enabled;
    this.sttEnabled = !!speech.stt?.enabled;
    this.ttsEnabled = !!speech.tts?.enabled;
    this.voiceIdEnabled = !!speech.voiceId?.enabled;
},
```
Extend the `saveSpeechToggles` payload + response handling:
```javascript
        const payload = {
            speechEnabled: this.speechEnabled,
            sttEnabled: this.sttEnabled,
            ttsEnabled: this.ttsEnabled,
            voiceIdEnabled: this.voiceIdEnabled,
        };
        const data = await api.post("/api/speech/toggles", payload);
        if (data?.success) {
            this._applySpeechToggles({
                enabled: data.speechEnabled,
                stt: { enabled: data.sttEnabled },
                tts: { enabled: data.ttsEnabled },
                voiceId: { enabled: data.voiceIdEnabled },
            });
            this.voiceRestartRequired = !!data.restartRequired;
            Alpine.store("toast").success(
                data.restartRequired
                    ? "Voice settings saved — restart required to fully apply voice-id"
                    : "Voice settings saved");
        }
```

- [ ] **Step 2: Add markup** in `app.html` after the `tts-enabled` checkbox (same indented sub-toggle style), plus a conditional badge:
```html
<div class="form-check ms-4">
    <input class="form-check-input" type="checkbox" id="voice-id-enabled"
           :checked="voiceIdEnabled"
           :disabled="savingSpeechToggles || !speechEnabled"
           @change="voiceIdEnabled = $event.target.checked; saveSpeechToggles()">
    <label class="form-check-label" for="voice-id-enabled">Speaker ID (voice-id)</label>
    <span class="badge bg-warning text-dark ms-2" x-show="voiceRestartRequired">restart required</span>
</div>
```

- [ ] **Step 3: Manual verification**

Run the Bridge (`.\scripts\Start-Cortex.ps1 -BridgeOnly`), Settings → Voice:
- Toggle **voice-id off** → `docker ps` shows `cortex-voice-id` stopping; if compose can't confirm, the "restart required" badge shows.
- Toggle **voice-id on** → container starts; badge clears.
- Reload → state persists.

- [ ] **Step 4: Commit**

```bash
git add src/Cortex.Contained.Bridge/wwwroot/
git commit -m "feat(voiceid): web UI voice-id sub-toggle + restart-required badge"
```

---

## Slice 3 self-review (run before handoff)

- **Spec coverage:** voice-id sub-toggle ✅ (Task 1/5/6), live sidecar start/stop ✅ (Task 2), compose profile + depends_on relaxed ✅ (Task 2), Bridge verification gated ✅ (Task 3), agent enrollment tools hidden via SignalR push ✅ (Task 4), `restartRequired` fallback badge ✅ (Task 2/5/6), defaults-true ✅ (Task 1).
- **Live vs restart:** sidecar start/stop + agent enrollment-tool gate are live (pushed). The Bridge verifier selection is evaluated at channel-construction time (Task 3) — a flip while a voice call is mid-session may need a restart to re-pick the verifier; this is exactly what the `restartRequired` badge communicates, and stopping the sidecar makes verification fail-safe immediately regardless. Fully-live per-verification re-read is intentionally deferred as not worth the channel-internals churn.
- **Master interaction:** turning the master speech switch off already forces `EffectiveVoiceId` false (so the sidecar stops) via Task 1's `&&`; the UI disables the sub-toggle when master is off (existing `:disabled="!speechEnabled"` pattern, applied to `voice-id-enabled` in Task 6).

---

## Done-criteria for the whole 3-slice feature

After Slices 1–3: master speech + STT + TTS + voice-id + built-in-memory are all independent, default-on, live web-UI toggles that start/stop their Docker sidecars and gate in-process usage. Rebuild the agent image + force-upgrade, then ship to `main`.
