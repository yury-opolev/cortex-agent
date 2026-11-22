using System.Text;
using Cortex.Contained.Bridge.Recall;
using Cortex.Contained.Bridge.Speech;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Recall;

/// <summary>The Bridge-wide reranker switch: persisted in cortex.yml, converged in a safe order.</summary>
public sealed class RerankerSwitchTests : IDisposable
{
    private readonly BridgeConfig config = new();
    private readonly IRerankerComposeRunner runner = Substitute.For<IRerankerComposeRunner>();
    private readonly List<string> calls = [];
    private readonly RerankerSwitch sut;

    public RerankerSwitchTests()
    {
        this.runner.HasRerankerImageAsync(Arg.Any<CancellationToken>()).Returns(true);
        this.runner.StartRerankerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            this.calls.Add("compose-up");
            return true;
        });
        this.runner.IsRerankerRunningAsync(Arg.Any<CancellationToken>()).Returns(true);
        this.runner.StopRerankerAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            this.calls.Add("compose-stop");
            return true;
        });
        this.sut = new RerankerSwitch(
            this.config,
            _ => this.calls.Add("persist"),
            new RerankerSidecarLifecycle(this.runner, () => "k", NullLogger<RerankerSidecarLifecycle>.Instance),
            _ =>
            {
                this.calls.Add("grant:" + this.config.Reranker.Enabled);
                return Task.CompletedTask;
            },
            NullLogger<RerankerSwitch>.Instance);
    }

    public void Dispose() => this.sut.Dispose();

    [Fact]
    public async Task Enable_StartsTheSidecarBeforeGrantingAgents()
    {
        this.sut.Set(true);
        await this.sut.ConvergeAsync(CancellationToken.None);

        Assert.Equal(["persist", "compose-up", "grant:True"], this.calls);
        Assert.Equal("running", this.sut.Current.Sidecar.State);
    }

    [Fact]
    public async Task Disable_WithdrawsTheGrantBeforeStoppingTheSidecar()
    {
        this.config.Reranker.Enabled = true;

        this.sut.Set(false);
        await this.sut.ConvergeAsync(CancellationToken.None);

        Assert.Equal(["persist", "grant:False", "compose-stop"], this.calls);
        Assert.False(this.sut.Current.Enabled);
    }

    [Fact]
    public async Task Enable_SidecarDoesNotComeUp_DoesNotGrantAgents()
    {
        this.runner.StartRerankerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            this.calls.Add("compose-up");
            return false;
        });

        this.sut.Set(true);
        await this.sut.ConvergeAsync(CancellationToken.None);

        Assert.Equal(["persist", "compose-up"], this.calls);
        Assert.Equal("failed", this.sut.Current.Sidecar.State);
    }

    [Fact]
    public void Set_PersistFails_RestoresThePreviousSwitchAndRethrows()
    {
        var failing = new RerankerSwitch(
            this.config,
            _ => throw new IOException("disk full"),
            new RerankerSidecarLifecycle(this.runner, () => "k", NullLogger<RerankerSidecarLifecycle>.Instance),
            _ => Task.CompletedTask,
            NullLogger<RerankerSwitch>.Instance);

        Assert.Throws<IOException>(() => failing.Set(true));

        Assert.False(failing.Current.Enabled);
        failing.Dispose();
    }

    [Fact]
    public void RerankerUpArguments_WaitForTheContainerToBeHealthy()
    {
        var args = DockerComposeCommandRunner.RerankerUpArguments(@"C:\c\docker-compose.yml");

        Assert.Contains("up -d --wait --wait-timeout " + DockerComposeCommandRunner.RerankerWaitSeconds, args, StringComparison.Ordinal);
        Assert.EndsWith("reranker", args, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistSettingsToYaml_WritesTheRerankerSwitch()
    {
        var path = Path.Combine(Path.GetTempPath(), "cortex-" + Guid.NewGuid().ToString("N") + ".yml");
        try
        {
            Cortex.Contained.Bridge.Setup.BridgeSettingsWriter.PersistSettingsToYaml(new BridgeConfig { Reranker = { Enabled = true } }, path);

            Assert.Contains("reranker:" + Environment.NewLine + "  enabled: true", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void AppendRerankerSection_Disabled_WritesFalse()
    {
        var sb = new StringBuilder();

        RerankerConfigYamlWriter.AppendRerankerSection(sb, new RerankerSettingsConfig());

        Assert.Contains("  enabled: false", sb.ToString(), StringComparison.Ordinal);
    }
}
