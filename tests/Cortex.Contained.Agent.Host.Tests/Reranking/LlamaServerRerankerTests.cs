using System.Net;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Reranking;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary>The llama-server client: request shape, authentication, response validation, exposure guard.</summary>
public sealed class LlamaServerRerankerTests : IDisposable
{
    private readonly StubHandler handler = new();
    private readonly RerankerOptions options = new();
    private string? key = "secret-key";

    public void Dispose() => this.handler.Dispose();

    private LlamaServerReranker Sut()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(LlamaServerReranker.HttpClientName).Returns(_ => new HttpClient(this.handler, disposeHandler: false));
        var monitor = Substitute.For<IOptionsMonitor<RerankerOptions>>();
        monitor.CurrentValue.Returns(this.options);
        return new LlamaServerReranker(factory, monitor, () => this.key);
    }

    [Fact]
    public async Task RerankAsync_PostsJinaRequestWithBearerKey_AndParsesScores()
    {
        this.handler.Respond(HttpStatusCode.OK, """{"model":"m","results":[{"index":1,"relevance_score":0.9},{"index":0,"relevance_score":0.1}]}""");

        var scores = await this.Sut().RerankAsync("night train", ["coffee", "train at 22:10"], CancellationToken.None);

        Assert.Equal([new RerankScore(1, 0.9), new RerankScore(0, 0.1)], scores);
        var request = Assert.Single(this.handler.Requests);
        Assert.Equal("http://reranker:8080/v1/rerank", request.Uri);
        Assert.Equal("Bearer secret-key", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("qwen3-reranker-0.6b", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("night train", body.RootElement.GetProperty("query").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("documents").GetArrayLength());
        Assert.Equal(2, body.RootElement.GetProperty("top_n").GetInt32());
    }

    [Fact]
    public async Task RerankAsync_Unauthorized_ThrowsWithoutText()
    {
        this.handler.Respond(HttpStatusCode.Unauthorized, """{"error":"invalid key"}""");

        var ex = await Assert.ThrowsAsync<RerankerException>(() => this.Sut().RerankAsync("private words", ["secret doc"], CancellationToken.None));

        Assert.Equal("The reranker rejected the API key.", ex.Message);
    }

    [Fact]
    public async Task RerankAsync_IndexOutOfRange_Throws()
    {
        this.handler.Respond(HttpStatusCode.OK, """{"results":[{"index":5,"relevance_score":0.9}]}""");

        await Assert.ThrowsAsync<RerankerException>(() => this.Sut().RerankAsync("q", ["a", "b"], CancellationToken.None));
    }

    [Fact]
    public async Task RerankAsync_NoKey_SendsNothing()
    {
        this.key = null;

        var ex = await Assert.ThrowsAsync<RerankerException>(() => this.Sut().RerankAsync("q", ["a"], CancellationToken.None));

        Assert.Contains("API key", ex.Message, StringComparison.Ordinal);
        Assert.Empty(this.handler.Requests);
    }

    [Fact]
    public async Task RerankAsync_EndpointChangedToRemotePlainHttp_SendsNothing()
    {
        var sut = this.Sut();
        this.options.Endpoint = "http://192.168.1.20:8080";

        await Assert.ThrowsAsync<RerankerException>(() => sut.RerankAsync("q", ["a"], CancellationToken.None));

        Assert.Empty(this.handler.Requests);
        Assert.NotNull(sut.UnavailableReason);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode status = HttpStatusCode.OK;
        private string body = "{}";

        public List<(string Uri, string? Authorization, string Body)> Requests { get; } = [];

        public void Respond(HttpStatusCode code, string json)
        {
            this.status = code;
            this.body = json;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            this.Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), content));
            return new HttpResponseMessage(this.status) { Content = new StringContent(this.body, Encoding.UTF8, "application/json") };
        }
    }
}
