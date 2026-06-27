using System.Net;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Hubs;

namespace Cortex.Contained.Agent.Host.Tests.Hubs;

/// <summary>
/// Unit tests for <see cref="EmbeddingEndpointProber"/>. Uses a stub
/// <see cref="HttpMessageHandler"/> so no real network calls are made. The prober runs
/// on the agent (Docker network) and maps results via the shared
/// <c>EmbeddingProbeMapper</c>, so these tests assert the result mapping end-to-end.
/// </summary>
public sealed class EmbeddingEndpointProberTests
{
    private const string Model = "qwen3-embedding:0.6b";
    private const int Dim = 1024;

    [Fact]
    public async Task ProbeAsync_200With1024Dim_ReturnsOk()
    {
        using var http = MakeClient(HttpStatusCode.OK, BuildEmbedResponse(1024));

        var result = await EmbeddingEndpointProber.ProbeAsync(
            "http://embeddings:11434", null, Model, Dim, http, default);

        Assert.True(result.Ok);
        Assert.Equal(1024, result.Dim);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ProbeAsync_200WithDimMismatch_ReturnsDimMismatch()
    {
        using var http = MakeClient(HttpStatusCode.OK, BuildEmbedResponse(8));

        var result = await EmbeddingEndpointProber.ProbeAsync(
            "http://embeddings:11434", null, Model, Dim, http, default);

        Assert.False(result.Ok);
        Assert.Equal(8, result.Dim);
        Assert.Contains("mismatch", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_401_ReturnsUnauthorized()
    {
        using var http = MakeClient(HttpStatusCode.Unauthorized, "Unauthorized");

        var result = await EmbeddingEndpointProber.ProbeAsync(
            "http://emb-auth:11435", "bad-key", Model, Dim, http, default);

        Assert.False(result.Ok);
        Assert.Null(result.Dim);
        Assert.Contains("Unauthorized", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_403_ReturnsUnauthorized()
    {
        using var http = MakeClient(HttpStatusCode.Forbidden, "Forbidden");

        var result = await EmbeddingEndpointProber.ProbeAsync(
            "http://emb-auth:11435", "bad-key", Model, Dim, http, default);

        Assert.False(result.Ok);
        Assert.Contains("Unauthorized", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_ConnectionFailure_ReturnsUnreachable()
    {
        using var http = MakeThrowingClient(new HttpRequestException("Connection refused"));

        var result = await EmbeddingEndpointProber.ProbeAsync(
            "http://embeddings:11434", null, Model, Dim, http, default);

        Assert.False(result.Ok);
        Assert.Null(result.Dim);
        Assert.Contains("unreachable", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_TaskCanceled_ReturnsUnreachable()
    {
        using var http = MakeThrowingClient(new TaskCanceledException("Timeout"));

        var result = await EmbeddingEndpointProber.ProbeAsync(
            "http://embeddings:11434", null, Model, Dim, http, default);

        Assert.False(result.Ok);
        Assert.Contains("unreachable", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_WithApiKey_SendsBearerHeaderAndProbesEmbedPath()
    {
        string? capturedAuth = null;
        string? capturedPath = null;
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(async (req, ct) =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            capturedPath = req.RequestUri?.AbsolutePath;
            capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildEmbedResponse(1024), Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler);

        await EmbeddingEndpointProber.ProbeAsync(
            "http://emb-auth:11435/", "secret-key", Model, Dim, http, default);

        Assert.Equal("Bearer secret-key", capturedAuth);
        Assert.Equal("/api/embed", capturedPath);
        Assert.NotNull(capturedBody);
        Assert.Contains(Model, capturedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAsync_WithoutApiKey_DoesNotSendAuthHeader()
    {
        string? capturedAuth = null;
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildEmbedResponse(1024), Encoding.UTF8, "application/json"),
            });
        });
        using var http = new HttpClient(handler);

        await EmbeddingEndpointProber.ProbeAsync(
            "http://embeddings:11434", null, Model, Dim, http, default);

        Assert.Null(capturedAuth);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildEmbedResponse(int dim)
    {
        var vector = Enumerable.Range(0, dim).Select(_ => 0.1f).ToArray();
        return JsonSerializer.Serialize(new { embeddings = new[] { vector } });
    }

    private static HttpClient MakeClient(HttpStatusCode statusCode, string body)
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }));
        return new HttpClient(handler);
    }

    private static HttpClient MakeThrowingClient(Exception ex)
    {
        var handler = new FakeHttpMessageHandler((_, _) => throw ex);
        return new HttpClient(handler);
    }

    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
