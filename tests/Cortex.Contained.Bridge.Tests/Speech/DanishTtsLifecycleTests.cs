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
