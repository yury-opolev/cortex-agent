using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation.Smoke;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>The live embedder check's logic, against a fake service (the live run is <c>history-smoke embedder-check</c>).</summary>
public sealed class EmbedderCheckTests
{
    private static MemoryMcpOptions Memory(string endpoint = "http://embeddings:11434") =>
        new() { Ollama = new OllamaOptions { Endpoint = endpoint, Dimensions = 4, NumCtx = 4096 } };

    [Fact]
    public async Task RunAsync_HealthyEmbedder_AllPass()
    {
        var result = await EmbedderCheck.RunAsync(new FakeService(maxChars: 10_000), Memory(), new HistoryOptions(), CancellationToken.None);

        Assert.True(result.Passed, string.Join('\n', result.Lines));
        Assert.Contains(result.Lines, l => l.Contains("fails instead of being truncated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_SilentTruncation_Fails()
    {
        var result = await EmbedderCheck.RunAsync(new FakeService(maxChars: int.MaxValue), Memory(), new HistoryOptions(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains(result.Lines, l => l.StartsWith("FAIL an input far beyond num_ctx", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_MaxChunkDoesNotFit_FailsWithAdvice()
    {
        var result = await EmbedderCheck.RunAsync(new FakeService(maxChars: 100), Memory(), new HistoryOptions(), CancellationToken.None);

        Assert.Contains(result.Lines, l => l.StartsWith("FAIL", StringComparison.Ordinal) && l.Contains("MaxChunkChars", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_EndpointNotAllowed_StopsBeforeSendingText()
    {
        var service = new FakeService(maxChars: 10_000);

        var result = await EmbedderCheck.RunAsync(service, Memory("http://198.51.100.7:11434"), new HistoryOptions(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public void Synthetic_ExactLengthAndDeterministic()
    {
        Assert.Equal(3000, EmbedderCheck.Synthetic(3000).Length);
        Assert.Equal(EmbedderCheck.Synthetic(500), EmbedderCheck.Synthetic(500));
    }

    private sealed class FakeService(int maxChars) : IEmbeddingService
    {
        public int Calls { get; private set; }

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            this.Calls++;
            return Task.FromResult<float[]>([1, 0, 0, 0]);
        }

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
        {
            this.Calls++;
            return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[] { 1, 0, 0, 0 }).ToList());
        }

        public Task<IReadOnlyList<float[]>> EmbedBatchStrictAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
        {
            this.Calls++;
            var list = texts.ToList();
            return list.Any(t => t.Length > maxChars)
                ? throw new EmbeddingInputTooLongException("too long")
                : Task.FromResult<IReadOnlyList<float[]>>(list.Select(_ => new float[] { 1, 0, 0, 0 }).ToList());
        }
    }
}
