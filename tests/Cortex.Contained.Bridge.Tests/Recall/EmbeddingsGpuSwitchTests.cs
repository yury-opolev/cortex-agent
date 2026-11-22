using System.Text;
using Cortex.Contained.Bridge.Recall;
using Cortex.Contained.Bridge.Speech;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Config.Yaml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Recall;

/// <summary>The Bridge-wide "run embeddings on the GPU" switch: persisted in cortex.yml, converged by recreating the sidecar.</summary>
public sealed class EmbeddingsGpuSwitchTests
{
    private readonly BridgeConfig config = new();
    private readonly IEmbeddingsComposeRunner runner = Substitute.For<IEmbeddingsComposeRunner>();
    private readonly List<string> calls = [];
    private readonly EmbeddingsGpuSwitch sut;
    private bool running = true;
    private bool onGpu;

    public EmbeddingsGpuSwitchTests()
    {
        this.runner.IsEmbeddingsRunningAsync(Arg.Any<CancellationToken>()).Returns(_ => this.running);
        this.runner.IsEmbeddingsOnGpuAsync(Arg.Any<CancellationToken>()).Returns(_ => this.onGpu);
        this.runner.StartEmbeddingsAsync(Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            this.calls.Add($"compose-up gpu={call.ArgAt<bool>(0)} recreate={call.ArgAt<bool>(1)}");
            this.onGpu = call.ArgAt<bool>(0);
            this.running = true;
            return true;
        });
        this.sut = this.NewSwitch(_ => this.calls.Add("persist"));
    }

    private EmbeddingsGpuSwitch NewSwitch(Action<BridgeConfig> persist) => new(
        this.config,
        persist,
        new EmbeddingsSidecarLifecycle(this.runner, () => this.config.Embeddings.Gpu, NullLogger<EmbeddingsSidecarLifecycle>.Instance),
        this.runner);

    [Fact]
    public void Gpu_DefaultsToOff()
    {
        Assert.False(new BridgeConfig().Embeddings.Gpu);
        Assert.False(this.sut.Current.Enabled);
    }

    [Fact]
    public async Task TurnOn_PersistsThenRecreatesTheContainerWithTheGpu()
    {
        this.sut.Set(true);
        await this.sut.ConvergeAsync(CancellationToken.None);

        Assert.Equal(["persist", "compose-up gpu=True recreate=True"], this.calls);
        Assert.True(this.sut.Current.Enabled);
        Assert.Equal("running", this.sut.Current.Sidecar.State);
    }

    [Fact]
    public async Task TurnOff_RecreatesTheContainerOnTheCpu()
    {
        this.config.Embeddings.Gpu = true;
        this.onGpu = true;

        this.sut.Set(false);
        await this.sut.ConvergeAsync(CancellationToken.None);

        Assert.Equal(["persist", "compose-up gpu=False recreate=True"], this.calls);
    }

    [Fact]
    public async Task TurnOn_MemoryIsOff_DoesNotStartTheSidecar()
    {
        this.config.Memory.Enabled = false;
        this.running = false;

        this.sut.Set(true);
        await this.sut.ConvergeAsync(CancellationToken.None);

        Assert.Equal(["persist"], this.calls);
        Assert.Equal("stopped", this.sut.Current.Sidecar.State);
    }

    [Fact]
    public async Task TurnOn_NoNvidiaRuntime_FallsBackToTheCpuAndReportsIt()
    {
        this.runner.StartEmbeddingsAsync(Arg.Is<bool>(g => g), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            this.calls.Add("compose-up gpu=True failed");
            return false;
        });

        this.sut.Set(true);
        await this.sut.ConvergeAsync(CancellationToken.None);

        Assert.Equal(["persist", "compose-up gpu=True failed", "compose-up gpu=False recreate=True"], this.calls);
        Assert.True(this.sut.Current.Enabled);
        Assert.Equal("running", this.sut.Current.Sidecar.State);
        Assert.Equal(EmbeddingsSidecarLifecycle.GpuFallbackProblem, this.sut.Current.Sidecar.Problem);
    }

    [Fact]
    public void Set_PersistFails_RestoresThePreviousSwitchAndRethrows()
    {
        var failing = this.NewSwitch(_ => throw new IOException("disk full"));

        Assert.Throws<IOException>(() => failing.Set(true));

        Assert.False(failing.Current.Enabled);
        Assert.False(this.config.Embeddings.Gpu);
    }

    [Fact]
    public async Task ProbeAsync_RunningOnTheGpu_ReportsModeAndProcessor()
    {
        this.onGpu = true;
        this.runner.GetEmbeddingsProcessorAsync(Arg.Any<CancellationToken>()).Returns("100% GPU");

        var view = await this.sut.ProbeAsync(CancellationToken.None);

        Assert.Equal("gpu", view.Mode);
        Assert.Equal("100% GPU", view.Processor);
    }

    [Fact]
    public async Task ProbeAsync_RunningOnTheCpuWithNoModelLoaded_ReportsCpuAndNoProcessor()
    {
        this.runner.GetEmbeddingsProcessorAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var view = await this.sut.ProbeAsync(CancellationToken.None);

        Assert.Equal("cpu", view.Mode);
        Assert.Null(view.Processor);
    }

    [Fact]
    public async Task ProbeAsync_NotRunning_ReportsNoModeAndDoesNotExec()
    {
        this.running = false;

        var view = await this.sut.ProbeAsync(CancellationToken.None);

        Assert.Null(view.Mode);
        await this.runner.DidNotReceive().GetEmbeddingsProcessorAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProbeAsync_DockerThrows_StillReturnsTheConfiguredSwitch()
    {
        this.runner.IsEmbeddingsRunningAsync(Arg.Any<CancellationToken>()).Returns<bool>(_ => throw new InvalidOperationException("docker exploded"));

        var view = await this.sut.ProbeAsync(CancellationToken.None);

        Assert.False(view.Enabled);
        Assert.Null(view.Mode);
    }

    [Fact]
    public void PersistSettingsToYaml_WritesTheGpuSwitch()
    {
        var path = Path.Combine(Path.GetTempPath(), "cortex-" + Guid.NewGuid().ToString("N") + ".yml");
        try
        {
            Cortex.Contained.Bridge.Setup.BridgeSettingsWriter.PersistSettingsToYaml(new BridgeConfig { Embeddings = { Gpu = true } }, path);

            Assert.Contains("embeddings:" + Environment.NewLine + "  gpu: true", File.ReadAllText(path), StringComparison.Ordinal);
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
    public void AppendEmbeddingsSection_Off_WritesFalse()
    {
        var sb = new StringBuilder();

        EmbeddingsConfigYamlWriter.AppendEmbeddingsSection(sb, new EmbeddingsSettingsConfig());

        Assert.Contains("  gpu: false", sb.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PersistedYaml_LoadedBack_KeepsTheGpuSwitch()
    {
        var path = Path.Combine(Path.GetTempPath(), "cortex-" + Guid.NewGuid().ToString("N") + ".yml");
        try
        {
            Cortex.Contained.Bridge.Setup.BridgeSettingsWriter.PersistSettingsToYaml(new BridgeConfig { Embeddings = { Gpu = true } }, path);

            var loaded = new BridgeConfig();
            new ConfigurationBuilder()
                .AddYamlFile(path, optional: false)
                .Build()
                .Bind(loaded);

            Assert.True(loaded.Embeddings.Gpu);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
