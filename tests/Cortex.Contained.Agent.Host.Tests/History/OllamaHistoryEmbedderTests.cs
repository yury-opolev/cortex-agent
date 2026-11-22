using Cortex.Contained.Agent.Host.History;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.History;

public sealed class OllamaHistoryEmbedderTests
{
    private readonly RecordingEmbeddingService service = new();
    private readonly MemoryMcpOptions memory = new() { Ollama = new OllamaOptions { Endpoint = "http://embeddings:11434", Model = "qwen3-embedding:0.6b", Dimensions = 3, NumCtx = 2048 } };
    private readonly HistoryOptions history = new();

    private OllamaHistoryEmbedder Embedder()
    {
        var memoryMonitor = Substitute.For<IOptionsMonitor<MemoryMcpOptions>>();
        memoryMonitor.CurrentValue.Returns(_ => this.memory);
        var historyMonitor = Substitute.For<IOptionsMonitor<HistoryOptions>>();
        historyMonitor.CurrentValue.Returns(_ => this.history);
        return new OllamaHistoryEmbedder(this.service, memoryMonitor, historyMonitor);
    }

    [Theory]
    [InlineData("http://embeddings:11434", null, true)]
    [InlineData("http://EMBEDDINGS:11434", null, true)]
    [InlineData("http://gpu-box:11434", null, false)]
    [InlineData("http://gpu-box:11435", "key", false)]
    [InlineData("https://gpu-box:11435", "key", true)]
    [InlineData("https://gpu-box:11435", null, false)]
    [InlineData("not a url", null, false)]
    public void Guard_AllowsSidecarOrAuthenticatedHttps(string endpoint, string? apiKey, bool allowed)
    {
        Assert.Equal(allowed, HistoryEmbedderGuard.Check(endpoint, apiKey, ["embeddings"]) is null);
    }

    [Fact]
    public void Fingerprint_CoversModelDimensionsAndContext()
    {
        Assert.Equal("qwen3-embedding:0.6b;dims=3;ctx=2048", this.Embedder().Fingerprint);
    }

    [Fact]
    public async Task EmbedQueryAsync_InstructionOnOrOff()
    {
        var embedder = this.Embedder();

        await embedder.EmbedQueryAsync("train", CancellationToken.None);
        this.history.Dense.UseQueryInstruction = false;
        await embedder.EmbedQueryAsync("train", CancellationToken.None);

        Assert.Equal("Instruct: " + this.history.Dense.QueryInstruction + "\nQuery:train", this.service.Texts[0]);
        Assert.Equal("train", this.service.Texts[1]);
    }

    [Fact]
    public async Task Embed_EndpointNotAllowed_NeverSendsText()
    {
        this.memory.Ollama.Endpoint = "http://203.0.113.5:11434";
        var embedder = this.Embedder();

        Assert.NotNull(embedder.UnavailableReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => embedder.EmbedDocumentsAsync(["secret"], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => embedder.EmbedQueryAsync("secret", CancellationToken.None));
        Assert.Empty(this.service.Texts);
    }

    [Fact]
    public async Task EmbedDocumentsAsync_WrongDimensions_Throws()
    {
        this.memory.Ollama.Dimensions = 4;

        await Assert.ThrowsAsync<InvalidOperationException>(() => this.Embedder().EmbedDocumentsAsync(["a"], CancellationToken.None));
    }

    [Fact]
    public async Task EmbedDocumentsAsync_RequestsNoTruncation()
    {
        await this.Embedder().EmbedDocumentsAsync(["chunk"], CancellationToken.None);

        Assert.Equal(1, this.service.StrictBatchCalls);
        Assert.Equal(0, this.service.TruncatingBatchCalls);
    }

    private sealed class RecordingEmbeddingService : IEmbeddingService
    {
        public List<string> Texts { get; } = [];

        public int StrictBatchCalls { get; private set; }

        public int TruncatingBatchCalls { get; private set; }

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            this.Texts.Add(text);
            return Task.FromResult<float[]>([1f, 0f, 0f]);
        }

        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
        {
            this.TruncatingBatchCalls++;
            return await this.EmbedAllAsync(texts, cancellationToken);
        }

        public async Task<IReadOnlyList<float[]>> EmbedBatchStrictAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
        {
            this.StrictBatchCalls++;
            return await this.EmbedAllAsync(texts, cancellationToken);
        }

        private async Task<IReadOnlyList<float[]>> EmbedAllAsync(IEnumerable<string> texts, CancellationToken cancellationToken)
        {
            var vectors = new List<float[]>();
            foreach (var text in texts)
            {
                vectors.Add(await this.EmbedAsync(text, cancellationToken));
            }

            return vectors;
        }
    }
}
