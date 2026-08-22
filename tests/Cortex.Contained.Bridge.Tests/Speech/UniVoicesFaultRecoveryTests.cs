using Cortex.Contained.Bridge.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class UniVoicesFaultRecoveryTests
{
    private static UniVoicesFaultRecovery Sut(IComposeCommandRunner runner, FakeTimeProvider clock) =>
        new(runner, clock, NullLogger<UniVoicesFaultRecovery>.Instance);

    private static (UniVoicesFaultRecovery Sut, IComposeCommandRunner Runner, FakeTimeProvider Clock) Make(
        bool restartSucceeds = true)
    {
        var runner = Substitute.For<IComposeCommandRunner>();
        runner.RestartDanishAsync(Arg.Any<CancellationToken>()).Returns(restartSucceeds);
        var clock = new FakeTimeProvider();
        return (Sut(runner, clock), runner, clock);
    }

    private static async Task FaultAsync(UniVoicesFaultRecovery sut, int times, int statusCode = 500)
    {
        for (var i = 0; i < times; i++)
        {
            sut.OnSynthesisFault("kokoro", statusCode);
        }

        if (sut.PendingRecovery is { } pending)
        {
            await pending;
        }
    }

    [Fact]
    public async Task BelowThreshold_DoesNotRestart()
    {
        var (sut, runner, _) = Make();

        await FaultAsync(sut, UniVoicesFaultRecovery.FaultThreshold - 1);

        await runner.DidNotReceive().RestartDanishAsync(Arg.Any<CancellationToken>());
        Assert.Equal(UniVoicesFaultRecovery.FaultThreshold - 1, sut.ConsecutiveFaults);
    }

    [Fact]
    public async Task AtThreshold_RestartsSidecar()
    {
        var (sut, runner, _) = Make();

        await FaultAsync(sut, UniVoicesFaultRecovery.FaultThreshold);

        await runner.Received(1).RestartDanishAsync(Arg.Any<CancellationToken>());
        Assert.Equal(0, sut.ConsecutiveFaults); // streak cleared after the restart
    }

    [Fact]
    public async Task ClientErrors_DoNotCountTowardRestart()
    {
        var (sut, runner, _) = Make();

        // A 4xx means we asked for an unknown engine/voice — restarting would hide that.
        await FaultAsync(sut, UniVoicesFaultRecovery.FaultThreshold * 2, statusCode: 400);

        await runner.DidNotReceive().RestartDanishAsync(Arg.Any<CancellationToken>());
        Assert.Equal(0, sut.ConsecutiveFaults);
    }

    [Fact]
    public async Task Success_ResetsStreak()
    {
        var (sut, runner, _) = Make();

        await FaultAsync(sut, UniVoicesFaultRecovery.FaultThreshold - 1);
        sut.OnSynthesisSuccess("kokoro");
        Assert.Equal(0, sut.ConsecutiveFaults);

        // One more fault must not tip the (now reset) streak over the threshold.
        await FaultAsync(sut, 1);

        await runner.DidNotReceive().RestartDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SecondFaultBurst_WithinCooldown_IsSuppressed()
    {
        var (sut, runner, clock) = Make();

        await FaultAsync(sut, UniVoicesFaultRecovery.FaultThreshold);
        await runner.Received(1).RestartDanishAsync(Arg.Any<CancellationToken>());

        clock.Advance(UniVoicesFaultRecovery.RestartCooldown - TimeSpan.FromSeconds(1));
        await FaultAsync(sut, UniVoicesFaultRecovery.FaultThreshold);

        await runner.Received(1).RestartDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SecondFaultBurst_AfterCooldown_RestartsAgain()
    {
        var (sut, runner, clock) = Make();

        await FaultAsync(sut, UniVoicesFaultRecovery.FaultThreshold);

        clock.Advance(UniVoicesFaultRecovery.RestartCooldown + TimeSpan.FromSeconds(1));
        await FaultAsync(sut, UniVoicesFaultRecovery.FaultThreshold);

        await runner.Received(2).RestartDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestartFails_ReportsFailureAndStillHonoursCooldown()
    {
        var (sut, runner, clock) = Make(restartSucceeds: false);

        await FaultAsync(sut, UniVoicesFaultRecovery.FaultThreshold);
        await runner.Received(1).RestartDanishAsync(Arg.Any<CancellationToken>());

        clock.Advance(TimeSpan.FromSeconds(1));
        await FaultAsync(sut, UniVoicesFaultRecovery.FaultThreshold);

        // A failed restart still starts the cooldown, so we don't hammer docker.
        await runner.Received(1).RestartDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunnerThrows_DoesNotPropagate()
    {
        var runner = Substitute.For<IComposeCommandRunner>();
        runner.RestartDanishAsync(Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("docker missing"));
        var sut = Sut(runner, new FakeTimeProvider());

        await FaultAsync(sut, UniVoicesFaultRecovery.FaultThreshold);

        Assert.False(await sut.TryRecoverAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TryRecover_WithoutFaults_IsNoOp()
    {
        var (sut, runner, _) = Make();

        Assert.False(await sut.TryRecoverAsync(CancellationToken.None));
        await runner.DidNotReceive().RestartDanishAsync(Arg.Any<CancellationToken>());
    }
}
