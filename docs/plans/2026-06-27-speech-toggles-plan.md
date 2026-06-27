# Speech Enable/Disable Toggles — Implementation Plan (Slice 1 of 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a master "speech" enable switch plus independent TTS and STT toggles that start/stop the matching Docker sidecars live, defaulting to current behavior.

**Architecture:** Bridge-only. New `Enabled` flags on `SpeechConfig`/`SttConfig`/`TtsConfig` (default `true`). A static `SpeechToggles` helper computes `effective = master && sub`. The two existing sidecar lifecycles (`SttSidecarLifecycle`, `DanishTtsLifecycle`) take an `enabled` bool and reconcile (start-if-down when enabled, stop-if-up when disabled). A new `POST /api/speech/toggles` persists flags to `cortex.yml` and fires both reconciles; the web UI Settings page renders the toggles.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, xUnit + NSubstitute, vanilla JS (`wwwroot/js/pages/global-settings.js`), `docker compose` CLI shell-out.

**Parent spec:** `docs/specs/2026-06-27-enable-disable-toggles-voice-memory-design.md`. This slice covers the **TTS / STT / master** rows only. Memory (Slice 2) and Voice-id (Slice 3) are separate plans — see "Roadmap" at the end.

## Global Constraints

- .NET 10; `TreatWarningsAsErrors` is on — code must be warning-clean.
- C# style: `this.`-prefixed instance access, braces on all control blocks, one type per file, file-scoped namespaces, `ConfigureAwait(false)` in Bridge service/lifecycle code.
- All new `Enabled` flags default to `true` — existing `cortex.yml` files and tests must behave identically.
- Test method naming: `Method_Condition_Expected` (CA1707 suppressed in test projects).
- Persistence is via `BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath)`.
- Reconcile dispatch is fire-and-forget (`_ = Task.Run(() => lifecycle.ReconcileAsync(...))`) so a slow `docker compose` call never blocks an HTTP save or boot.

---

### Task 1: Config flags + effective-state helper

**Files:**
- Modify: `src/Cortex.Contained.Contracts/Config/BridgeConfig.cs` (the `SpeechConfig`, `SttConfig`, `TtsConfig` classes)
- Create: `src/Cortex.Contained.Contracts/Config/SpeechToggles.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/Speech/SpeechTogglesTests.cs`

**Interfaces:**
- Produces: `SpeechConfig.Enabled`, `SttConfig.Enabled`, `TtsConfig.Enabled` (all `bool`, default `true`); `static bool SpeechToggles.EffectiveStt(SpeechConfig)`, `static bool SpeechToggles.EffectiveTts(SpeechConfig)`.

- [ ] **Step 1: Write the failing test**

`tests/Cortex.Contained.Bridge.Tests/Speech/SpeechTogglesTests.cs`:
```csharp
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class SpeechTogglesTests
{
    [Theory]
    [InlineData(true, true, true)]    // master on, sub on  -> effective on
    [InlineData(true, false, false)]  // master on, sub off -> effective off
    [InlineData(false, true, false)]  // master off         -> effective off
    [InlineData(false, false, false)]
    public void EffectiveStt_IsMasterAndSub(bool master, bool sub, bool expected)
    {
        var speech = new SpeechConfig { Enabled = master, Stt = new SttConfig { Enabled = sub } };
        Assert.Equal(expected, SpeechToggles.EffectiveStt(speech));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void EffectiveTts_IsMasterAndSub(bool master, bool sub, bool expected)
    {
        var speech = new SpeechConfig { Enabled = master, Tts = new TtsConfig { Enabled = sub } };
        Assert.Equal(expected, SpeechToggles.EffectiveTts(speech));
    }

    [Fact]
    public void Defaults_AreEnabled()
    {
        var speech = new SpeechConfig();
        Assert.True(SpeechToggles.EffectiveStt(speech));
        Assert.True(SpeechToggles.EffectiveTts(speech));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "ClassName=Cortex.Contained.Bridge.Tests.Speech.SpeechTogglesTests"`
Expected: FAIL to compile — `SpeechToggles` and the `Enabled` members don't exist.

- [ ] **Step 3: Add the `Enabled` flags**

In `BridgeConfig.cs`, add to `SpeechConfig` (before `Stt`):
```csharp
/// <summary>Master voice switch. When false, STT, TTS, and voice-id are all off.</summary>
public bool Enabled { get; set; } = true;
```
Add to `SttConfig` (first member):
```csharp
/// <summary>Whether speech-to-text is enabled (gated by <see cref="SpeechConfig.Enabled"/>).</summary>
public bool Enabled { get; set; } = true;
```
Add to `TtsConfig` (first member):
```csharp
/// <summary>Whether text-to-speech is enabled (gated by <see cref="SpeechConfig.Enabled"/>).</summary>
public bool Enabled { get; set; } = true;
```

- [ ] **Step 4: Create the helper**

`src/Cortex.Contained.Contracts/Config/SpeechToggles.cs`:
```csharp
namespace Cortex.Contained.Contracts.Config;

/// <summary>
/// Computes effective speech-subsystem enablement: a subsystem is on only when the
/// master <see cref="SpeechConfig.Enabled"/> switch AND its own flag are both true.
/// Kept as a static helper (not computed properties) so the booleans never leak into
/// serialized YAML/JSON config.
/// </summary>
public static class SpeechToggles
{
    /// <summary>True when STT should run: master AND stt flag.</summary>
    public static bool EffectiveStt(SpeechConfig speech) => speech.Enabled && speech.Stt.Enabled;

    /// <summary>True when TTS should run: master AND tts flag.</summary>
    public static bool EffectiveTts(SpeechConfig speech) => speech.Enabled && speech.Tts.Enabled;
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "ClassName=Cortex.Contained.Bridge.Tests.Speech.SpeechTogglesTests"`
Expected: PASS (7 cases).

- [ ] **Step 6: Commit**

```bash
git add src/Cortex.Contained.Contracts/Config/BridgeConfig.cs src/Cortex.Contained.Contracts/Config/SpeechToggles.cs tests/Cortex.Contained.Bridge.Tests/Speech/SpeechTogglesTests.cs
git commit -m "feat(speech): add master+STT+TTS enable flags and effective-state helper"
```

---

### Task 2: STT lifecycle honors `enabled` (adds stop path)

**Files:**
- Modify: `src/Cortex.Contained.Bridge/Speech/ISttComposeRunner.cs`
- Modify: `src/Cortex.Contained.Bridge/Speech/DockerComposeCommandRunner.cs`
- Modify: `src/Cortex.Contained.Bridge/Speech/SttSidecarLifecycle.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/Speech/SttSidecarLifecycleTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `ISttComposeRunner.StopSttAsync(CancellationToken)`; `SttSidecarLifecycle.ReconcileAsync(bool enabled, CancellationToken)` (signature change — the old parameterless overload is removed).

- [ ] **Step 1: Write the failing tests**

Replace the body of `SttSidecarLifecycleTests.cs` with:
```csharp
using Cortex.Contained.Bridge.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class SttSidecarLifecycleTests
{
    private static SttSidecarLifecycle Sut(ISttComposeRunner runner) =>
        new(runner, NullLogger<SttSidecarLifecycle>.Instance);

    [Fact]
    public async Task Enabled_NotRunning_StartsContainer()
    {
        var runner = Substitute.For<ISttComposeRunner>();
        runner.IsSttRunningAsync(Arg.Any<CancellationToken>()).Returns(false);

        await Sut(runner).ReconcileAsync(enabled: true, CancellationToken.None);

        await runner.Received(1).StartSttAsync(Arg.Any<CancellationToken>());
        await runner.DidNotReceive().StopSttAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enabled_AlreadyRunning_NoOp()
    {
        var runner = Substitute.For<ISttComposeRunner>();
        runner.IsSttRunningAsync(Arg.Any<CancellationToken>()).Returns(true);

        await Sut(runner).ReconcileAsync(enabled: true, CancellationToken.None);

        await runner.DidNotReceive().StartSttAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_Running_StopsContainer()
    {
        var runner = Substitute.For<ISttComposeRunner>();
        runner.IsSttRunningAsync(Arg.Any<CancellationToken>()).Returns(true);

        await Sut(runner).ReconcileAsync(enabled: false, CancellationToken.None);

        await runner.Received(1).StopSttAsync(Arg.Any<CancellationToken>());
        await runner.DidNotReceive().StartSttAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_NotRunning_NoOp()
    {
        var runner = Substitute.For<ISttComposeRunner>();
        runner.IsSttRunningAsync(Arg.Any<CancellationToken>()).Returns(false);

        await Sut(runner).ReconcileAsync(enabled: false, CancellationToken.None);

        await runner.DidNotReceive().StopSttAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_RunnerThrows_DoesNotPropagate()
    {
        var runner = Substitute.For<ISttComposeRunner>();
        runner.IsSttRunningAsync(Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("docker exploded"));

        await Sut(runner).ReconcileAsync(enabled: true, CancellationToken.None);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "ClassName=Cortex.Contained.Bridge.Tests.Speech.SttSidecarLifecycleTests"`
Expected: FAIL to compile — `StopSttAsync` and the `bool enabled` overload don't exist.

- [ ] **Step 3: Add `StopSttAsync` to the seam**

In `ISttComposeRunner.cs`, add:
```csharp
    /// <summary>`docker compose --profile voice stop stt`. Returns true on exit 0.</summary>
    Task<bool> StopSttAsync(CancellationToken cancellationToken);
```

In `DockerComposeCommandRunner.cs`, add after `StartSttAsync`:
```csharp
    /// <inheritdoc />
    public Task<bool> StopSttAsync(CancellationToken cancellationToken)
        => this.RunCommandAsync(
            $"compose -f \"{this.composeFilePath}\" --profile voice stop stt",
            ShortTimeout,
            cancellationToken);
```

- [ ] **Step 4: Make the lifecycle honor `enabled`**

Replace `SttSidecarLifecycle.ReconcileAsync` and its summary:
```csharp
    /// <summary>
    /// Converges the cortex-stt sidecar with the desired run-state: when
    /// <paramref name="enabled"/> is true, start it if down; when false, stop it
    /// if up. Failures are logged, never propagated.
    /// </summary>
    public async Task ReconcileAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            var running = await this.runner.IsSttRunningAsync(cancellationToken).ConfigureAwait(false);
            if (enabled && !running)
            {
                this.LogStarting();
                await this.runner.StartSttAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (!enabled && running)
            {
                this.LogStopping();
                await this.runner.StopSttAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            this.LogReconcileFailed(ex.Message);
        }
    }
```
Add the new log message next to `LogStarting`:
```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "whisper-stt disabled — stopping cortex-stt")]
    private partial void LogStopping();
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "ClassName=Cortex.Contained.Bridge.Tests.Speech.SttSidecarLifecycleTests"`
Expected: PASS (5 cases). NOTE: this breaks the build at the two call sites in `Program.cs:899` — fixed in Task 4. Build the test project only here: `dotnet build tests/Cortex.Contained.Bridge.Tests` will fail on the Bridge project until Task 4. Run the filtered test after Task 4 if compilation blocks; otherwise proceed to Task 4 before the green run.

> Sequencing note: Tasks 2–4 share the lifecycle signature change, so the Bridge project only compiles again after Task 4. Implement Tasks 2, 3, 4 together, then run the green tests and commit at the end of Task 4. (Commit for Task 2/3 is deferred into Task 4's commit.)

---

### Task 3: TTS lifecycle honors `enabled`

**Files:**
- Modify: `src/Cortex.Contained.Bridge/Speech/DanishTtsLifecycle.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/Speech/DanishTtsLifecycleTests.cs`

**Interfaces:**
- Consumes: existing `IComposeCommandRunner` (`StartDanishAsync`, `StopDanishAsync`, `IsDanishRunningAsync`).
- Produces: `DanishTtsLifecycle.ReconcileAsync(bool enabled, CancellationToken)` (replaces the `TtsConfig`-taking overload).

- [ ] **Step 1: Write the failing tests**

Replace `DanishTtsLifecycleTests.cs` body with the mirror of Task 2 (start when enabled+down, stop when disabled+up, no-ops otherwise, throw-is-swallowed), using `IComposeCommandRunner` + `IsDanishRunningAsync`/`StartDanishAsync`/`StopDanishAsync`:
```csharp
using Cortex.Contained.Bridge.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class DanishTtsLifecycleTests
{
    private static DanishTtsLifecycle Sut(IComposeCommandRunner runner) =>
        new(runner, NullLogger<DanishTtsLifecycle>.Instance);

    [Fact]
    public async Task Enabled_NotRunning_Starts()
    {
        var runner = Substitute.For<IComposeCommandRunner>();
        runner.IsDanishRunningAsync(Arg.Any<CancellationToken>()).Returns(false);

        await Sut(runner).ReconcileAsync(enabled: true, CancellationToken.None);

        await runner.Received(1).StartDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_Running_Stops()
    {
        var runner = Substitute.For<IComposeCommandRunner>();
        runner.IsDanishRunningAsync(Arg.Any<CancellationToken>()).Returns(true);

        await Sut(runner).ReconcileAsync(enabled: false, CancellationToken.None);

        await runner.Received(1).StopDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_NotRunning_NoOp()
    {
        var runner = Substitute.For<IComposeCommandRunner>();
        runner.IsDanishRunningAsync(Arg.Any<CancellationToken>()).Returns(false);

        await Sut(runner).ReconcileAsync(enabled: false, CancellationToken.None);

        await runner.DidNotReceive().StopDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_RunnerThrows_DoesNotPropagate()
    {
        var runner = Substitute.For<IComposeCommandRunner>();
        runner.IsDanishRunningAsync(Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("boom"));

        await Sut(runner).ReconcileAsync(enabled: true, CancellationToken.None);
    }
}
```
(If existing tests in this file already cover start-when-down, replace them rather than duplicate.)

- [ ] **Step 2: Run to verify failure** — same command form as Task 2 for `DanishTtsLifecycleTests`. Expected: FAIL to compile (no `bool` overload).

- [ ] **Step 3: Update the lifecycle**

Replace `DanishTtsLifecycle.ReconcileAsync`:
```csharp
    /// <summary>
    /// Converges the uni-voices sidecar with the desired run-state: start-if-down when
    /// <paramref name="enabled"/>, stop-if-up otherwise. Failures are logged, not propagated.
    /// </summary>
    public async Task ReconcileAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            var running = await this.runner.IsDanishRunningAsync(cancellationToken).ConfigureAwait(false);
            if (enabled && !running)
            {
                this.LogStarting();
                await this.runner.StartDanishAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (!enabled && running)
            {
                this.LogStopping();
                await this.runner.StopDanishAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            this.LogReconcileFailed(ex.Message);
        }
    }
```
Add the stop log:
```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "uni-voices TTS disabled — stopping uni-voices")]
    private partial void LogStopping();
```

- [ ] **Step 4: Defer green run + commit to Task 4** (shared compile barrier).

---

### Task 4: Wire callers to pass effective-enabled

**Files:**
- Modify: `src/Cortex.Contained.Bridge/Program.cs:887-900`
- Modify: `src/Cortex.Contained.Bridge/Endpoints/SpeechEndpoints.cs:367-368` (and any other `ReconcileAsync(config.Speech.Tts, ...)` call sites — grep `ReconcileAsync(`)

**Interfaces:**
- Consumes: `SpeechToggles.EffectiveStt/EffectiveTts`, the new `ReconcileAsync(bool, ct)` overloads.

- [ ] **Step 1: Update the startup reconciles** in `Program.cs` (the block at lines ~887–900):
```csharp
// --- TTS sidecar lifecycle: converge with config at startup (fire-and-forget) ---
{
    var danishLifecycle = app.Services.GetRequiredService<Cortex.Contained.Bridge.Speech.DanishTtsLifecycle>();
    var speech = app.Services.GetRequiredService<BridgeConfig>().Speech;
    _ = Task.Run(() => danishLifecycle.ReconcileAsync(SpeechToggles.EffectiveTts(speech), CancellationToken.None));
}

// --- STT sidecar lifecycle: converge with config at startup (fire-and-forget) ---
{
    var sttLifecycle = app.Services.GetRequiredService<Cortex.Contained.Bridge.Speech.SttSidecarLifecycle>();
    var speech = app.Services.GetRequiredService<BridgeConfig>().Speech;
    _ = Task.Run(() => sttLifecycle.ReconcileAsync(SpeechToggles.EffectiveStt(speech), CancellationToken.None));
}
```
Ensure `using Cortex.Contained.Contracts.Config;` is present (it is — `BridgeConfig` is already used).

- [ ] **Step 2: Update the save-path reconcile** in `SpeechEndpoints.cs:368`:
```csharp
            var danishLifecycle = sp.GetRequiredService<Cortex.Contained.Bridge.Speech.DanishTtsLifecycle>();
            _ = Task.Run(() => danishLifecycle.ReconcileAsync(SpeechToggles.EffectiveTts(config.Speech), CancellationToken.None));
```
Repeat for any other `danishLifecycle.ReconcileAsync(config.Speech.Tts, ...)` occurrence found by grep.

- [ ] **Step 3: Build the whole Bridge + tests**

Run: `dotnet build src/Cortex.Contained.Bridge && dotnet test tests/Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~Speech"`
Expected: build succeeds; STT + TTS lifecycle + toggles tests all PASS.

- [ ] **Step 4: Commit Tasks 2–4 together**

```bash
git add src/Cortex.Contained.Bridge/Speech/ src/Cortex.Contained.Bridge/Program.cs src/Cortex.Contained.Bridge/Endpoints/SpeechEndpoints.cs tests/Cortex.Contained.Bridge.Tests/Speech/
git commit -m "feat(speech): sidecar lifecycles honor master+STT+TTS enable flags (live start/stop)"
```

---

### Task 5: Toggle persistence endpoint + expose flags in settings GET

**Files:**
- Modify: `src/Cortex.Contained.Bridge/Endpoints/SettingsEndpoints.cs:50-54` (add `enabled` to the speech GET payload)
- Modify: `src/Cortex.Contained.Bridge/Endpoints/SpeechEndpoints.cs` (add `POST /api/speech/toggles`)
- Test: `tests/Cortex.Contained.Bridge.Tests/Speech/SpeechToggleApplyTests.cs`

**Interfaces:**
- Produces: `POST /api/speech/toggles` accepting `{ speechEnabled?: bool, sttEnabled?: bool, ttsEnabled?: bool }`, returning `{ success, sttRunning?, restartRequired:false }`. A testable static helper `SpeechToggleApply.Apply(SpeechConfig, bool?, bool?, bool?)` that mutates the config.

- [ ] **Step 1: Write the failing test**

`tests/Cortex.Contained.Bridge.Tests/Speech/SpeechToggleApplyTests.cs`:
```csharp
using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class SpeechToggleApplyTests
{
    [Fact]
    public void Apply_SetsOnlyProvidedFlags()
    {
        var speech = new SpeechConfig(); // all true by default
        SpeechToggleApply.Apply(speech, speechEnabled: false, sttEnabled: null, ttsEnabled: null);

        Assert.False(speech.Enabled);
        Assert.True(speech.Stt.Enabled);   // unchanged (null)
        Assert.True(speech.Tts.Enabled);   // unchanged (null)
    }

    [Fact]
    public void Apply_UpdatesSubFlags()
    {
        var speech = new SpeechConfig();
        SpeechToggleApply.Apply(speech, speechEnabled: null, sttEnabled: false, ttsEnabled: true);

        Assert.True(speech.Enabled);
        Assert.False(speech.Stt.Enabled);
        Assert.True(speech.Tts.Enabled);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "ClassName=Cortex.Contained.Bridge.Tests.Speech.SpeechToggleApplyTests"`. Expected: FAIL to compile.

- [ ] **Step 3: Add the apply helper**

`src/Cortex.Contained.Bridge/Endpoints/SpeechToggleApply.cs`:
```csharp
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>Applies optional speech-toggle updates to a <see cref="SpeechConfig"/>. Null = leave as-is.</summary>
public static class SpeechToggleApply
{
    public static void Apply(SpeechConfig speech, bool? speechEnabled, bool? sttEnabled, bool? ttsEnabled)
    {
        if (speechEnabled.HasValue)
        {
            speech.Enabled = speechEnabled.Value;
        }

        if (sttEnabled.HasValue)
        {
            speech.Stt.Enabled = sttEnabled.Value;
        }

        if (ttsEnabled.HasValue)
        {
            speech.Tts.Enabled = ttsEnabled.Value;
        }
    }
}
```

- [ ] **Step 4: Run to verify pass** — same filter. Expected: PASS.

- [ ] **Step 5: Add the endpoint + GET fields**

In `SpeechEndpoints.cs`, add a new endpoint (mirroring the persist + fire-and-forget reconcile pattern at lines 303/357/367):
```csharp
        // Master + STT + TTS enable toggles. Persists to YAML and converges both sidecars live.
        app.MapPost("/api/speech/toggles", async (HttpContext ctx, BridgeConfig config, IServiceProvider sp) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<SpeechTogglesRequest>();
            if (body is null)
            {
                return Results.Json(new { error = "body is required" }, statusCode: 400);
            }

            SpeechToggleApply.Apply(config.Speech, body.SpeechEnabled, body.SttEnabled, body.TtsEnabled);
            BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath);

            var stt = sp.GetRequiredService<Cortex.Contained.Bridge.Speech.SttSidecarLifecycle>();
            var tts = sp.GetRequiredService<Cortex.Contained.Bridge.Speech.DanishTtsLifecycle>();
            _ = Task.Run(() => stt.ReconcileAsync(SpeechToggles.EffectiveStt(config.Speech), CancellationToken.None));
            _ = Task.Run(() => tts.ReconcileAsync(SpeechToggles.EffectiveTts(config.Speech), CancellationToken.None));

            return Results.Ok(new
            {
                success = true,
                speechEnabled = config.Speech.Enabled,
                sttEnabled = SpeechToggles.EffectiveStt(config.Speech),
                ttsEnabled = SpeechToggles.EffectiveTts(config.Speech),
            });
        }).RequireAuthorization();
```
Add the request record near the file's other request types:
```csharp
    private sealed record SpeechTogglesRequest(bool? SpeechEnabled, bool? SttEnabled, bool? TtsEnabled);
```
In `SettingsEndpoints.cs`, extend the `speech` GET object (lines 50-54):
```csharp
                speech = new
                {
                    enabled = config.Speech.Enabled,
                    stt = new { config.Speech.Stt.Enabled, config.Speech.Stt.Engine, config.Speech.Stt.WhisperModelPath, config.Speech.Stt.Language },
                    tts = new { config.Speech.Tts.Enabled, config.Speech.Tts.Engine, config.Speech.Tts.KokoroVoice, config.Speech.Tts.KokoroModelPath, config.Speech.Tts.WindowsVoiceName, config.Speech.Tts.WindowsSpeechRate },
                },
```

- [ ] **Step 6: Build + test + commit**

Run: `dotnet build src/Cortex.Contained.Bridge && dotnet test tests/Cortex.Contained.Bridge.Tests --filter "ClassName=Cortex.Contained.Bridge.Tests.Speech.SpeechToggleApplyTests"`
Expected: build OK, tests PASS.
```bash
git add src/Cortex.Contained.Bridge/Endpoints/ tests/Cortex.Contained.Bridge.Tests/Speech/SpeechToggleApplyTests.cs
git commit -m "feat(speech): /api/speech/toggles endpoint + expose enable flags in settings"
```

---

### Task 6: Web UI toggles

**Files:**
- Modify: `src/Cortex.Contained.Bridge/wwwroot/js/pages/global-settings.js`
- Modify (if markup is static): `src/Cortex.Contained.Bridge/wwwroot/app.html` (add a "Features" section container near the existing speech/memory settings)

**Interfaces:**
- Consumes: `GET /api/settings` → `speech.enabled`, `speech.stt.enabled`, `speech.tts.enabled`; `POST /api/speech/toggles`.

- [ ] **Step 1: Render the toggles**

In `global-settings.js`, where the page renders the speech section, add a "Voice" group with a master checkbox and two sub-checkboxes bound to the settings payload. Concrete handler (adapt selectors to the page's existing render style):
```javascript
function renderSpeechToggles(speech) {
  const master = document.getElementById('speech-enabled');
  const stt = document.getElementById('stt-enabled');
  const tts = document.getElementById('tts-enabled');
  master.checked = speech.enabled;
  stt.checked = speech.stt.enabled;
  tts.checked = speech.tts.enabled;
  // sub-toggles are subordinate to the master
  stt.disabled = !speech.enabled;
  tts.disabled = !speech.enabled;
}

async function saveSpeechToggles() {
  const payload = {
    speechEnabled: document.getElementById('speech-enabled').checked,
    sttEnabled: document.getElementById('stt-enabled').checked,
    ttsEnabled: document.getElementById('tts-enabled').checked,
  };
  const res = await fetch('/api/speech/toggles', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  });
  const data = await res.json();
  if (data.success) { renderSpeechToggles({ enabled: data.speechEnabled, stt: { enabled: data.sttEnabled }, tts: { enabled: data.ttsEnabled } }); }
}
```
Wire `change` listeners on the three checkboxes to `saveSpeechToggles`, and call `renderSpeechToggles(settings.speech)` after the `/api/settings` fetch.

- [ ] **Step 2: Add markup** (if `app.html` holds static settings markup): a Features card with three labeled checkboxes (`speech-enabled`, `stt-enabled`, `tts-enabled`), the two sub-toggles visually indented under the master.

- [ ] **Step 3: Manual verification** (no JS test harness in this repo)

Run the Bridge (`.\scripts\Start-Cortex.ps1 -BridgeOnly`), open `http://127.0.0.1:5080`, go to Settings:
- Toggle **Voice master off** → both sub-toggles disable; `docker ps` shows `cortex-stt` and `cortex-uni-voices` stopping.
- Toggle **Voice on, TTS off** → `cortex-uni-voices` stops, `cortex-stt` starts.
- Reload the page → toggle states persist (written to `cortex.yml`).

- [ ] **Step 4: Commit**

```bash
git add src/Cortex.Contained.Bridge/wwwroot/
git commit -m "feat(speech): web UI toggles for master voice + STT + TTS"
```

---

## Slice 1 self-review (run before handoff)

- **Spec coverage:** master switch ✅ (Task 1/4/5), STT toggle ✅, TTS toggle ✅, live start/stop ✅ (Tasks 2–4), persistence ✅ (Task 5), UI ✅ (Task 6). Defaults-true ✅ (Task 1). Voice-id + memory are out of this slice by design.
- **Restart-fallback:** STT/TTS are fully live; the "restart required" fallback (spec §error handling) is N/A here because both sidecars start/stop live — no badge needed for this slice. (It becomes relevant for voice-id in Slice 3.)
- **Channel "unavailable" UX** (spec §edge cases, master-off ⇒ voice channel advertises unavailable) is intentionally deferred: stopping the sidecars already disables voice functionally. If desired, fold a channel-availability check into Slice 3 where voice-id/channel gating is touched.

---

## Roadmap (the other two slices — separate plans, written when reached)

**Slice 2 — Built-in memory toggle** (`docs/plans/…-memory-toggle-plan.md`)
1. Add `MemorySettingsConfig.Enabled` + `MemoryConfig.MemoryEnabled` DTO field (Contracts).
2. `MemorySettingsStore.MemoryEnabled` + push via `CredentialsPusher.BuildMemoryConfig` / `UpdateMemoryConfigAsync` → `AgentHub` handler.
3. Agent gate: hide the 5 memory tools, skip extraction-buffer flush + `MemoryExtractionService`, skip `MemoryCompactionService`, drop memory guidance from the system prompt.
4. `embeddings` sidecar: add `profiles:[memory]`; new `EmbeddingsSidecarLifecycle` (mirror STT); relax `cortex-agent.depends_on: embeddings`; **memory-off boot test**.
5. UI toggle + `/api/memory/toggle` (or extend the memory settings save).

**Slice 3 — Voice-id toggle** (`docs/plans/…-voiceid-toggle-plan.md`)
1. Add `VoiceIdConfig` (with `Enabled`) under `SpeechConfig`; extend `SpeechToggles` with `EffectiveVoiceId`.
2. `voice-id` sidecar: add `profiles:[voiceid]`; new `VoiceIdSidecarLifecycle`; relax `cortex-agent.depends_on: voice-id`.
3. New SignalR push for `VoiceIdEnabled` (no existing channel) → agent gates `EnrollmentOrchestrator` usage; keep tool registration on model-presence.
4. UI sub-toggle under the Voice master; "restart required" fallback badge when a live compose op fails (spec §error handling).
