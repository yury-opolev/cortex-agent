using System.Net;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Agent.Host.History.Evaluation.Smoke;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary><c>history-smoke reranker-check</c> against a stubbed llama-server (no sidecar).</summary>
public sealed class RerankerCheckTests : IDisposable
{
    private readonly StubServer server = new();
    private readonly HttpClient http;
    private readonly RerankerOptions options = new();

    public RerankerCheckTests()
    {
        this.http = new HttpClient(this.server);
        Environment.SetEnvironmentVariable("Reranker__ApiKey", "check-key");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("Reranker__ApiKey", null);
        this.http.Dispose();
        this.server.Dispose();
    }

    private Task<RerankerCheckResult> RunAsync(string? key = "check-key") =>
        RerankerCheck.RunAsync(RerankEvalSetup.LiveReranker(this.options, this.http), this.options, key, this.http, TimeSpan.FromSeconds(5), CancellationToken.None);

    [Fact]
    public async Task HealthySidecar_AllChecksPass()
    {
        var result = await this.RunAsync();

        Assert.True(result.Passed, string.Join('\n', result.Lines));
        Assert.Contains(result.Lines, l => l.StartsWith("PASS a request without the key is refused", StringComparison.Ordinal));
        Assert.Contains(result.Lines, l => l.StartsWith("PASS top30-chars2000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SidecarWithoutAuthentication_Fails()
    {
        this.server.EnforceKey = false;

        var result = await this.RunAsync();

        Assert.False(result.Passed);
        Assert.Contains(result.Lines, l => l.StartsWith("FAIL a request without the key is refused (HTTP 200", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoKey_StopsBeforeSendingAnything()
    {
        var result = await this.RunAsync(key: null);

        Assert.False(result.Passed);
        Assert.Single(result.Lines);
        Assert.Empty(this.server.Paths);
    }

    private sealed class StubServer : HttpMessageHandler
    {
        public bool EnforceKey { get; set; } = true;

        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            this.Paths.Add(path);
            if (path == "/health")
            {
                return Json(HttpStatusCode.OK, """{"status":"ok"}""");
            }

            if (this.EnforceKey && request.Headers.Authorization?.Parameter != "check-key")
            {
                return Json(HttpStatusCode.Unauthorized, """{"error":"Invalid API Key"}""");
            }

            if (path == "/slots")
            {
                return Json(HttpStatusCode.NotImplemented, "{}");
            }

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var documents = body.RootElement.GetProperty("documents").EnumerateArray().Select(d => d.GetString()!).ToList();
            var results = documents.Select((d, i) => $$"""{"index":{{i}},"relevance_score":{{(d.Contains("night train", StringComparison.Ordinal) ? "0.99" : "0.01")}}}""");
            return Json(HttpStatusCode.OK, $$"""{"results":[{{string.Join(',', results)}}]}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
