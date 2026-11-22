using YamlDotNet.RepresentationModel;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>Embedder exposure (§4.2): the sidecar that receives history text publishes no port and logs no bodies.</summary>
public sealed class ComposeExposureTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "cortex-contained.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private static YamlMappingNode Service(string name)
    {
        var stream = new YamlStream();
        using (var reader = new StreamReader(Path.Combine(RepositoryRoot(), "docker-compose.yml")))
        {
            stream.Load(reader);
        }

        var services = (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode).Children[new YamlScalarNode("services")];
        return (YamlMappingNode)services.Children[new YamlScalarNode(name)];
    }

    private static List<string> Environment(YamlMappingNode service) =>
        ((YamlSequenceNode)service.Children[new YamlScalarNode("environment")]).Children.Select(n => ((YamlScalarNode)n).Value!).ToList();

    [Fact]
    public void Embeddings_PublishesNoPort_AndDebugLoggingIsOff()
    {
        var embeddings = Service("embeddings");

        Assert.False(embeddings.Children.ContainsKey(new YamlScalarNode("ports")));
        Assert.Contains("OLLAMA_DEBUG=0", Environment(embeddings));
        Assert.Contains("OLLAMA_FLASH_ATTENTION=1", Environment(embeddings));
    }

    [Fact]
    public void Embeddings_BaseService_ReservesNoGpu_SoHostsWithoutOneStillStart()
    {
        var embeddings = Service("embeddings");
        var deploy = (YamlMappingNode)embeddings.Children[new YamlScalarNode("deploy")];
        var resources = (YamlMappingNode)deploy.Children[new YamlScalarNode("resources")];

        Assert.False(resources.Children.ContainsKey(new YamlScalarNode("reservations")));
    }

    [Fact]
    public void EmbeddingsGpuOverride_OnlyAddsTheNvidiaDeviceReservationAndTheGpuOverheadToEmbeddings()
    {
        var stream = new YamlStream();
        using (var reader = new StreamReader(Path.Combine(RepositoryRoot(), "docker-compose.embeddings-gpu.yml")))
        {
            stream.Load(reader);
        }

        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        Assert.Equal(["services"], root.Children.Keys.Select(k => ((YamlScalarNode)k).Value!).ToList());
        var services = (YamlMappingNode)root.Children[new YamlScalarNode("services")];
        Assert.Equal(["embeddings"], services.Children.Keys.Select(k => ((YamlScalarNode)k).Value!).ToList());

        var service = (YamlMappingNode)services.Children[new YamlScalarNode("embeddings")];
        Assert.Equal(["environment", "deploy"], service.Children.Keys.Select(k => ((YamlScalarNode)k).Value!).ToList());
        Assert.Equal(["OLLAMA_GPU_OVERHEAD=${CORTEX_EMBEDDINGS_GPU_OVERHEAD:-536870912}"], Environment(service));
        var resources = (YamlMappingNode)((YamlMappingNode)service.Children[new YamlScalarNode("deploy")]).Children[new YamlScalarNode("resources")];
        var reservations = (YamlMappingNode)resources.Children[new YamlScalarNode("reservations")];
        var device = (YamlMappingNode)((YamlSequenceNode)reservations.Children[new YamlScalarNode("devices")]).Children.Single();

        Assert.Equal("nvidia", ((YamlScalarNode)device.Children[new YamlScalarNode("driver")]).Value);
        Assert.Equal("all", ((YamlScalarNode)device.Children[new YamlScalarNode("count")]).Value);
        Assert.Equal(["gpu"], ((YamlSequenceNode)device.Children[new YamlScalarNode("capabilities")]).Children.Select(n => ((YamlScalarNode)n).Value!).ToList());
    }

    [Fact]
    public void LauncherAndBuildScript_BundleTheGpuOverrideNextToTheCompose()
    {
        var root = RepositoryRoot();

        Assert.Contains("docker-compose.embeddings-gpu.yml", File.ReadAllText(Path.Combine(root, "src", "Cortex.Contained.Launcher", "Cortex.Contained.Launcher.csproj")), StringComparison.Ordinal);
        Assert.Contains("docker-compose.embeddings-gpu.yml", File.ReadAllText(Path.Combine(root, "scripts", "Build-Launcher.ps1")), StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_TakesTheHistoryTimeZoneFromTheHostViaCortexTimezone()
    {
        // Empty (the default) keeps TimeZoneInfo.Local, i.e. the container's own zone.
        Assert.Contains("History__TimeZone=${CORTEX_TIMEZONE:-}", Environment(Service("cortex-agent")));
    }

    [Fact]
    public void Agent_UsesTheInComposeSidecar()
    {
        Assert.Contains("MemoryMcp__Ollama__Endpoint=http://embeddings:11434", Environment(Service("cortex-agent")));
    }
}
