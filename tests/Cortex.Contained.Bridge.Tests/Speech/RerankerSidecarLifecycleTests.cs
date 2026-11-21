using Cortex.Contained.Bridge.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class RerankerSidecarLifecycleTests
{
    private readonly IRerankerComposeRunner runner = Substitute.For<IRerankerComposeRunner>();

    private RerankerSidecarLifecycle Sut(Func<string>? key = null) =>
        new(this.runner, key ?? (() => "k1"), NullLogger<RerankerSidecarLifecycle>.Instance);

    [Fact]
    public async Task Enabled_AlwaysComposesUpWithTheKey_EvenWhenRunning()
    {
        this.runner.IsRerankerRunningAsync(Arg.Any<CancellationToken>()).Returns(true);

        await this.Sut().ReconcileAsync(enabled: true, CancellationToken.None);

        await this.runner.Received(1).StartRerankerAsync("k1", Arg.Any<CancellationToken>());
        await this.runner.DidNotReceive().StopRerankerAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_Running_Stops()
    {
        this.runner.IsRerankerRunningAsync(Arg.Any<CancellationToken>()).Returns(true);

        await this.Sut().ReconcileAsync(enabled: false, CancellationToken.None);

        await this.runner.Received(1).StopRerankerAsync(Arg.Any<CancellationToken>());
        await this.runner.DidNotReceive().StartRerankerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_NotRunning_NoOp()
    {
        await this.Sut().ReconcileAsync(enabled: false, CancellationToken.None);

        await this.runner.DidNotReceive().StopRerankerAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KeyUnavailable_DoesNotPropagate_AndDoesNotStart()
    {
        await this.Sut(() => throw new InvalidOperationException("dpapi")).ReconcileAsync(enabled: true, CancellationToken.None);

        await this.runner.DidNotReceive().StartRerankerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
