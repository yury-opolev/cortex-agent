using System.Diagnostics;
using Cortex.Contained.Launcher.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Launcher.Tests.Services;

public sealed class BridgeProcessServiceTests : IDisposable
{
    private readonly IProcessRunner processRunner = Substitute.For<IProcessRunner>();
    private readonly BridgeProcessService sut;

    public BridgeProcessServiceTests()
    {
        this.sut = new BridgeProcessService(
            this.processRunner,
            NullLogger<BridgeProcessService>.Instance);
    }

    public void Dispose()
    {
        this.sut.Dispose();
    }

    [Fact]
    public void IsRunning_BeforeStart_ReturnsFalse()
    {
        Assert.False(this.sut.IsRunning);
    }

    [Fact]
    public void Start_SpawnsBackgroundProcess()
    {
        var mockProcess = Substitute.For<Process>();
        this.processRunner
            .StartBackground(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Dictionary<string, string>?>())
            .Returns(mockProcess);

        this.sut.Start(@"C:\App\Bridge\Cortex.Contained.Bridge.exe", "dev-token");

        this.processRunner.Received(1).StartBackground(
            @"C:\App\Bridge\Cortex.Contained.Bridge.exe",
            Arg.Any<string>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<Dictionary<string, string>?>());
    }

    [Fact]
    public void OnExited_CarriesExitCode_ForRestartDetection()
    {
        // The Launcher needs to know the exit code so it can distinguish a
        // Web-UI-initiated restart (code 73 → respawn) from a normal stop
        // or crash (any other code → Error state). Pin that contract.
        var captured = int.MinValue;
        this.sut.OnExited += code => captured = code;

        this.sut.RaiseExitedForTesting(73);

        Assert.Equal(73, captured);
    }

    [Fact]
    public void OnExited_OtherExitCodes_PassedThrough()
    {
        var captured = int.MinValue;
        this.sut.OnExited += code => captured = code;

        this.sut.RaiseExitedForTesting(0);
        Assert.Equal(0, captured);

        this.sut.RaiseExitedForTesting(-1);
        Assert.Equal(-1, captured);

        this.sut.RaiseExitedForTesting(255);
        Assert.Equal(255, captured);
    }

    [Fact]
    public void Start_PassesHubTokenViaEnvironmentVariables()
    {
        var mockProcess = Substitute.For<Process>();
        this.processRunner
            .StartBackground(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Dictionary<string, string>?>())
            .Returns(mockProcess);

        this.sut.Start(@"C:\App\Bridge.exe", "my-secret-token");

        this.processRunner.Received(1).StartBackground(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<Action<string>?>(),
            Arg.Is<Dictionary<string, string>?>(d => d != null && d["CORTEX_HUB_TOKEN"] == "my-secret-token"));
    }
}
