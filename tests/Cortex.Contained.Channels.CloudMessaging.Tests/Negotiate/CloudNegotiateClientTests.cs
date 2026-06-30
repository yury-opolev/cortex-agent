using System.Net;
using System.Text;
using Cortex.Contained.Channels.CloudMessaging.Auth;
using Cortex.Contained.Channels.CloudMessaging.Negotiate;

namespace Cortex.Contained.Channels.CloudMessaging.Tests.Negotiate;

/// <summary>
/// Tests for <see cref="CloudNegotiateClient"/> using a fake HTTP handler
/// to avoid any network dependency.
/// </summary>
public class CloudNegotiateClientTests
{
    private const string BaseUrl = "https://service.example.com";
    private const string FakeToken = "test-bridge-token";

    private static CloudNegotiateClient MakeClient(
        HttpStatusCode statusCode,
        string responseBody)
    {
        var handler = new FakeHttpMessageHandler(statusCode, responseBody);
        var httpClient = new HttpClient(handler);
        var credentialProvider = new StaticTokenBridgeCredentialProvider(FakeToken);
        return new CloudNegotiateClient(httpClient, credentialProvider, BaseUrl);
    }

    [Fact]
    public async Task NegotiateAsync_SuccessResponse_ReturnsUrlAndTenants()
    {
        var json = """{"url":"wss://wps.example.com/client?access_token=abc","tenants":["t1","t2"]}""";
        var client = MakeClient(HttpStatusCode.OK, json);

        var result = await client.NegotiateAsync();

        Assert.Equal("wss://wps.example.com/client?access_token=abc", result.Url);
        Assert.Equal(2, result.Tenants.Count);
        Assert.Contains("t1", result.Tenants);
        Assert.Contains("t2", result.Tenants);
    }

    [Fact]
    public async Task NegotiateAsync_NonSuccessStatus_Throws()
    {
        var client = MakeClient(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}""");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.NegotiateAsync());
    }

    [Fact]
    public async Task NegotiateAsync_EmptyTenants_Throws()
    {
        var json = """{"url":"wss://wps.example.com/client?access_token=abc","tenants":[]}""";
        var client = MakeClient(HttpStatusCode.OK, json);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.NegotiateAsync());
    }

    [Fact]
    public async Task NegotiateAsync_MissingUrl_Throws()
    {
        var json = """{"url":"","tenants":["t1"]}""";
        var client = MakeClient(HttpStatusCode.OK, json);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.NegotiateAsync());
    }

    [Fact]
    public async Task NegotiateAsync_SendsBearerToken()
    {
        var captureHandler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            """{"url":"wss://x.example.com","tenants":["t1"]}""");

        var httpClient = new HttpClient(captureHandler);
        var credentialProvider = new StaticTokenBridgeCredentialProvider(FakeToken);
        var client = new CloudNegotiateClient(httpClient, credentialProvider, BaseUrl);

        _ = await client.NegotiateAsync();

        Assert.NotNull(captureHandler.LastRequest);
        Assert.Equal(HttpMethod.Post, captureHandler.LastRequest.Method);
        Assert.Equal(
            $"{BaseUrl}/negotiate-bridge",
            captureHandler.LastRequest.RequestUri?.ToString());
        Assert.Equal(
            "Bearer",
            captureHandler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal(
            FakeToken,
            captureHandler.LastRequest.Headers.Authorization?.Parameter);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode statusCode;
        private readonly string body;

        internal FakeHttpMessageHandler(HttpStatusCode statusCode, string body)
        {
            this.statusCode = statusCode;
            this.body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(this.statusCode)
            {
                Content = new StringContent(this.body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode statusCode;
        private readonly string body;

        internal HttpRequestMessage? LastRequest { get; private set; }

        internal CapturingHttpMessageHandler(HttpStatusCode statusCode, string body)
        {
            this.statusCode = statusCode;
            this.body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.LastRequest = request;
            var response = new HttpResponseMessage(this.statusCode)
            {
                Content = new StringContent(this.body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
