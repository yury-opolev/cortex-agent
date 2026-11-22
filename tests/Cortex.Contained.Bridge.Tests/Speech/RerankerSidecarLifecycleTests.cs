using Cortex.Contained.Bridge.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class RerankerSidecarLifecycleTests
{
    private readonly IRerankerComposeRunner runner = Substitute.For<IRerankerComposeRunner>();

    public RerankerSidecarLifecycleTests()
    {
        this.runner.HasRerankerImageAsync(Arg.Any<CancellationToken>()).Returns(true);
        this.runner.StartRerankerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
    }

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

    [Fact]
    public async Task Enabled_ImageMissing_ReportsBuildHintAndDoesNotStart()
    {
        this.runner.HasRerankerImageAsync(Arg.Any<CancellationToken>()).Returns(false);
        var sut = this.Sut();

        var status = await sut.ReconcileAsync(enabled: true, CancellationToken.None);

        Assert.Equal("failed", status.State);
        Assert.Equal(RerankerSidecarLifecycle.MissingImageProblem, status.Problem);
        Assert.Same(status, sut.Status);
        await this.runner.DidNotReceive().StartRerankerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enabled_ComposeFails_ReportsGpuHint()
    {
        this.runner.StartRerankerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var status = await this.Sut().ReconcileAsync(enabled: true, CancellationToken.None);

        Assert.Equal("failed", status.State);
        Assert.Contains("NVIDIA GPU", status.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_StartedButNotRunning_ReportsExit()
    {
        var status = await this.Sut().ReconcileAsync(enabled: true, CancellationToken.None);

        Assert.Equal(RerankerSidecarLifecycle.NotRunningProblem, status.Problem);
    }

    [Fact]
    public async Task Enabled_Running_ReportsRunning()
    {
        this.runner.IsRerankerRunningAsync(Arg.Any<CancellationToken>()).Returns(true);

        var status = await this.Sut().ReconcileAsync(enabled: true, CancellationToken.None);

        Assert.Equal(new RerankerSidecarStatus("running", null, status.At), status);
    }

    [Fact]
    public async Task Disabled_ReportsStopped()
    {
        Assert.Equal("stopped", (await this.Sut().ReconcileAsync(enabled: false, CancellationToken.None)).State);
    }
}
