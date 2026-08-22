using Cortex.Contained.Bridge.Speech;
using Cortex.Contained.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class SpeechSidecarFaultRecoveryTests
{
    private sealed record Harness(
        SpeechSidecarFaultRecovery Sut,
        IComposeCommandRunner TtsRunner,
        ISttComposeRunner SttRunner,
        FakeTimeProvider Clock);

    private static Harness Make(bool restartSucceeds = true)
    {
        var tts = Substitute.For<IComposeCommandRunner>();
        tts.RestartDanishAsync(Arg.Any<CancellationToken>()).Returns(restartSucceeds);
        var stt = Substitute.For<ISttComposeRunner>();
        stt.RestartSttAsync(Arg.Any<CancellationToken>()).Returns(restartSucceeds);
        var clock = new FakeTimeProvider();
        return new Harness(
            new SpeechSidecarFaultRecovery(tts, stt, clock, NullLogger<SpeechSidecarFaultRecovery>.Instance),
            tts, stt, clock);
    }

    private static async Task FaultAsync(
        SpeechSidecarFaultRecovery sut, SpeechSidecar sidecar, int times, int statusCode = 500)
    {
        for (var i = 0; i < times; i++)
        {
            sut.OnSidecarFault(sidecar, "detail", statusCode);
        }

        if (sut.PendingRecovery is { } pending)
        {
            await pending;
        }
    }

    [Fact]
    public async Task BelowThreshold_DoesNotRestart()
    {
        var h = Make();

        await FaultAsync(h.Sut, SpeechSidecar.Tts, SpeechSidecarFaultRecovery.FaultThreshold - 1);

        await h.TtsRunner.DidNotReceive().RestartDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TtsAtThreshold_RestartsUniVoicesOnly()
    {
        var h = Make();

        await FaultAsync(h.Sut, SpeechSidecar.Tts, SpeechSidecarFaultRecovery.FaultThreshold);

        await h.TtsRunner.Received(1).RestartDanishAsync(Arg.Any<CancellationToken>());
        await h.SttRunner.DidNotReceive().RestartSttAsync(Arg.Any<CancellationToken>());
        Assert.Equal(0, h.Sut.ConsecutiveFaults(SpeechSidecar.Tts));
    }

    [Fact]
    public async Task SttAtThreshold_RestartsWhisperOnly()
    {
        var h = Make();

        await FaultAsync(h.Sut, SpeechSidecar.Stt, SpeechSidecarFaultRecovery.FaultThreshold);

        await h.SttRunner.Received(1).RestartSttAsync(Arg.Any<CancellationToken>());
        await h.TtsRunner.DidNotReceive().RestartDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FaultsAreTrackedPerSidecar()
    {
        var h = Make();

        // Two faults each: neither sidecar reaches the threshold on its own, and the
        // counters must not be pooled into a single restart trigger.
        await FaultAsync(h.Sut, SpeechSidecar.Tts, 2);
        await FaultAsync(h.Sut, SpeechSidecar.Stt, 2);

        await h.TtsRunner.DidNotReceive().RestartDanishAsync(Arg.Any<CancellationToken>());
        await h.SttRunner.DidNotReceive().RestartSttAsync(Arg.Any<CancellationToken>());
        Assert.Equal(2, h.Sut.ConsecutiveFaults(SpeechSidecar.Tts));
        Assert.Equal(2, h.Sut.ConsecutiveFaults(SpeechSidecar.Stt));
    }

    [Fact]
    public async Task SuccessOnOneSidecar_DoesNotClearTheOther()
    {
        var h = Make();

        await FaultAsync(h.Sut, SpeechSidecar.Tts, 2);
        h.Sut.OnSidecarSuccess(SpeechSidecar.Stt);

        Assert.Equal(2, h.Sut.ConsecutiveFaults(SpeechSidecar.Tts));
    }

    [Fact]
    public async Task ClientErrors_DoNotCountTowardRestart()
    {
        var h = Make();

        await FaultAsync(h.Sut, SpeechSidecar.Tts, SpeechSidecarFaultRecovery.FaultThreshold * 2, statusCode: 400);

        await h.TtsRunner.DidNotReceive().RestartDanishAsync(Arg.Any<CancellationToken>());
        Assert.Equal(0, h.Sut.ConsecutiveFaults(SpeechSidecar.Tts));
    }

    [Fact]
    public async Task Success_ResetsStreak()
    {
        var h = Make();

        await FaultAsync(h.Sut, SpeechSidecar.Tts, SpeechSidecarFaultRecovery.FaultThreshold - 1);
        h.Sut.OnSidecarSuccess(SpeechSidecar.Tts);
        Assert.Equal(0, h.Sut.ConsecutiveFaults(SpeechSidecar.Tts));

        await FaultAsync(h.Sut, SpeechSidecar.Tts, 1);

        await h.TtsRunner.DidNotReceive().RestartDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SecondBurst_WithinCooldown_IsSuppressed()
    {
        var h = Make();

        await FaultAsync(h.Sut, SpeechSidecar.Tts, SpeechSidecarFaultRecovery.FaultThreshold);
        h.Clock.Advance(SpeechSidecarFaultRecovery.RestartCooldown - TimeSpan.FromSeconds(1));
        await FaultAsync(h.Sut, SpeechSidecar.Tts, SpeechSidecarFaultRecovery.FaultThreshold);

        await h.TtsRunner.Received(1).RestartDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SecondBurst_AfterCooldown_RestartsAgain()
    {
        var h = Make();

        await FaultAsync(h.Sut, SpeechSidecar.Tts, SpeechSidecarFaultRecovery.FaultThreshold);
        h.Clock.Advance(SpeechSidecarFaultRecovery.RestartCooldown + TimeSpan.FromSeconds(1));
        await FaultAsync(h.Sut, SpeechSidecar.Tts, SpeechSidecarFaultRecovery.FaultThreshold);

        await h.TtsRunner.Received(2).RestartDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CooldownIsPerSidecar()
    {
        var h = Make();

        await FaultAsync(h.Sut, SpeechSidecar.Tts, SpeechSidecarFaultRecovery.FaultThreshold);
        // STT has never been restarted, so the TTS cooldown must not block it.
        await FaultAsync(h.Sut, SpeechSidecar.Stt, SpeechSidecarFaultRecovery.FaultThreshold);

        await h.SttRunner.Received(1).RestartSttAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestartFails_StillHonoursCooldown()
    {
        var h = Make(restartSucceeds: false);

        await FaultAsync(h.Sut, SpeechSidecar.Tts, SpeechSidecarFaultRecovery.FaultThreshold);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await FaultAsync(h.Sut, SpeechSidecar.Tts, SpeechSidecarFaultRecovery.FaultThreshold);

        await h.TtsRunner.Received(1).RestartDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunnerThrows_DoesNotPropagate()
    {
        var tts = Substitute.For<IComposeCommandRunner>();
        tts.RestartDanishAsync(Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("docker missing"));
        var sut = new SpeechSidecarFaultRecovery(
            tts, Substitute.For<ISttComposeRunner>(), new FakeTimeProvider(),
            NullLogger<SpeechSidecarFaultRecovery>.Instance);

        await FaultAsync(sut, SpeechSidecar.Tts, SpeechSidecarFaultRecovery.FaultThreshold);

        Assert.False(await sut.TryRecoverAsync(SpeechSidecar.Tts, CancellationToken.None));
    }

    [Fact]
    public async Task TryRecover_WithoutFaults_IsNoOp()
    {
        var h = Make();

        Assert.False(await h.Sut.TryRecoverAsync(SpeechSidecar.Tts, CancellationToken.None));
        await h.TtsRunner.DidNotReceive().RestartDanishAsync(Arg.Any<CancellationToken>());
    }
}
