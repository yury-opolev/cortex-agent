using Cortex.Contained.Bridge.Speech;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class DanishTtsLifecycleTests
{
    // uni-voices is the universal TTS engine now, so the sidecar must start
    // regardless of which (if any) language voices are configured. These configs
    // exercise both a Danish/roest-da setup and a non-Danish setup to prove the
    // start decision does not depend on config.
    private static TtsConfig WithDanish() => new()
    {
        Languages = new()
        {
            ["da"] = new LanguageTtsConfig { MaleVoice = "roest-da:nic", FemaleVoice = "roest-da:mic" },
        },
    };

    private static TtsConfig WithoutDanish() => new()
    {
        Languages = new()
        {
            ["en"] = new LanguageTtsConfig { MaleVoice = "kokoro:am_adam", FemaleVoice = "kokoro:af_heart" },
        },
    };

    [Fact]
    public async Task NotRunning_StartsContainer_EvenWithoutDanish()
    {
        var runner = Substitute.For<IComposeCommandRunner>();
        runner.IsDanishRunningAsync(Arg.Any<CancellationToken>()).Returns(false);
        var sut = new DanishTtsLifecycle(runner, NullLogger<DanishTtsLifecycle>.Instance);

        await sut.ReconcileAsync(WithoutDanish(), CancellationToken.None);

        await runner.Received(1).StartDanishAsync(Arg.Any<CancellationToken>());
        await runner.DidNotReceive().StopDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotRunning_StartsContainer_WithDanish()
    {
        var runner = Substitute.For<IComposeCommandRunner>();
        runner.IsDanishRunningAsync(Arg.Any<CancellationToken>()).Returns(false);
        var sut = new DanishTtsLifecycle(runner, NullLogger<DanishTtsLifecycle>.Instance);

        await sut.ReconcileAsync(WithDanish(), CancellationToken.None);

        await runner.Received(1).StartDanishAsync(Arg.Any<CancellationToken>());
        await runner.DidNotReceive().StopDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AlreadyRunning_NoOp()
    {
        var runner = Substitute.For<IComposeCommandRunner>();
        runner.IsDanishRunningAsync(Arg.Any<CancellationToken>()).Returns(true);
        var sut = new DanishTtsLifecycle(runner, NullLogger<DanishTtsLifecycle>.Instance);

        await sut.ReconcileAsync(WithoutDanish(), CancellationToken.None);

        await runner.DidNotReceive().StartDanishAsync(Arg.Any<CancellationToken>());
        await runner.DidNotReceive().StopDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NeverStopsContainer()
    {
        // Even with no TTS voices configured at all, the sidecar must not be
        // stopped — it serves every language and stays up with the Bridge.
        var runner = Substitute.For<IComposeCommandRunner>();
        runner.IsDanishRunningAsync(Arg.Any<CancellationToken>()).Returns(true);
        var sut = new DanishTtsLifecycle(runner, NullLogger<DanishTtsLifecycle>.Instance);

        await sut.ReconcileAsync(new TtsConfig(), CancellationToken.None);

        await runner.DidNotReceive().StopDanishAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_RunnerThrows_DoesNotPropagate()
    {
        var runner = Substitute.For<IComposeCommandRunner>();
        runner.IsDanishRunningAsync(Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("docker exploded"));
        var sut = new DanishTtsLifecycle(runner, NullLogger<DanishTtsLifecycle>.Instance);

        // Must not throw — reconcile failures are logged, not propagated.
        await sut.ReconcileAsync(WithDanish(), CancellationToken.None);
    }
}
