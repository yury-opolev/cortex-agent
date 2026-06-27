using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Bridge.RemoteServices;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Tests.RemoteServices;

/// <summary>
/// Unit tests for <see cref="EmbeddingProbe"/>. Uses a stub <see cref="HttpMessageHandler"/>
/// so no real network calls are made. The probe path is the valuable testable seam — the
/// route handlers are thin wrappers that call <see cref="EmbeddingProbe.ProbeAsync"/>.
/// </summary>
public sealed class EmbeddingProbeTests
{
    // ── ParseDimension ────────────────────────────────────────────────────────

    [Fact]
    public void ParseDimension_ValidResponse_Returns1024()
    {
        var body = BuildEmbedResponse(1024);

        var dim = EmbeddingProbe.ParseDimension(body);

        Assert.Equal(1024, dim);
    }

    [Fact]
    public void ParseDimension_DifferentDimension_ReturnsActualLength()
    {
        var body = BuildEmbedResponse(8);

        var dim = EmbeddingProbe.ParseDimension(body);

        Assert.Equal(8, dim);
    }

    [Fact]
    public void ParseDimension_EmptyEmbeddings_ReturnsNull()
    {
        var body = """{"embeddings":[]}""";

        var dim = EmbeddingProbe.ParseDimension(body);

        Assert.Null(dim);
    }

    [Fact]
    public void ParseDimension_MalformedJson_ReturnsNull()
    {
        var dim = EmbeddingProbe.ParseDimension("not json at all");

        Assert.Null(dim);
    }

    [Fact]
    public void ParseDimension_MissingEmbeddingsKey_ReturnsNull()
    {
        var body = """{"result":"ok"}""";

        var dim = EmbeddingProbe.ParseDimension(body);

        Assert.Null(dim);
    }

    // ── ProbeAsync — HTTP 200 + correct dimension ─────────────────────────────

    [Fact]
    public async Task ProbeAsync_200With1024Dim_ReturnsOk()
    {
        using var http = MakeClient(HttpStatusCode.OK, BuildEmbedResponse(1024));

        var result = await EmbeddingProbe.ProbeAsync("http://embed:11434", null, http, default);

        Assert.True(result.Ok);
        Assert.Equal(1024, result.Dim);
        Assert.Null(result.Error);
    }

    // ── ProbeAsync — HTTP 200 + wrong dimension ───────────────────────────────

    [Fact]
    public async Task ProbeAsync_200WithDimMismatch_ReturnsDimMismatch()
    {
        using var http = MakeClient(HttpStatusCode.OK, BuildEmbedResponse(8));

        var result = await EmbeddingProbe.ProbeAsync("http://embed:11434", null, http, default);

        Assert.False(result.Ok);
        Assert.Equal(8, result.Dim);
        Assert.Contains("mismatch", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── ProbeAsync — HTTP 401 ─────────────────────────────────────────────────

    [Fact]
    public async Task ProbeAsync_401_ReturnsBadKey()
    {
        using var http = MakeClient(HttpStatusCode.Unauthorized, "Unauthorized");

        var result = await EmbeddingProbe.ProbeAsync("http://embed:11434", "bad-key", http, default);

        Assert.False(result.Ok);
        Assert.Null(result.Dim);
        Assert.Contains("Unauthorized", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── ProbeAsync — HTTP 403 ─────────────────────────────────────────────────

    [Fact]
    public async Task ProbeAsync_403_ReturnsBadKey()
    {
        using var http = MakeClient(HttpStatusCode.Forbidden, "Forbidden");

        var result = await EmbeddingProbe.ProbeAsync("http://embed:11434", "bad-key", http, default);

        Assert.False(result.Ok);
        Assert.Contains("Unauthorized", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── ProbeAsync — connection failure ───────────────────────────────────────

    [Fact]
    public async Task ProbeAsync_ConnectionFailure_ReturnsUnreachable()
    {
        using var http = MakeThrowingClient(new HttpRequestException("Connection refused"));

        var result = await EmbeddingProbe.ProbeAsync("http://embed:11434", null, http, default);

        Assert.False(result.Ok);
        Assert.Null(result.Dim);
        Assert.Contains("unreachable", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── ProbeAsync — timeout treated as unreachable ───────────────────────────

    [Fact]
    public async Task ProbeAsync_TaskCanceled_ReturnsUnreachable()
    {
        using var http = MakeThrowingClient(new TaskCanceledException("Timeout"));

        var result = await EmbeddingProbe.ProbeAsync("http://embed:11434", null, http, default);

        Assert.False(result.Ok);
        Assert.Contains("unreachable", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── ProbeAsync — Authorization header is sent when key is provided ────────

    [Fact]
    public async Task ProbeAsync_WithApiKey_SendsBearerHeader()
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
        using var http = new HttpClient(handler) { BaseAddress = null };

        await EmbeddingProbe.ProbeAsync("http://embed:11434", "secret-key", http, default);

        Assert.Equal("Bearer secret-key", capturedAuth);
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
        using var http = new HttpClient(handler) { BaseAddress = null };

        await EmbeddingProbe.ProbeAsync("http://embed:11434", null, http, default);

        Assert.Null(capturedAuth);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildEmbedResponse(int dim)
    {
        var vector = Enumerable.Range(0, dim).Select(i => 0.1f).ToArray();
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
