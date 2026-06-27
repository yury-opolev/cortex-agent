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
