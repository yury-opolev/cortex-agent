using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

/// <summary>
/// Replays the real 2026-08-19 Discord-voice outage through the recovery chain
/// (log classification → policy decision) and pins both halves of the fix.
/// <para>
/// The incident: a join-race MLS failure armed a rejoin; Discord's welcome healed the
/// group 70&#160;ms later and the bot successfully decrypted the user's speech; the
/// watchdog fired the armed rejoin anyway ~950&#160;ms later and destroyed the working
/// session; the forced reconnect timed out; and the replacement session never completed
/// its DAVE handshake, leaving the bot deaf for ~100 minutes with every failure counter
/// reading zero.
/// </para>
/// <para>
/// Timestamps and log strings below are taken verbatim from <c>bridge-20260819.log</c>.
/// </para>
/// </summary>
public class DaveVoiceOutageReplayTests
{
    private static readonly long JoinRaceWindow = 20 * TimeSpan.TicksPerSecond;
    private static readonly long SettleWindow = 5 * TimeSpan.TicksPerSecond;
    private static readonly long StallWindow = 30 * TimeSpan.TicksPerSecond;

    private static long At(string hhmmssfff) =>
        DateTime.ParseExact("2026-08-19 " + hhmmssfff, "yyyy-MM-dd HH:mm:ss.fff", null).Ticks;

    /// <summary>Drives the lifecycle classifier exactly as DiscordChannel.OnDiscordLog does.</summary>
    private static (long Started, long Ready) Apply(
        (long Ticks, string Source, string Message)[] lines,
        long started = 0,
        long ready = 0)
    {
        foreach (var (ticks, source, message) in lines)
        {
            switch (DaveSessionLifecycleClassifier.Classify(source, message))
            {
                case DaveLifecycleEvent.HandshakeStarted:
                    started = ticks;
                    ready = 0; // a fresh handshake invalidates the previous milestone
                    break;
                case DaveLifecycleEvent.SessionReady:
                    ready = ticks;
                    break;
                default:
                    break;
            }
        }

        return (started, ready);
    }

    [Fact]
    public void HealthyAudio5_SelfHealedMlsFailure_NoLongerForcesRejoin()
    {
        // ── the exact Audio #5 sequence ──
        var join = At("19:02:41.174");
        var mlsFailure = At("19:02:41.902");
        var watchdogTick = At("19:02:42.853"); // when the old code tore it all down

        var (_, ready) = Apply(
        [
            (At("19:02:41.889"), "Dave #5", "Init dave protocol session, version 1"),
            (At("19:02:41.972"), "Dave #5", "Preparing to transition to protocol version 1 (transition #0)"),
        ]);

        // Stage 1 still arms — the failure genuinely lands in the join race.
        Assert.True(DaveMlsRecoveryPolicy.ShouldArm(
            everJoined: true,
            ticksSinceJoin: mlsFailure - join,
            joinRaceWindowTicks: JoinRaceWindow));

        // Stage 2 refuses: the group was established 70ms after the failure, which is
        // why the bot then produced "voice-in: speech onset utt=b744613b" at 19:02:42.322.
        Assert.True(ready > mlsFailure);
        Assert.False(DaveMlsRecoveryPolicy.ShouldRecover(
            armedTicks: mlsFailure,
            sessionReadyTicks: ready,
            nowTicks: watchdogTick,
            settleWindowTicks: SettleWindow));

        // ...and it stays refused for the rest of the session, not just past the settle window.
        Assert.False(DaveMlsRecoveryPolicy.ShouldRecover(
            armedTicks: mlsFailure,
            sessionReadyTicks: ready,
            nowTicks: At("20:41:34.596"),
            settleWindowTicks: SettleWindow));
    }

    [Fact]
    public void WedgedAudio7_HandshakeNeverCompletes_IsDetected()
    {
        // ── the exact Audio #7 sequence: init + key package, external sender, then nothing ──
        var (started, ready) = Apply(
        [
            (At("19:02:58.472"), "Dave #7", "Init dave protocol session, version 1"),
            (At("19:02:58.475"), "Dave #7", "Received Binary DaveMLSExternalSender | 71 bytes, sequence #1"),
            (At("19:02:58.475"), "Dave #7", "Handling external sender package"),
        ]);

        Assert.Equal(At("19:02:58.472"), started);
        Assert.Equal(0, ready);
        Assert.True(DaveHandshakeStallPolicy.IsUnhealed(started, ready));

        // The watchdog latches on its first tick with the user present, then acts once
        // the stall window elapses — roughly 40s in, versus the 100 minutes it actually took.
        var latched = At("19:03:08.000");
        Assert.False(DaveHandshakeStallPolicy.ShouldRecover(latched, At("19:03:28.000"), StallWindow));
        Assert.True(DaveHandshakeStallPolicy.ShouldRecover(latched, At("19:03:38.000"), StallWindow));

        Assert.Equal(
            "dave-handshake-stall",
            ForceReconnectTrigger.Resolve(daveMlsSuspect: false, decryptFloodSuspect: false, daveHandshakeStallSuspect: true));
    }

    [Fact]
    public void WedgedAudio7_WasInvisibleToEveryPreExistingWatchdog()
    {
        // Why nothing fired for 100 minutes: the transport reported Connected, and the
        // DAVE stats line read "decryptFail=0 ... mlsFail=0" for the entire outage.
        Assert.False(DaveDecryptFloodPolicy.ShouldRecover(
            userPresent: true,
            failuresSinceCommit: 0,
            ticksSinceFirstFailure: 100 * 60 * TimeSpan.TicksPerSecond,
            floodThreshold: 50,
            minWindowTicks: 30 * TimeSpan.TicksPerSecond));

        Assert.False(DaveMlsRecoveryPolicy.ShouldRecover(
            armedTicks: 0, // no MLS failure was ever logged on Audio #7
            sessionReadyTicks: 0,
            nowTicks: At("20:41:34.596"),
            settleWindowTicks: SettleWindow));

        Assert.Equal(
            WatchdogAction.None,
            VoiceWatchdogDecision.Decide(
                userPresent: true,
                isConnected: true,
                suspectDead: false,
                lastForcedReconnectTicks: 0,
                nowTicks: At("20:00:00.000"),
                cooldownTicks: 25 * TimeSpan.TicksPerSecond));
    }

    [Fact]
    public void MalformedFrameRate_IsNotUsedAsAHealthSignal()
    {
        // Guards the reasoning that kept a malformed-frame detector out of the fix: the
        // healthy 08-08 session logged ~10 malformed frames per 5s sample while the
        // wedged 08-19 session logged ~5. The counter is inverted with respect to health
        // — it counts non-Opus control traffic (~1Hz per registered source), not faults.
        // A wedged session must therefore be detected structurally, via the handshake.
        var wedged = Apply([(At("19:02:58.472"), "Dave #7", "Init dave protocol session, version 1")]);
        Assert.True(DaveHandshakeStallPolicy.IsUnhealed(wedged.Started, wedged.Ready));

        var healthy = Apply(
        [
            (At("19:02:41.889"), "Dave #5", "Init dave protocol session, version 1"),
            (At("19:02:41.972"), "Dave #5", "Preparing to transition to protocol version 1 (transition #0)"),
        ]);
        Assert.False(DaveHandshakeStallPolicy.IsUnhealed(healthy.Started, healthy.Ready));

        // "Malformed Frame" is still classified for telemetry, but drives no recovery.
        Assert.Equal(DaveEventKind.MalformedFrame, DaveEventStats.Classify("Audio #7", "Malformed Frame"));
    }
}
