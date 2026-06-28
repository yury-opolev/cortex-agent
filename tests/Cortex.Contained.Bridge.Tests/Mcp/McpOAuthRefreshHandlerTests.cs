using System.Net;
using Cortex.Contained.Bridge.Mcp.Auth;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpOAuthRefreshHandlerTests
{
    private sealed class RecordingInnerHandler : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> statuses;

        public RecordingInnerHandler(params HttpStatusCode[] statuses)
            => this.statuses = new Queue<HttpStatusCode>(statuses);

        public List<string?> SeenAuthorization { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.SeenAuthorization.Add(request.Headers.Authorization?.ToString());
            var status = this.statuses.Count > 0 ? this.statuses.Dequeue() : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class FakeBearerSource : IMcpBearerSource
    {
        public string? Current { get; set; } = "old-token";

        public string? Refreshed { get; set; } = "new-token";

        public int RefreshCalls { get; private set; }

        public Task<string?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(this.Current);

        public Task<string?> RefreshAsync(CancellationToken cancellationToken)
        {
            this.RefreshCalls++;
            return Task.FromResult(this.Refreshed);
        }
    }

    private static HttpClient Build(HttpMessageHandler inner, IMcpBearerSource source)
        => new(new McpOAuthRefreshHandler(source) { InnerHandler = inner });

    [Fact]
    public async Task SendAsync_AttachesBearerFromSource()
    {
        var inner = new RecordingInnerHandler(HttpStatusCode.OK);
        using var client = Build(inner, new FakeBearerSource { Current = "tok-1" });

        using var response = await client.GetAsync(new Uri("https://mcp.example.com/mcp"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer tok-1", inner.SeenAuthorization[0]);
    }

    [Fact]
    public async Task SendAsync_On401_RefreshesAndRetriesWithNewBearer()
    {
        var inner = new RecordingInnerHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        var source = new FakeBearerSource { Current = "old-token", Refreshed = "new-token" };
        using var client = Build(inner, source);

        using var response = await client.GetAsync(new Uri("https://mcp.example.com/mcp"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, source.RefreshCalls);
        Assert.Equal("Bearer old-token", inner.SeenAuthorization[0]);
        Assert.Equal("Bearer new-token", inner.SeenAuthorization[1]);
    }

    [Fact]
    public async Task SendAsync_On401_RefreshReturnsNull_DoesNotRetry()
    {
        var inner = new RecordingInnerHandler(HttpStatusCode.Unauthorized);
        var source = new FakeBearerSource { Current = "old-token", Refreshed = null };
        using var client = Build(inner, source);

        using var response = await client.GetAsync(new Uri("https://mcp.example.com/mcp"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(inner.SeenAuthorization);
    }

    [Fact]
    public async Task SendAsync_PostWith401_RetriesWithBufferedBody()
    {
        var inner = new RecordingInnerHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        var source = new FakeBearerSource();
        using var client = Build(inner, source);

        using var content = new StringContent("{\"jsonrpc\":\"2.0\"}");
        using var response = await client.PostAsync(new Uri("https://mcp.example.com/mcp"), content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.SeenAuthorization.Count);
    }
}
