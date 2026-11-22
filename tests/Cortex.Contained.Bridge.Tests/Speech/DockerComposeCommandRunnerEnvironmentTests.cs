using Cortex.Contained.Bridge.Speech;
using Cortex.Contained.Common;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class DockerComposeCommandRunnerEnvironmentTests
{
    private static readonly IReadOnlyDictionary<string, string> Berlin =
        new Dictionary<string, string> { [HostTimeZone.EnvironmentVariable] = "Europe/Berlin" };

    [Fact]
    public void BuildEnvironment_NoExtras_CarriesTheHostTimeZone()
    {
        var environment = DockerComposeCommandRunner.BuildEnvironment(null, Berlin);

        Assert.Equal("Europe/Berlin", environment[HostTimeZone.EnvironmentVariable]);
    }

    [Fact]
    public void BuildEnvironment_WithSecret_KeepsBothVariables()
    {
        var extra = new Dictionary<string, string> { [DockerComposeCommandRunner.RerankerKeyVariable] = "k" };

        var environment = DockerComposeCommandRunner.BuildEnvironment(extra, Berlin);

        Assert.Equal("k", environment[DockerComposeCommandRunner.RerankerKeyVariable]);
        Assert.Equal("Europe/Berlin", environment[HostTimeZone.EnvironmentVariable]);
    }

    [Fact]
    public void BuildEnvironment_ExplicitExtraWinsOverTheHostZone()
    {
        var extra = new Dictionary<string, string> { [HostTimeZone.EnvironmentVariable] = "Asia/Tokyo" };

        Assert.Equal("Asia/Tokyo", DockerComposeCommandRunner.BuildEnvironment(extra, Berlin)[HostTimeZone.EnvironmentVariable]);
    }
}
