using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Channels.CloudMessaging.Auth;

namespace Cortex.Contained.Channels.CloudMessaging.Tests.Auth;

/// <summary>
/// Tests for <see cref="PrivateKeyJwtBridgeCredentialProvider"/>.
/// Verifies the OAuth 2.0 client_credentials flow with private_key_jwt:
/// the provider builds and signs a JWT assertion, posts to /oauth2/token,
/// and returns (and caches) the issued access token.
/// </summary>
public class PrivateKeyJwtBridgeCredentialProviderTests
{
    private const string TokenEndpoint = "https://service.example.com/oauth2/token";
    private const string ClientId = "bridge-client-001";

    // Generate a fresh RSA key pair for each test run (tests only; never a real key).
    private static readonly string TestPrivateKeyPem;

    static PrivateKeyJwtBridgeCredentialProviderTests()
    {
        using var rsa = RSA.Create(2048);
        TestPrivateKeyPem = rsa.ExportRSAPrivateKeyPem();
    }

    private static PrivateKeyJwtBridgeCredentialProvider MakeProvider(
        HttpStatusCode statusCode,
        string responseBody,
        out CapturingHttpMessageHandler captureHandler)
    {
        captureHandler = new CapturingHttpMessageHandler(statusCode, responseBody);
        var httpClient = new HttpClient(captureHandler);
        return new PrivateKeyJwtBridgeCredentialProvider(
            httpClient, TokenEndpoint, ClientId, TestPrivateKeyPem);
    }

    [Fact]
    public async Task GetTokenAsync_SuccessResponse_ReturnsAccessToken()
    {
        var json = """{"access_token":"my-s2s-token","token_type":"Bearer","expires_in":3600}""";
        var provider = MakeProvider(HttpStatusCode.OK, json, out _);

        var token = await provider.GetTokenAsync();

        Assert.Equal("my-s2s-token", token);
    }

    [Fact]
    public async Task GetTokenAsync_PostsToTokenEndpoint()
    {
        var json = """{"access_token":"tok","token_type":"Bearer","expires_in":3600}""";
        var provider = MakeProvider(HttpStatusCode.OK, json, out var capture);

        _ = await provider.GetTokenAsync();

        Assert.NotNull(capture.LastRequest);
        Assert.Equal(HttpMethod.Post, capture.LastRequest.Method);
        Assert.Equal(TokenEndpoint, capture.LastRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task GetTokenAsync_IncludesClientCredentialsGrantType()
    {
        var json = """{"access_token":"tok","token_type":"Bearer","expires_in":3600}""";
        var provider = MakeProvider(HttpStatusCode.OK, json, out var capture);

        _ = await provider.GetTokenAsync();

        Assert.NotNull(capture.LastRequestBody);
        Assert.Contains("grant_type=client_credentials", capture.LastRequestBody);
    }

    [Fact]
    public async Task GetTokenAsync_IncludesClientId()
    {
        var json = """{"access_token":"tok","token_type":"Bearer","expires_in":3600}""";
        var provider = MakeProvider(HttpStatusCode.OK, json, out var capture);

        _ = await provider.GetTokenAsync();

        Assert.NotNull(capture.LastRequestBody);
        Assert.Contains("client_id=bridge-client-001", capture.LastRequestBody);
    }

    [Fact]
    public async Task GetTokenAsync_IncludesPrivateKeyJwtAssertionType()
    {
        var json = """{"access_token":"tok","token_type":"Bearer","expires_in":3600}""";
        var provider = MakeProvider(HttpStatusCode.OK, json, out var capture);

        _ = await provider.GetTokenAsync();

        Assert.NotNull(capture.LastRequestBody);
        Assert.Contains(
            "client_assertion_type=urn%3Aietf%3Aparams%3Aoauth%3Aclient-assertion-type%3Ajwt-bearer",
            capture.LastRequestBody);
    }

    [Fact]
    public async Task GetTokenAsync_IncludesJwtAssertion()
    {
        var json = """{"access_token":"tok","token_type":"Bearer","expires_in":3600}""";
        var provider = MakeProvider(HttpStatusCode.OK, json, out var capture);

        _ = await provider.GetTokenAsync();

        Assert.NotNull(capture.LastRequestBody);
        // A JWT has the form "header.payload.signature" — two dots
        var assertionParam = ExtractFormParam(capture.LastRequestBody!, "client_assertion");
        Assert.NotNull(assertionParam);
        Assert.Equal(2, assertionParam!.Count(c => c == '.'));
    }

    [Fact]
    public async Task GetTokenAsync_NonSuccessStatus_Throws()
    {
        var provider = MakeProvider(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""", out _);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetTokenAsync());
    }

    [Fact]
    public async Task GetTokenAsync_EmptyAccessToken_Throws()
    {
        var json = """{"access_token":"","token_type":"Bearer","expires_in":3600}""";
        var provider = MakeProvider(HttpStatusCode.OK, json, out _);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync());
    }

    [Fact]
    public async Task GetTokenAsync_CachesToken_OnlyOneHttpCall()
    {
        var json = """{"access_token":"cached-tok","token_type":"Bearer","expires_in":3600}""";
        var provider = MakeProvider(HttpStatusCode.OK, json, out var capture);

        var first = await provider.GetTokenAsync();
        var second = await provider.GetTokenAsync();

        Assert.Equal("cached-tok", first);
        Assert.Equal("cached-tok", second);
        Assert.Equal(1, capture.CallCount); // only one HTTP call despite two GetTokenAsync calls
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string? ExtractFormParam(string body, string key)
    {
        var prefix = key + "=";
        var parts = body.Split('&');
        foreach (var part in parts)
        {
            if (part.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(part[prefix.Length..]);
            }
        }

        return null;
    }

    internal sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode statusCode;
        private readonly string body;

        internal HttpRequestMessage? LastRequest { get; private set; }
        internal string? LastRequestBody { get; private set; }
        internal int CallCount { get; private set; }

        internal CapturingHttpMessageHandler(HttpStatusCode statusCode, string body)
        {
            this.statusCode = statusCode;
            this.body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.LastRequest = request;
            this.CallCount++;

            if (request.Content is not null)
            {
                this.LastRequestBody = await request.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var response = new HttpResponseMessage(this.statusCode)
            {
                Content = new StringContent(this.body, Encoding.UTF8, "application/json"),
            };
            return response;
        }
    }
}
