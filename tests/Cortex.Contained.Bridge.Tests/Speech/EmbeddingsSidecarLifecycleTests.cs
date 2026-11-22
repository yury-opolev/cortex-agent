using Cortex.Contained.Bridge.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class EmbeddingsSidecarLifecycleTests
{
    private static EmbeddingsSidecarLifecycle Sut(IEmbeddingsComposeRunner runner, bool gpu = false) =>
        new(runner, () => gpu, NullLogger<EmbeddingsSidecarLifecycle>.Instance);

    [Fact]
    public async Task Enabled_NotRunning_StartsOnTheCpu()
    {
        var runner = Substitute.For<IEmbeddingsComposeRunner>();
        runner.IsEmbeddingsRunningAsync(Arg.Any<CancellationToken>()).Returns(false);

        await Sut(runner).ReconcileAsync(enabled: true, CancellationToken.None);

        await runner.Received(1).StartEmbeddingsAsync(false, false, Arg.Any<CancellationToken>());
        await runner.DidNotReceive().StopEmbeddingsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enabled_AlreadyRunning_NoOp()
    {
        var runner = Substitute.For<IEmbeddingsComposeRunner>();
        runner.IsEmbeddingsRunningAsync(Arg.Any<CancellationToken>()).Returns(true);

        await Sut(runner).ReconcileAsync(enabled: true, CancellationToken.None);

        await runner.DidNotReceiveWithAnyArgs().StartEmbeddingsAsync(default, default, default);
    }

    [Fact]
    public async Task Disabled_Running_Stops()
    {
        var runner = Substitute.For<IEmbeddingsComposeRunner>();
        runner.IsEmbeddingsRunningAsync(Arg.Any<CancellationToken>()).Returns(true);

        await Sut(runner).ReconcileAsync(enabled: false, CancellationToken.None);

        await runner.Received(1).StopEmbeddingsAsync(Arg.Any<CancellationToken>());
        await runner.DidNotReceiveWithAnyArgs().StartEmbeddingsAsync(default, default, default);
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
        var sut = Sut(runner);

        await sut.ReconcileAsync(enabled: true, CancellationToken.None);

        Assert.Equal("failed", sut.Status.State);
    }

    [Fact]
    public async Task Gpu_NotRunning_StartsWithTheGpuOverride()
    {
        var runner = RunnerWhere(running: false, startSucceeds: true);
        var sut = Sut(runner, gpu: true);

        await sut.ReconcileAsync(enabled: true, CancellationToken.None);

        await runner.Received(1).StartEmbeddingsAsync(true, false, Arg.Any<CancellationToken>());
        await runner.DidNotReceive().StartEmbeddingsAsync(Arg.Is<bool>(g => !g), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        Assert.Equal("running", sut.Status.State);
        Assert.Null(sut.Status.Problem);
    }

    [Fact]
    public async Task Gpu_RunningOnTheCpu_RecreatesOnTheGpu()
    {
        var runner = RunnerWhere(running: true, startSucceeds: true, onGpu: false);

        await Sut(runner, gpu: true).ReconcileAsync(enabled: true, CancellationToken.None);

        await runner.Received(1).StartEmbeddingsAsync(true, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cpu_RunningOnTheGpu_RecreatesWithoutTheOverride()
    {
        var runner = RunnerWhere(running: true, startSucceeds: true, onGpu: true);

        await Sut(runner, gpu: false).ReconcileAsync(enabled: true, CancellationToken.None);

        await runner.Received(1).StartEmbeddingsAsync(false, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Gpu_RunningOnTheGpu_NoOp()
    {
        var runner = RunnerWhere(running: true, startSucceeds: true, onGpu: true);

        await Sut(runner, gpu: true).ReconcileAsync(enabled: true, CancellationToken.None);

        await runner.DidNotReceiveWithAnyArgs().StartEmbeddingsAsync(default, default, default);
    }

    [Fact]
    public async Task Gpu_StartFails_FallsBackToTheCpuAndSaysSo()
    {
        var runner = RunnerWhere(running: false, startSucceeds: true);
        runner.StartEmbeddingsAsync(Arg.Is<bool>(g => g), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(false);
        var sut = Sut(runner, gpu: true);

        await sut.ReconcileAsync(enabled: true, CancellationToken.None);

        await runner.Received(1).StartEmbeddingsAsync(false, true, Arg.Any<CancellationToken>());
        Assert.Equal("running", sut.Status.State);
        Assert.Equal(EmbeddingsSidecarLifecycle.GpuFallbackProblem, sut.Status.Problem);
    }

    [Fact]
    public async Task Gpu_StartFailsAndTheCpuFallbackFailsToo_ReportsFailed()
    {
        var runner = RunnerWhere(running: false, startSucceeds: false);
        var sut = Sut(runner, gpu: true);

        await sut.ReconcileAsync(enabled: true, CancellationToken.None);

        Assert.Equal("failed", sut.Status.State);
        Assert.Equal(EmbeddingsSidecarLifecycle.StartFailedProblem, sut.Status.Problem);
    }

    [Fact]
    public async Task Cpu_StartFails_DoesNotRetryAndReportsFailed()
    {
        var runner = RunnerWhere(running: false, startSucceeds: false);
        var sut = Sut(runner, gpu: false);

        await sut.ReconcileAsync(enabled: true, CancellationToken.None);

        await runner.Received(1).StartEmbeddingsAsync(false, false, Arg.Any<CancellationToken>());
        Assert.Equal("failed", sut.Status.State);
    }

    [Fact]
    public async Task Disabled_RecordsStopped()
    {
        var runner = RunnerWhere(running: true, startSucceeds: true);
        var sut = Sut(runner, gpu: true);

        await sut.ReconcileAsync(enabled: false, CancellationToken.None);

        Assert.Equal("stopped", sut.Status.State);
        await runner.DidNotReceiveWithAnyArgs().StartEmbeddingsAsync(default, default, default);
    }

    private static IEmbeddingsComposeRunner RunnerWhere(bool running, bool startSucceeds, bool onGpu = false)
    {
        var runner = Substitute.For<IEmbeddingsComposeRunner>();
        var isRunning = running;
        runner.IsEmbeddingsRunningAsync(Arg.Any<CancellationToken>()).Returns(_ => isRunning);
        runner.IsEmbeddingsOnGpuAsync(Arg.Any<CancellationToken>()).Returns(onGpu);
        runner.StopEmbeddingsAsync(Arg.Any<CancellationToken>()).Returns(true);
        runner.StartEmbeddingsAsync(Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            isRunning = startSucceeds;
            return startSucceeds;
        });
        return runner;
    }
}
