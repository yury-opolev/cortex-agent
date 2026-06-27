using Cortex.Contained.Bridge.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class EmbeddingsSidecarLifecycleTests
{
    private static EmbeddingsSidecarLifecycle Sut(IEmbeddingsComposeRunner runner) =>
        new(runner, NullLogger<EmbeddingsSidecarLifecycle>.Instance);

    [Fact]
    public async Task Enabled_NotRunning_Starts()
    {
        var runner = Substitute.For<IEmbeddingsComposeRunner>();
        runner.IsEmbeddingsRunningAsync(Arg.Any<CancellationToken>()).Returns(false);

        await Sut(runner).ReconcileAsync(enabled: true, CancellationToken.None);

        await runner.Received(1).StartEmbeddingsAsync(Arg.Any<CancellationToken>());
        await runner.DidNotReceive().StopEmbeddingsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enabled_AlreadyRunning_NoOp()
    {
        var runner = Substitute.For<IEmbeddingsComposeRunner>();
        runner.IsEmbeddingsRunningAsync(Arg.Any<CancellationToken>()).Returns(true);

        await Sut(runner).ReconcileAsync(enabled: true, CancellationToken.None);

        await runner.DidNotReceive().StartEmbeddingsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_Running_Stops()
    {
        var runner = Substitute.For<IEmbeddingsComposeRunner>();
        runner.IsEmbeddingsRunningAsync(Arg.Any<CancellationToken>()).Returns(true);

        await Sut(runner).ReconcileAsync(enabled: false, CancellationToken.None);

        await runner.Received(1).StopEmbeddingsAsync(Arg.Any<CancellationToken>());
        await runner.DidNotReceive().StartEmbeddingsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_NotRunning_NoOp()
    {
        var runner = Substitute.For<IEmbeddingsComposeRunner>();
        runner.IsEmbeddingsRunningAsync(Arg.Any<CancellationToken>()).Returns(false);

        await Sut(runner).ReconcileAsync(enabled: false, CancellationToken.None);

        await runner.DidNotReceive().StopEmbeddingsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_RunnerThrows_DoesNotPropagate()
    {
        var runner = Substitute.For<IEmbeddingsComposeRunner>();
        runner.IsEmbeddingsRunningAsync(Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("docker exploded"));

        await Sut(runner).ReconcileAsync(enabled: true, CancellationToken.None);
    }
}
