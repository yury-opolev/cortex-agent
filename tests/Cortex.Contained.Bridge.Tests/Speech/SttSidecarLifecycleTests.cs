using Cortex.Contained.Bridge.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class SttSidecarLifecycleTests
{
    [Fact]
    public async Task NotRunning_StartsContainer()
    {
        var runner = Substitute.For<ISttComposeRunner>();
        runner.IsSttRunningAsync(Arg.Any<CancellationToken>()).Returns(false);
        var sut = new SttSidecarLifecycle(runner, NullLogger<SttSidecarLifecycle>.Instance);

        await sut.ReconcileAsync(CancellationToken.None);

        await runner.Received(1).StartSttAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AlreadyRunning_NoOp()
    {
        var runner = Substitute.For<ISttComposeRunner>();
        runner.IsSttRunningAsync(Arg.Any<CancellationToken>()).Returns(true);
        var sut = new SttSidecarLifecycle(runner, NullLogger<SttSidecarLifecycle>.Instance);

        await sut.ReconcileAsync(CancellationToken.None);

        await runner.DidNotReceive().StartSttAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_RunnerThrows_DoesNotPropagate()
    {
        var runner = Substitute.For<ISttComposeRunner>();
        runner.IsSttRunningAsync(Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("docker exploded"));
        var sut = new SttSidecarLifecycle(runner, NullLogger<SttSidecarLifecycle>.Instance);

        // Must not throw — reconcile failures are logged, not propagated.
        await sut.ReconcileAsync(CancellationToken.None);
    }
}
