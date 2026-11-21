using YamlDotNet.RepresentationModel;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>Embedder exposure (§4.2): the sidecar that receives history text publishes no port and logs no bodies.</summary>
public sealed class ComposeExposureTests
{
    private static YamlMappingNode Service(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "cortex-contained.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var stream = new YamlStream();
        using (var reader = new StreamReader(Path.Combine(directory.FullName, "docker-compose.yml")))
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
