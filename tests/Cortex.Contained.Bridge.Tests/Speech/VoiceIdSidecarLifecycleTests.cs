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
