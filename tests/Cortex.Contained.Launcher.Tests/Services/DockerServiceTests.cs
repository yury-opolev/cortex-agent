using Cortex.Contained.Common;
using Cortex.Contained.Launcher.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Launcher.Tests.Services;

public sealed class DockerServiceTests
{
    private static readonly IReadOnlyDictionary<string, string> BerlinEnvironment =
        new Dictionary<string, string> { [HostTimeZone.EnvironmentVariable] = "Europe/Berlin" };

    [Fact]
    public async Task StartContainersAsync_PassesTheHostTimeZoneToCompose()
    {
        var runner = Substitute.For<IProcessRunner>();
        runner
            .RunAsync(default!, default!, default, default, default)
            .ReturnsForAnyArgs(new ProcessResult(0, string.Empty, string.Empty));
        var sut = new DockerService(runner, NullLogger<DockerService>.Instance, () => BerlinEnvironment);

        await sut.StartContainersAsync("compose.yml");

        await runner.Received(1).RunAsync(
            "docker",
            Arg.Is<string>(a => a.Contains("up -d", StringComparison.Ordinal)),
            Arg.Any<TimeSpan?>(),
            BerlinEnvironment,
            Arg.Any<CancellationToken>());
    }
}
