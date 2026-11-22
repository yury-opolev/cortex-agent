using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary>
/// Reranker exposure (§4.3): the sidecar that receives history and memory text publishes no port, needs
/// an API key, logs no requests, is never pulled from a registry, and its server and model are pinned.
/// </summary>
public sealed partial class RerankerSidecarExposureTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
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
        using (var reader = new StreamReader(Path.Combine(Root, "docker-compose.yml")))
        {
            stream.Load(reader);
        }

        var services = (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode).Children[new YamlScalarNode("services")];
        return (YamlMappingNode)services.Children[new YamlScalarNode(name)];
    }

    private static string Scalar(YamlMappingNode node, string key) => ((YamlScalarNode)node.Children[new YamlScalarNode(key)]).Value!;

    private static List<string> Sequence(YamlMappingNode node, string key) =>
        ((YamlSequenceNode)node.Children[new YamlScalarNode(key)]).Children.Select(n => ((YamlScalarNode)n).Value!).ToList();

    [Fact]
    public void Reranker_PublishesNoPort_TakesTheKeyFromTheBridge_NeverPulls()
    {
        var reranker = Service("reranker");

        Assert.False(reranker.Children.ContainsKey(new YamlScalarNode("ports")));
        var environment = Sequence(reranker, "environment");
        Assert.Equal("LLAMA_API_KEY=${CORTEX_RERANKER_API_KEY:-}", environment[0]);
        Assert.All(environment.Skip(1), entry => Assert.StartsWith("RERANKER_", entry, StringComparison.Ordinal));
        Assert.Equal(["reranker"], Sequence(reranker, "profiles"));
        Assert.Equal(["cortex-internal"], Sequence(reranker, "networks"));
        Assert.Equal("never", Scalar(reranker, "pull_policy"));
        Assert.Equal("cortex-reranker", Scalar(reranker, "container_name"));
    }

    [Fact]
    public void Agent_UsesTheInComposeReranker()
    {
        Assert.Contains("Reranker__Endpoint=http://reranker:8080", Sequence(Service("cortex-agent"), "environment"));
    }

    [Fact]
    public void Image_PinsEveryServerByDigest_AndTheModelBySha256()
    {
        var dockerfile = File.ReadAllText(Path.Combine(Root, "docker", "reranker", "Dockerfile"));

        var bases = dockerfile.Split('\n').Where(l => l.StartsWith("FROM ghcr.io/", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, bases.Count);
        Assert.All(bases, line => Assert.Matches(PinnedImage(), line));
        Assert.Matches(PinnedModel(), dockerfile);
    }

    [Fact]
    public void Entrypoint_RefusesAnEmptyKey_AndServesRerankingOnlyWithoutRequestLogs()
    {
        var entrypoint = File.ReadAllText(Path.Combine(Root, "docker", "reranker", "entrypoint.sh"));

        Assert.Contains("if [ -z \"${LLAMA_API_KEY:-}\" ]", entrypoint, StringComparison.Ordinal);
        Assert.Contains("exit 64", entrypoint, StringComparison.Ordinal);
        foreach (var flag in new[] { "--reranking", "--no-webui", "--no-slots", "--log-verbosity 2" })
        {
            Assert.Contains(flag, entrypoint, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("--metrics", entrypoint, StringComparison.Ordinal);
        Assert.DoesNotContain("--verbose", entrypoint, StringComparison.Ordinal);
    }

    [Fact]
    public void Entrypoint_SizesTheServerFromTheEnvironment_WithTheLeanDefaults()
    {
        var entrypoint = File.ReadAllText(Path.Combine(Root, "docker", "reranker", "entrypoint.sh"));

        foreach (var flag in new[]
        {
            "--ctx-size \"${RERANKER_CTX_SIZE:-4096}\"",
            "--parallel \"${RERANKER_PARALLEL:-2}\"",
            "--batch-size \"${RERANKER_BATCH_SIZE:-2048}\"",
            "--ubatch-size \"${RERANKER_UBATCH_SIZE:-2048}\"",
            "--flash-attn \"${RERANKER_FLASH_ATTN:-on}\"",
            "--cache-type-k \"${RERANKER_CACHE_TYPE:-q8_0}\"",
            "--cache-type-v \"${RERANKER_CACHE_TYPE:-q8_0}\"",
        })
        {
            Assert.Contains(flag, entrypoint, StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(@"^FROM ghcr\.io/ggml-org/llama\.cpp:server(-cuda)?-b\d+@sha256:[0-9a-f]{64} AS server-(cuda|cpu)$")]
    private static partial Regex PinnedImage();

    [GeneratedRegex(@"ADD --chmod=0555 --checksum=sha256:[0-9a-f]{64} \\\n\s+https://huggingface\.co/[^\s]+/resolve/[0-9a-f]{40}/[^\s]+\.gguf")]
    private static partial Regex PinnedModel();
}
