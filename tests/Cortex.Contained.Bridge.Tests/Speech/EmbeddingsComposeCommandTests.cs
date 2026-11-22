using Cortex.Contained.Bridge.Speech;

namespace Cortex.Contained.Bridge.Tests.Speech;

/// <summary>The docker commands behind the embeddings sidecar: base compose file alone (CPU) or plus the GPU override.</summary>
public sealed class EmbeddingsComposeCommandTests
{
    private const string Base = @"C:\c\docker-compose.yml";

    [Fact]
    public void UpArguments_Cpu_UsesOnlyTheBaseFileAndWaitsForHealth()
    {
        var args = DockerComposeCommandRunner.EmbeddingsUpArguments(Base, useGpu: false, recreate: false);

        Assert.Equal(
            $"compose -f \"{Base}\" --profile memory up -d --wait --wait-timeout {DockerComposeCommandRunner.EmbeddingsWaitSeconds} embeddings",
            args);
    }

    [Fact]
    public void UpArguments_Gpu_AddsTheOverrideFileNextToTheBaseFile()
    {
        var args = DockerComposeCommandRunner.EmbeddingsUpArguments(Base, useGpu: true, recreate: false);

        Assert.StartsWith($"compose -f \"{Base}\" -f \"C:\\c\\docker-compose.embeddings-gpu.yml\" --profile memory up -d", args, StringComparison.Ordinal);
        Assert.EndsWith(" embeddings", args, StringComparison.Ordinal);
    }

    [Fact]
    public void UpArguments_Recreate_ForcesANewContainer()
    {
        var args = DockerComposeCommandRunner.EmbeddingsUpArguments(Base, useGpu: false, recreate: true);

        Assert.Contains(" up -d --force-recreate --wait ", args, StringComparison.Ordinal);
    }

    [Fact]
    public void UpArguments_AreScopedToTheEmbeddingsService()
    {
        var args = DockerComposeCommandRunner.EmbeddingsUpArguments(Base, useGpu: true, recreate: true);

        Assert.DoesNotContain("reranker", args, StringComparison.Ordinal);
        Assert.DoesNotContain("--profile reranker", args, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NAME                    ID              SIZE      PROCESSOR    CONTEXT    UNTIL\nqwen3-embedding:0.6b    ac6da0dfba84    1.5 GB    100% GPU     4096       11 hours from now\n", "100% GPU")]
    [InlineData("NAME                    ID              SIZE      PROCESSOR    UNTIL\nqwen3-embedding:0.6b    ac6da0dfba84    1.2 GB    100% CPU     4 minutes from now\n", "100% CPU")]
    [InlineData("NAME                    ID              SIZE      PROCESSOR          CONTEXT    UNTIL\nqwen3-embedding:0.6b    ac6da0dfba84    1.5 GB    48%/52% CPU/GPU    4096       Forever\r\n", "48%/52% CPU/GPU")]
    public void ParseProcessor_ReadsThePROCESSORColumn(string output, string expected)
    {
        Assert.Equal(expected, OllamaPs.ParseProcessor(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("NAME    ID    SIZE    PROCESSOR    CONTEXT    UNTIL\n")]
    [InlineData("something unexpected")]
    public void ParseProcessor_NoLoadedModelOrUnknownFormat_IsNull(string output)
    {
        Assert.Null(OllamaPs.ParseProcessor(output));
    }
}
