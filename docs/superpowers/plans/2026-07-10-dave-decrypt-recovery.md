# DAVE decrypt-desync recovery & instrumentation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the Discord voice bot a way to (a) be tested with DAVE E2EE off, and (b) auto-recover from the inbound DAVE decrypt-desync that leaves it silently deaf, plus instrument the desync for a later in-place fix.

**Architecture:** Three additions to the Discord channel, all following existing patterns. (1) A config toggle for `EnableVoiceDaveEncryption` (default true, byte-inert). (2) A pure decrypt-flood decision policy + a per-user burst tracker that both *logs* the flood (instrumentation) and *feeds* a new watchdog recovery trigger. (3) A `4017` close-code classifier so a DAVE-off experiment fails loudly. All recovery reuses the existing `ForceReconnectAsync` machinery.

**Tech Stack:** C# / .NET 10, xUnit + NSubstitute. Discord.Net **3.20.1 from NuGet** (the `lib/Discord.Net` submodule is vestigial and is NOT built — do not modify it).

## Global Constraints

- **C# style:** `this.` on all instance members; braces on every block; one type per file; file-scoped namespaces; `sealed` where not designed for inheritance; source-generated `[LoggerMessage]` for logs; `camelCase` private fields (no underscore) in cortex code (NOTE: the existing `DiscordVoiceHandler`/`DiscordChannel` predate this and use some other conventions — **match the conventions already in the file you edit**).
- **Test naming:** `Method_Condition_Expected`. Tests under `tests/Cortex.Contained.Channels.Discord.Tests/`.
- **DAVE default stays ON:** `EnableVoiceDaveEncryption` defaults to `true` everywhere. Discord enforces DAVE for non-stage voice as of March 2026 (close code `4017`); shipping it off by default would break all voice. The default-true unit test is the guard — do not weaken it.
- **Do NOT modify `lib/Discord.Net`** — the build uses the NuGet package; submodule edits have zero effect.
- **Time is injected:** pure policies/trackers take `nowTicks` (or deltas) as parameters — never call `DateTime.Now`/`Environment.TickCount` inside them. The handler already holds a `timeProvider` (`this.timeProvider.GetUtcNow().UtcTicks`).
- **TreatWarningsAsErrors is on** — no warnings in build or test output.

---

### Task 1: `EnableVoiceDaveEncryption` config toggle (default true)

Makes DAVE on/off a `cortex.yml` decision so the elimination experiment is reversible without a rebuild. Client-level setting (whole bot connection), so it lives on `DiscordChannelOptions` — NOT `VoiceHandlerConfig`.

**Files:**
- Modify: `src/Cortex.Contained.Channels.Discord/DiscordChannelOptions.cs`
- Modify: `src/Cortex.Contained.Channels.Discord/DiscordChannel.cs:164`
- Modify: `src/Cortex.Contained.Bridge/Program.cs` (the `DiscordChannelOptions`/`globalOptions` initializer near line 588–627, where `EnableBargeIn` etc. are parsed)
- Test: `tests/Cortex.Contained.Channels.Discord.Tests/DiscordChannelOptionsTests.cs` (create if absent)

**Interfaces:**
- Produces: `DiscordChannelOptions.EnableVoiceDaveEncryption` (`bool`, default `true`).

- [ ] **Step 1: Write the failing test**

Create/append `tests/Cortex.Contained.Channels.Discord.Tests/DiscordChannelOptionsTests.cs`:

```csharp
using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public sealed class DiscordChannelOptionsTests
{
    [Fact]
    public void EnableVoiceDaveEncryption_Default_IsTrue()
    {
        var options = new DiscordChannelOptions { BotToken = "t" };
        Assert.True(options.EnableVoiceDaveEncryption);
    }

    [Fact]
    public void EnableVoiceDaveEncryption_CanBeDisabled()
    {
        var options = new DiscordChannelOptions { BotToken = "t", EnableVoiceDaveEncryption = false };
        Assert.False(options.EnableVoiceDaveEncryption);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Channels.Discord.Tests --filter "ClassName=Cortex.Contained.Channels.Discord.Tests.DiscordChannelOptionsTests"`
Expected: FAIL — `DiscordChannelOptions` has no `EnableVoiceDaveEncryption`.

- [ ] **Step 3: Add the property**

In `src/Cortex.Contained.Channels.Discord/DiscordChannelOptions.cs`, add (next to `EnableBargeIn`), matching the file's existing XML-doc + `{ get; init; }` style:

```csharp
    /// <summary>
    /// Whether the bot advertises Discord DAVE (voice E2EE) support. Default true.
    /// Discord requires DAVE for non-stage voice as of March 2026 — setting this
    /// false makes the bot connect without E2EE and Discord may reject the voice
    /// join with close code 4017. Internal/experimental (cortex.yml only).
    /// </summary>
    public bool EnableVoiceDaveEncryption { get; init; } = true;
```

- [ ] **Step 4: Use it at the socket-config site**

In `src/Cortex.Contained.Channels.Discord/DiscordChannel.cs`, replace the hardcoded line 164 `EnableVoiceDaveEncryption = true,` with:

```csharp
            EnableVoiceDaveEncryption = this.options.EnableVoiceDaveEncryption,
```

(Confirm `this.options` is assigned before this config-builder runs; it is set from the ctor parameter.)

- [ ] **Step 5: Plumb from Bridge config**

In `src/Cortex.Contained.Bridge/Program.cs`, inside the `DiscordChannelOptions` initializer (the block containing `EnableBargeIn = ...` near line 600), add a parsed field, mirroring the `EnableBargeIn` default-true idiom exactly:

```csharp
            EnableVoiceDaveEncryption = !bool.TryParse(
                discordConfig.Settings.GetValueOrDefault("EnableVoiceDaveEncryption")
                    ?? builder.Configuration["Discord:EnableVoiceDaveEncryption"],
                out var enableVoiceDave) || enableVoiceDave, // default true
```

- [ ] **Step 6: Run tests + build to verify pass**

Run: `dotnet test tests/Cortex.Contained.Channels.Discord.Tests --filter "ClassName=Cortex.Contained.Channels.Discord.Tests.DiscordChannelOptionsTests"`
Expected: PASS.
Run: `dotnet build src/Cortex.Contained.Bridge/Cortex.Contained.Bridge.csproj`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add src/Cortex.Contained.Channels.Discord/DiscordChannelOptions.cs \
        src/Cortex.Contained.Channels.Discord/DiscordChannel.cs \
        src/Cortex.Contained.Bridge/Program.cs \
        tests/Cortex.Contained.Channels.Discord.Tests/DiscordChannelOptionsTests.cs
git commit -m "feat(discord-voice): configurable EnableVoiceDaveEncryption (default true)"
```

---

### Task 2: `4017` DAVE-required close-code classifier + loud log

So a DAVE-off experiment that Discord rejects is obvious and trivially reverted. Pure classifier wired into the existing `OnDiscordLog` alongside the audio-death / MLS classifiers.

**Files:**
- Create: `src/Cortex.Contained.Channels.Discord/DaveRequiredCloseClassifier.cs`
- Modify: `src/Cortex.Contained.Channels.Discord/DiscordChannel.cs` (`OnDiscordLog` + a new `[LoggerMessage]`)
- Test: `tests/Cortex.Contained.Channels.Discord.Tests/DaveRequiredCloseClassifierTests.cs`

**Interfaces:**
- Produces: `static bool DaveRequiredCloseClassifier.IsDaveRequired(string? source, string? message)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Cortex.Contained.Channels.Discord.Tests/DaveRequiredCloseClassifierTests.cs`:

```csharp
using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public sealed class DaveRequiredCloseClassifierTests
{
    [Theory]
    [InlineData("Voice", "Voice connection closed: 4017")]
    [InlineData("Voice", "WebSocket closed with code 4017 EndToEndEncryptionDAVEProtocolRequired")]
    [InlineData(null, "Disconnected: close code 4017")]
    public void IsDaveRequired_Close4017_ReturnsTrue(string? source, string message)
    {
        Assert.True(DaveRequiredCloseClassifier.IsDaveRequired(source, message));
    }

    [Theory]
    [InlineData("Gateway", "A task was canceled.")]
    [InlineData("Voice", "Disconnected: 4014")]
    [InlineData("Voice", "")]
    [InlineData("Voice", null)]
    public void IsDaveRequired_Unrelated_ReturnsFalse(string? source, string? message)
    {
        Assert.False(DaveRequiredCloseClassifier.IsDaveRequired(source, message));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Channels.Discord.Tests --filter "ClassName=Cortex.Contained.Channels.Discord.Tests.DaveRequiredCloseClassifierTests"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement the classifier**

Create `src/Cortex.Contained.Channels.Discord/DaveRequiredCloseClassifier.cs`:

```csharp
namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Pure classifier for Discord voice close code 4017
/// (<c>EndToEndEncryptionDAVEProtocolRequired</c>) — the gateway rejecting a
/// non-DAVE client. Surfaced so that disabling <see cref="DiscordChannelOptions.EnableVoiceDaveEncryption"/>
/// on a channel that mandates DAVE fails loudly instead of silently.
/// Best-effort text match against the Discord.Net voice log line (the close code
/// is not exposed structurally to consumers).
/// </summary>
public static class DaveRequiredCloseClassifier
{
    public static bool IsDaveRequired(string? source, string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        if (message.Contains("4017", StringComparison.Ordinal))
        {
            return true;
        }

        return message.Contains("EndToEndEncryptionDAVEProtocolRequired", StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Cortex.Contained.Channels.Discord.Tests --filter "ClassName=Cortex.Contained.Channels.Discord.Tests.DaveRequiredCloseClassifierTests"`
Expected: PASS.

- [ ] **Step 5: Wire into `OnDiscordLog`**

In `src/Cortex.Contained.Channels.Discord/DiscordChannel.cs`, inside `OnDiscordLog` (after the existing MLS-failure block near line 905, before the severity dispatch), add:

```csharp
        // A DAVE-disabled experiment against a channel that mandates E2EE is
        // rejected by the voice gateway with close code 4017. Surface it loudly
        // so the operator knows to re-enable enableVoiceDaveEncryption.
        if (DaveRequiredCloseClassifier.IsDaveRequired(logMsg.Source, logMsg.Message)
            || DaveRequiredCloseClassifier.IsDaveRequired(logMsg.Source, logMsg.Exception?.Message))
        {
            this.LogDaveRequiredByChannel();
        }
```

Add the logger message next to the other `[LoggerMessage]` declarations in the file:

```csharp
    [LoggerMessage(Level = LogLevel.Warning, Message = "Voice rejected with close code 4017 (DAVE required by this channel). Set channels.discord.settings.EnableVoiceDaveEncryption=true and restart the Bridge to restore voice.")]
    private partial void LogDaveRequiredByChannel();
```

- [ ] **Step 6: Build to verify pass**

Run: `dotnet build src/Cortex.Contained.Channels.Discord/Cortex.Contained.Channels.Discord.csproj`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add src/Cortex.Contained.Channels.Discord/DaveRequiredCloseClassifier.cs \
        src/Cortex.Contained.Channels.Discord/DiscordChannel.cs \
        tests/Cortex.Contained.Channels.Discord.Tests/DaveRequiredCloseClassifierTests.cs
git commit -m "feat(discord-voice): detect + log 4017 DAVE-required voice close"
```

---

### Task 3: `DaveDecryptFloodPolicy` — pure recovery decision

Decides whether an ongoing inbound decrypt-flood warrants forcing a clean rejoin. Mirrors `DaveMlsRecoveryPolicy` / `VoiceWatchdogDecision` (pure, unit-testable).

**Files:**
- Create: `src/Cortex.Contained.Channels.Discord/DaveDecryptFloodPolicy.cs`
- Test: `tests/Cortex.Contained.Channels.Discord.Tests/DaveDecryptFloodPolicyTests.cs`

**Interfaces:**
- Produces: `static bool DaveDecryptFloodPolicy.ShouldRecover(bool userPresent, long failuresSinceCommit, long ticksSinceFirstFailure, long floodThreshold, long minWindowTicks)`.
- Consumed by: Task 5 (watchdog wiring).

**Design:** Trip only when the user is present, decrypt failures have accumulated past `floodThreshold` since the last successful speech commit (proves packets are arriving = the user is transmitting — silence produces none), and the flood has persisted at least `minWindowTicks` (~30 s) since its first failure. A successful commit resets `failuresSinceCommit` to 0 (see Task 5), so a healthy conversation can never trip it.

- [ ] **Step 1: Write the failing tests**

Create `tests/Cortex.Contained.Channels.Discord.Tests/DaveDecryptFloodPolicyTests.cs`:

```csharp
using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public sealed class DaveDecryptFloodPolicyTests
{
    private const long Threshold = 50;
    private static readonly long Window = TimeSpan.FromSeconds(30).Ticks;

    [Fact]
    public void ShouldRecover_SustainedFloodNoCommitUserPresent_Trips()
    {
        Assert.True(DaveDecryptFloodPolicy.ShouldRecover(
            userPresent: true, failuresSinceCommit: 200,
            ticksSinceFirstFailure: Window + 1, Threshold, Window));
    }

    [Fact]
    public void ShouldRecover_Silence_NoFailures_DoesNotTrip()
    {
        // No decrypt failures at all — user simply not talking.
        Assert.False(DaveDecryptFloodPolicy.ShouldRecover(
            userPresent: true, failuresSinceCommit: 0,
            ticksSinceFirstFailure: 0, Threshold, Window));
    }

    [Fact]
    public void ShouldRecover_BelowThreshold_DoesNotTrip()
    {
        // A brief transient burst (e.g. normal epoch churn) under the threshold.
        Assert.False(DaveDecryptFloodPolicy.ShouldRecover(
            userPresent: true, failuresSinceCommit: 10,
            ticksSinceFirstFailure: Window + 1, Threshold, Window));
    }

    [Fact]
    public void ShouldRecover_FloodButWithinWindow_DoesNotTripYet()
    {
        Assert.False(DaveDecryptFloodPolicy.ShouldRecover(
            userPresent: true, failuresSinceCommit: 200,
            ticksSinceFirstFailure: Window - 1, Threshold, Window));
    }

    [Fact]
    public void ShouldRecover_UserAbsent_DoesNotTrip()
    {
        Assert.False(DaveDecryptFloodPolicy.ShouldRecover(
            userPresent: false, failuresSinceCommit: 200,
            ticksSinceFirstFailure: Window + 1, Threshold, Window));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Cortex.Contained.Channels.Discord.Tests --filter "ClassName=Cortex.Contained.Channels.Discord.Tests.DaveDecryptFloodPolicyTests"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement the policy**

Create `src/Cortex.Contained.Channels.Discord/DaveDecryptFloodPolicy.cs`:

```csharp
namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Pure policy deciding whether a sustained inbound DAVE decrypt-flood warrants
/// forcing a clean voice rejoin. Extracted so the recovery rule is unit-testable
/// without a live Discord client.
/// </summary>
/// <remarks>
/// Root cause (2026-07-08 outage): after a join-race-seeded MLS group develops a
/// per-sender media-key ratchet desync, inbound audio packets fail to decrypt in
/// bursts each time the user resumes speaking. No decrypted PCM reaches the VAD,
/// so the agent never hears the user and stays silent — the transport still
/// reports Connected and no MLS failure fires, so nothing self-heals until the
/// user manually rejoins. We detect the flood and force one clean rejoin.
/// <para>
/// The trip is scoped so ordinary silence and healthy conversation never fire it:
/// decrypt failures only accrue when the user is actually transmitting (packets
/// arriving but failing), and any successful speech commit resets the count to
/// zero (the caller's responsibility).
/// </para>
/// </remarks>
public static class DaveDecryptFloodPolicy
{
    /// <param name="userPresent">Linked user is in the target voice channel.</param>
    /// <param name="failuresSinceCommit">Decrypt failures accumulated since the
    /// last successful speech commit (reset to 0 on commit / (re)join).</param>
    /// <param name="ticksSinceFirstFailure">Ticks since the first failure of the
    /// current run. Only meaningful when <paramref name="failuresSinceCommit"/> &gt; 0.</param>
    /// <param name="floodThreshold">Minimum accumulated failures to consider a flood.</param>
    /// <param name="minWindowTicks">Minimum age of the flood before acting.</param>
    public static bool ShouldRecover(
        bool userPresent,
        long failuresSinceCommit,
        long ticksSinceFirstFailure,
        long floodThreshold,
        long minWindowTicks)
    {
        if (!userPresent)
        {
            return false;
        }

        if (failuresSinceCommit < floodThreshold)
        {
            return false;
        }

        return ticksSinceFirstFailure >= minWindowTicks;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Cortex.Contained.Channels.Discord.Tests --filter "ClassName=Cortex.Contained.Channels.Discord.Tests.DaveDecryptFloodPolicyTests"`
Expected: PASS (all 5).

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Channels.Discord/DaveDecryptFloodPolicy.cs \
        tests/Cortex.Contained.Channels.Discord.Tests/DaveDecryptFloodPolicyTests.cs
git commit -m "feat(discord-voice): DaveDecryptFloodPolicy pure recovery decision"
```

---

### Task 4: `DaveDecryptBurstTracker` — per-user flood accumulator + instrumentation

Owns the mutable accumulation the policy reads, and produces a burst-summary for the diagnostic log when a run ends. Time is injected (`nowTicks`) so it is fully unit-testable. Per-user (keyed by SSRC-resolved user id) with a "worst active" probe for the watchdog.

**Files:**
- Create: `src/Cortex.Contained.Channels.Discord/DaveDecryptBurstTracker.cs`
- Test: `tests/Cortex.Contained.Channels.Discord.Tests/DaveDecryptBurstTrackerTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct DaveBurstSummary(ulong UserId, long FailureCount, long DurationMs, string ResultCode)`
  - `readonly record struct DaveBurstProbe(long FailuresSinceReset, long TicksSinceFirstFailure)`
  - `void DaveDecryptBurstTracker.RecordFailure(ulong userId, string resultCode, long nowTicks)`
  - `DaveBurstSummary? DaveDecryptBurstTracker.Reset(ulong userId, long nowTicks)` — returns a summary iff a run was active.
  - `IReadOnlyList<DaveBurstSummary> DaveDecryptBurstTracker.ResetAll(long nowTicks)`
  - `DaveBurstProbe DaveDecryptBurstTracker.WorstActive(long nowTicks)` — the user with the most accumulated failures (zeros when none active).
- Consumed by: Task 5.

- [ ] **Step 1: Write the failing tests**

Create `tests/Cortex.Contained.Channels.Discord.Tests/DaveDecryptBurstTrackerTests.cs`:

```csharp
using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public sealed class DaveDecryptBurstTrackerTests
{
    private static long Sec(double s) => (long)(s * TimeSpan.TicksPerSecond);

    [Fact]
    public void WorstActive_AccumulatesFailures_TracksCountAndAge()
    {
        var t = new DaveDecryptBurstTracker();
        t.RecordFailure(1, "DecryptionFailure", Sec(0));
        t.RecordFailure(1, "DecryptionFailure", Sec(1));
        t.RecordFailure(1, "DecryptionFailure", Sec(2));

        var probe = t.WorstActive(Sec(30));
        Assert.Equal(3, probe.FailuresSinceReset);
        Assert.Equal(Sec(30), probe.TicksSinceFirstFailure);
    }

    [Fact]
    public void WorstActive_NoFailures_IsZero()
    {
        var t = new DaveDecryptBurstTracker();
        var probe = t.WorstActive(Sec(10));
        Assert.Equal(0, probe.FailuresSinceReset);
    }

    [Fact]
    public void Reset_AfterFailures_ReturnsSummary_AndClears()
    {
        var t = new DaveDecryptBurstTracker();
        t.RecordFailure(1, "DecryptionFailure", Sec(0));
        t.RecordFailure(1, "DecryptionFailure", Sec(4));

        var summary = t.Reset(1, Sec(5));
        Assert.NotNull(summary);
        Assert.Equal(1UL, summary!.Value.UserId);
        Assert.Equal(2, summary.Value.FailureCount);
        Assert.Equal(4000, summary.Value.DurationMs);

        Assert.Equal(0, t.WorstActive(Sec(6)).FailuresSinceReset);
    }

    [Fact]
    public void Reset_NoActiveRun_ReturnsNull()
    {
        var t = new DaveDecryptBurstTracker();
        Assert.Null(t.Reset(1, Sec(1)));
    }

    [Fact]
    public void WorstActive_MultipleUsers_ReturnsHighestCount()
    {
        var t = new DaveDecryptBurstTracker();
        t.RecordFailure(1, "DecryptionFailure", Sec(0));
        t.RecordFailure(2, "DecryptionFailure", Sec(0));
        t.RecordFailure(2, "DecryptionFailure", Sec(1));

        Assert.Equal(2, t.WorstActive(Sec(2)).FailuresSinceReset);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Cortex.Contained.Channels.Discord.Tests --filter "ClassName=Cortex.Contained.Channels.Discord.Tests.DaveDecryptBurstTrackerTests"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement the tracker**

Create `src/Cortex.Contained.Channels.Discord/DaveDecryptBurstTracker.cs`:

```csharp
using System.Collections.Concurrent;

namespace Cortex.Contained.Channels.Discord;

/// <summary>Summary of a finished decrypt-failure run, for the diagnostic log.</summary>
public readonly record struct DaveBurstSummary(ulong UserId, long FailureCount, long DurationMs, string ResultCode);

/// <summary>Snapshot of the currently-worst active run, for the recovery watchdog.</summary>
public readonly record struct DaveBurstProbe(long FailuresSinceReset, long TicksSinceFirstFailure);

/// <summary>
/// Per-user accumulator for inbound DAVE decrypt failures. A "run" is an
/// uninterrupted sequence of failures for one user, ended by a successful speech
/// commit (<see cref="Reset(ulong, long)"/>) or a (re)join
/// (<see cref="ResetAll(long)"/>). Feeds both the diagnostic burst-summary log
/// and <see cref="DaveDecryptFloodPolicy"/>. Thread-safe (per-user lock via the
/// concurrent dictionary; counters are only touched under the entry lock).
/// </summary>
public sealed class DaveDecryptBurstTracker
{
    private sealed class Run
    {
        public long Count;
        public long FirstFailureTicks;
        public long LastFailureTicks;
        public string ResultCode = "Unknown";
    }

    private readonly ConcurrentDictionary<ulong, Run> runs = new();

    public void RecordFailure(ulong userId, string resultCode, long nowTicks)
    {
        var run = this.runs.GetOrAdd(userId, _ => new Run());
        lock (run)
        {
            if (run.Count == 0)
            {
                run.FirstFailureTicks = nowTicks;
            }

            run.Count++;
            run.LastFailureTicks = nowTicks;
            run.ResultCode = resultCode;
        }
    }

    public DaveBurstSummary? Reset(ulong userId, long nowTicks)
    {
        if (!this.runs.TryGetValue(userId, out var run))
        {
            return null;
        }

        lock (run)
        {
            if (run.Count == 0)
            {
                return null;
            }

            var summary = new DaveBurstSummary(
                userId,
                run.Count,
                (run.LastFailureTicks - run.FirstFailureTicks) / TimeSpan.TicksPerMillisecond,
                run.ResultCode);

            run.Count = 0;
            run.FirstFailureTicks = 0;
            run.LastFailureTicks = 0;
            return summary;
        }
    }

    public IReadOnlyList<DaveBurstSummary> ResetAll(long nowTicks)
    {
        var summaries = new List<DaveBurstSummary>();
        foreach (var userId in this.runs.Keys)
        {
            var summary = this.Reset(userId, nowTicks);
            if (summary is not null)
            {
                summaries.Add(summary.Value);
            }
        }

        return summaries;
    }

    public DaveBurstProbe WorstActive(long nowTicks)
    {
        long worstCount = 0;
        long worstFirstTicks = nowTicks;
        foreach (var run in this.runs.Values)
        {
            lock (run)
            {
                if (run.Count > worstCount)
                {
                    worstCount = run.Count;
                    worstFirstTicks = run.FirstFailureTicks;
                }
            }
        }

        return new DaveBurstProbe(worstCount, worstCount == 0 ? 0 : nowTicks - worstFirstTicks);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Cortex.Contained.Channels.Discord.Tests --filter "ClassName=Cortex.Contained.Channels.Discord.Tests.DaveDecryptBurstTrackerTests"`
Expected: PASS (all 5).

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Channels.Discord/DaveDecryptBurstTracker.cs \
        tests/Cortex.Contained.Channels.Discord.Tests/DaveDecryptBurstTrackerTests.cs
git commit -m "feat(discord-voice): DaveDecryptBurstTracker flood accumulator + burst summary"
```

---

### Task 5: Wire decrypt-flood detection into the voice handler + watchdog

Connects the pieces: decrypt-failure log lines → tracker; successful commit / (re)join → tracker reset (+ diagnostic summary log); watchdog tick → policy → `decryptFloodSuspect` → `ForceReconnectAsync("dave-decrypt-flood")`. Also extracts the force-reconnect trigger-label selection into a tiny testable pure function (the existing 2-way becomes 3-way).

**Files:**
- Create: `src/Cortex.Contained.Channels.Discord/ForceReconnectTrigger.cs` (pure label resolver)
- Modify: `src/Cortex.Contained.Channels.Discord/DiscordVoiceHandler.cs` (tracker field + constants, `NotifyDecryptFailure`, reset on commit, watchdog integration, trigger label, `[LoggerMessage]`s)
- Modify: `src/Cortex.Contained.Channels.Discord/DiscordChannel.cs` (`OnDiscordLog`: forward decrypt-failure lines to handlers)
- Test: `tests/Cortex.Contained.Channels.Discord.Tests/ForceReconnectTriggerTests.cs`

**Interfaces:**
- Consumes: `DaveDecryptFloodPolicy.ShouldRecover(...)` (Task 3), `DaveDecryptBurstTracker` + `DaveBurstProbe`/`DaveBurstSummary` (Task 4), `DaveEventStats.Classify`/`TryParseUserId` (existing).
- Produces: `static string ForceReconnectTrigger.Resolve(bool daveMlsSuspect, bool decryptFloodSuspect)`.

- [ ] **Step 1: Write the failing test (trigger label resolver)**

Create `tests/Cortex.Contained.Channels.Discord.Tests/ForceReconnectTriggerTests.cs`:

```csharp
using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public sealed class ForceReconnectTriggerTests
{
    [Fact]
    public void Resolve_DaveMls_ReturnsMlsTrigger()
        => Assert.Equal("dave-mls-failure", ForceReconnectTrigger.Resolve(daveMlsSuspect: true, decryptFloodSuspect: false));

    [Fact]
    public void Resolve_DecryptFlood_ReturnsFloodTrigger()
        => Assert.Equal("dave-decrypt-flood", ForceReconnectTrigger.Resolve(daveMlsSuspect: false, decryptFloodSuspect: true));

    [Fact]
    public void Resolve_Neither_ReturnsAudioDeathTrigger()
        => Assert.Equal("audio-death-signal", ForceReconnectTrigger.Resolve(daveMlsSuspect: false, decryptFloodSuspect: false));

    [Fact]
    public void Resolve_MlsTakesPrecedenceOverFlood()
        => Assert.Equal("dave-mls-failure", ForceReconnectTrigger.Resolve(daveMlsSuspect: true, decryptFloodSuspect: true));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Channels.Discord.Tests --filter "ClassName=Cortex.Contained.Channels.Discord.Tests.ForceReconnectTriggerTests"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement the label resolver**

Create `src/Cortex.Contained.Channels.Discord/ForceReconnectTrigger.cs`:

```csharp
namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Pure selection of the watchdog force-reconnect trigger label from the active
/// suspect flags. MLS-failure takes precedence (it re-speaks a pending proactive
/// message), then decrypt-flood, else a silent audio-transport death.
/// </summary>
public static class ForceReconnectTrigger
{
    public static string Resolve(bool daveMlsSuspect, bool decryptFloodSuspect)
    {
        if (daveMlsSuspect)
        {
            return "dave-mls-failure";
        }

        if (decryptFloodSuspect)
        {
            return "dave-decrypt-flood";
        }

        return "audio-death-signal";
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Cortex.Contained.Channels.Discord.Tests --filter "ClassName=Cortex.Contained.Channels.Discord.Tests.ForceReconnectTriggerTests"`
Expected: PASS.

- [ ] **Step 5: Add tracker + constants + `NotifyDecryptFailure` to the handler**

In `src/Cortex.Contained.Channels.Discord/DiscordVoiceHandler.cs`, near the other DAVE fields (around the `daveSessionSuspect` declaration ~line 214), add:

```csharp
    // ── DAVE inbound decrypt-flood recovery (auto-heal a deaf bot) ────
    //
    // The 2026-07-08 outage: a join-race-seeded MLS ratchet desync makes inbound
    // audio fail to decrypt in bursts whenever the user resumes speaking, so the
    // bot hears nothing and never replies while the transport reports Connected.
    // We accumulate decrypt failures (reset by any successful commit) and force
    // one clean rejoin once the flood is sustained. See DaveDecryptFloodPolicy.

    /// <summary>Accumulated decrypt failures past which a sustained flood is suspected.</summary>
    private const long DecryptFloodThreshold = 50;

    /// <summary>Minimum age of the flood before forcing a rejoin (~13x faster than
    /// the user's manual recovery, well clear of transient epoch-transition bursts).</summary>
    private static readonly TimeSpan DecryptFloodMinWindow = TimeSpan.FromSeconds(30);

    private readonly DaveDecryptBurstTracker decryptBurstTracker = new();

    /// <summary>Set by the watchdog when a sustained decrypt-flood is detected;
    /// consumed on the same tick to force a clean rejoin.</summary>
    private volatile bool decryptFloodSuspect;
```

Add the notify method near `NotifyDaveSessionSuspect` (record the failure into the tracker):

```csharp
    /// <summary>
    /// Called for each inbound DAVE decrypt-failure log line (from
    /// <see cref="DiscordChannel.OnDiscordLog"/>). Feeds the burst tracker that
    /// drives decrypt-flood recovery and the diagnostic summary log.
    /// </summary>
    internal void NotifyDecryptFailure(ulong userId, string resultCode)
    {
        this.decryptBurstTracker.RecordFailure(userId, resultCode, this.timeProvider.GetUtcNow().UtcTicks);
    }
```

- [ ] **Step 6: Reset the tracker on a successful commit (and log the burst summary)**

In `DiscordVoiceHandler.cs`, find the speech-commit site — the block that logs the per-turn DAVE drops (`LogDaveDropsForTurn`, ~line 1794–1810, inside the commit handling). Immediately after that log call, add a reset that also emits the diagnostic burst summary if a run was active:

```csharp
                            // A successful commit means inbound audio is getting
                            // through — clear any decrypt-flood run and log its
                            // shape for later diagnosis.
                            var floodSummary = this.decryptBurstTracker.Reset(
                                state.UserId, this.timeProvider.GetUtcNow().UtcTicks);
                            if (floodSummary is { } fs)
                            {
                                this.LogDecryptBurstEnded(fs.UserId, fs.FailureCount, fs.DurationMs, fs.ResultCode);
                            }
```

> **Implementer note:** confirm the enclosing scope exposes the committing user id. The per-utterance state (`UserAudioState`) carries `UserId`; use whatever the surrounding code already uses to identify the committing user at that site (it logs `utt=` for that user's utterance). If no user id is in scope, call `this.decryptBurstTracker.ResetAll(...)` instead and log each returned summary — the linked user is the only expected talker.

Also reset on (re)join so a fresh session starts clean. Find where `lastJoinTicks` is set after a successful join and add alongside it:

```csharp
            this.decryptBurstTracker.ResetAll(this.timeProvider.GetUtcNow().UtcTicks);
```

- [ ] **Step 7: Evaluate the flood in the watchdog tick (preserving the no-REST steady-state path)**

The existing watchdog deliberately avoids a REST `userPresent` call when the transport is healthy and nothing is suspect (the `if (isConnected && !suspect) { return; }` fast path). The flood pre-check must therefore be computed **purely in-memory** from the tracker (no REST) and folded into `suspect`; `userPresent` is only used to *confirm* after the existing gate. In `WatchdogTickAsync` (`DiscordVoiceHandler.cs` ~line 909), replace:

```csharp
        var audioDeathSuspect = this.suspectDead;
        var daveSuspect = this.daveSessionSuspect;
        var suspect = audioDeathSuspect || daveSuspect;
```

with (cheap in-memory flood candidate — `userPresent: true` so no REST here):

```csharp
        var audioDeathSuspect = this.suspectDead;
        var daveSuspect = this.daveSessionSuspect;

        var nowTicksForFlood = this.timeProvider.GetUtcNow().UtcTicks;
        var floodProbe = this.decryptBurstTracker.WorstActive(nowTicksForFlood);
        var floodCandidate = DaveDecryptFloodPolicy.ShouldRecover(
            userPresent: true, // real user-presence confirmed after the gate
            failuresSinceCommit: floodProbe.FailuresSinceReset,
            ticksSinceFirstFailure: floodProbe.TicksSinceFirstFailure,
            floodThreshold: DecryptFloodThreshold,
            minWindowTicks: DecryptFloodMinWindow.Ticks);

        var suspect = audioDeathSuspect || daveSuspect || floodCandidate;
```

The existing `if (isConnected && !suspect) { return; }` line stays as-is — it now also short-circuits when there is no flood candidate, so the steady-state path still makes no REST call. Immediately AFTER the existing `var userPresent = await IsUserInTargetChannelAsync(ct).ConfigureAwait(false);` line, confirm the flood with the real presence and expose it for the trigger label:

```csharp
        var floodSuspect = floodCandidate && userPresent;
        this.decryptFloodSuspect = floodSuspect;
```

> **Implementer note:** there must remain exactly ONE `IsUserInTargetChannelAsync` call per tick (the existing one). Do not add another. `floodProbe`/`floodCandidate` are computed with no REST call.

- [ ] **Step 8: Use the 3-way trigger label + clear the flood flag on reconnect**

In the `WatchdogAction.ForceReconnect` case (~line 951–964), replace the trigger-label expression:

```csharp
                await ForceReconnectAsync(daveSuspect ? "dave-mls-failure" : "audio-death-signal").ConfigureAwait(false);
```

with:

```csharp
                await ForceReconnectAsync(ForceReconnectTrigger.Resolve(daveSuspect, floodSuspect)).ConfigureAwait(false);
```

and in the same case, after the existing `this.daveSessionSuspect = false;`, also clear:

```csharp
                this.decryptFloodSuspect = false;
                this.decryptBurstTracker.ResetAll(nowTicksForFlood);
```

(Also clear `this.decryptFloodSuspect = false;` in the `WatchdogAction.Reconnect` case next to the existing suspect resets.)

- [ ] **Step 9: Add the logger messages**

Next to the other `[LoggerMessage]` declarations in `DiscordVoiceHandler.cs`:

```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: DAVE decrypt burst ended user={UserId} failures={FailureCount} durationMs={DurationMs} code={ResultCode}")]
    private partial void LogDecryptBurstEnded(ulong userId, long failureCount, long durationMs, string resultCode);
```

- [ ] **Step 10: Forward decrypt-failure log lines from `DiscordChannel`**

In `src/Cortex.Contained.Channels.Discord/DiscordChannel.cs` `OnDiscordLog`, the `kind` is already computed near line 854. After the MLS-failure block, add a decrypt-failure forward:

```csharp
        // Inbound decrypt failures feed the per-handler flood tracker that drives
        // decrypt-flood recovery (2026-07-08 deaf-bot outage).
        if (kind is DaveEventKind.DecryptFailure)
        {
            var failUserId = DaveEventStats.TryParseUserId(logMsg.Message);
            if (failUserId is { } uid)
            {
                var resultCode = ClassifyDecryptResultCode(logMsg.Message);
                DiscordVoiceHandler[] handlers;
                lock (this.voiceHandlersLock)
                {
                    handlers = [.. this.voiceHandlers.Values];
                }

                foreach (var handler in handlers)
                {
                    handler.NotifyDecryptFailure(uid, resultCode);
                }
            }
        }
```

Add a tiny private helper in `DiscordChannel` (the log line ends in `": {code}"`):

```csharp
    private static string ClassifyDecryptResultCode(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "Unknown";
        }

        var idx = message.LastIndexOf(": ", StringComparison.Ordinal);
        return idx >= 0 && idx + 2 < message.Length ? message[(idx + 2)..] : "Unknown";
    }
```

- [ ] **Step 11: Build + run the full Discord test project**

Run: `dotnet build src/Cortex.Contained.Channels.Discord/Cortex.Contained.Channels.Discord.csproj`
Expected: Build succeeded, 0 warnings.
Run: `dotnet test tests/Cortex.Contained.Channels.Discord.Tests`
Expected: PASS (all, including the 4 new test classes).

- [ ] **Step 12: Commit**

```bash
git add src/Cortex.Contained.Channels.Discord/ForceReconnectTrigger.cs \
        src/Cortex.Contained.Channels.Discord/DiscordVoiceHandler.cs \
        src/Cortex.Contained.Channels.Discord/DiscordChannel.cs \
        tests/Cortex.Contained.Channels.Discord.Tests/ForceReconnectTriggerTests.cs
git commit -m "feat(discord-voice): decrypt-flood watchdog recovery + burst instrumentation"
```

---

## Final verification (after all tasks)

- [ ] `dotnet build cortex-contained.sln` — 0 warnings, 0 errors.
- [ ] `dotnet test tests/Cortex.Contained.Channels.Discord.Tests` — all green, pristine output.
- [ ] Confirm `lib/Discord.Net` is untouched: `git status lib/Discord.Net` shows no changes.
- [ ] Manual/live (post-deploy, separate step): the DAVE-off test protocol from the spec; and if a wedge recurs with DAVE on, confirm a `voice-in: connection recovering trigger=dave-decrypt-flood` fires and observe (via `voice-in: DAVE decrypt burst ended ...`) whether the rejoin clears it.

## Spec coverage self-check

- Component 1 (toggle + 4017) → Tasks 1, 2. ✅
- Component 2 (instrumentation at cortex layer, shared accumulator) → Task 4 (tracker + summary) + Task 5 step 6/9 (summary log). ✅
- Component 3 (burst-aware watchdog, silence-safe) → Tasks 3, 4, 5. ✅ Silence-safe (no failures → no accrual → policy false); healthy-safe (commit resets). ✅
- Vendored lib untouched (NuGet constraint) → stated in Global Constraints + final check. ✅
- Stage 2 (in-place resync) → explicitly out of scope. ✅
