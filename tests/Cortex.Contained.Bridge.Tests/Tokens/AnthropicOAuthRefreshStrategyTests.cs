using System.Net;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Bridge.Tokens;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Tokens;

/// <summary>
/// Unit tests for <see cref="AnthropicOAuthRefreshStrategy"/>. A stub
/// <see cref="HttpMessageHandler"/> stands in for Anthropic's token endpoint so no real
/// network calls are made. These pin the behaviour ported verbatim from the old inline
/// refresh block in <c>CredentialsPusher</c>: the CanHandle gate and the access/refresh/
/// expires_in mapping.
/// </summary>
public sealed class AnthropicOAuthRefreshStrategyTests
{
    // ── CanHandle truth table ─────────────────────────────────────────────────

    [Fact]
    public void CanHandle_OAuthAnthropicWithRefreshToken_True()
    {
        var strategy = new AnthropicOAuthRefreshStrategy();
        var provider = Provider(tokenType: "oauth", api: "anthropic-messages", refreshToken: "r");

        Assert.True(strategy.CanHandle(provider));
    }

    [Fact]
    public void CanHandle_IsCaseInsensitiveForTokenTypeAndApi()
    {
        var strategy = new AnthropicOAuthRefreshStrategy();
        var provider = Provider(tokenType: "OAuth", api: "Anthropic-Messages", refreshToken: "r");

        Assert.True(strategy.CanHandle(provider));
    }

    [Fact]
    public void CanHandle_NotOAuth_False()
    {
        var strategy = new AnthropicOAuthRefreshStrategy();
        var provider = Provider(tokenType: "bearer", api: "anthropic-messages", refreshToken: "r");

        Assert.False(strategy.CanHandle(provider));
    }

    [Fact]
    public void CanHandle_NotAnthropicApi_False()
    {
        var strategy = new AnthropicOAuthRefreshStrategy();
        var provider = Provider(tokenType: "oauth", api: "github-copilot-api", refreshToken: "r");

        Assert.False(strategy.CanHandle(provider));
    }

    [Fact]
    public void CanHandle_NoRefreshToken_False()
    {
        var strategy = new AnthropicOAuthRefreshStrategy();
        var provider = Provider(tokenType: "oauth", api: "anthropic-messages", refreshToken: null);

        Assert.False(strategy.CanHandle(provider));
    }

    // ── RefreshAsync mapping ──────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_MapsAccessRefreshAndExpiry()
    {
        var strategy = new AnthropicOAuthRefreshStrategy();
        var provider = Provider(tokenType: "oauth", api: "anthropic-messages", refreshToken: "old-refresh");
        using var http = MakeClient(HttpStatusCode.OK, TokenResponse(
            accessToken: "new-access", refreshToken: "new-refresh", expiresIn: 3600));

        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var outcome = await strategy.RefreshAsync(provider, http, CancellationToken.None);
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.Equal("new-access", outcome.AccessToken);
        Assert.Equal("new-refresh", outcome.RefreshToken);

        // ExpiresAtMs ≈ now + expires_in*1000 (bracketed by the call window).
        Assert.InRange(outcome.ExpiresAtMs, before + (3600 * 1000L), after + (3600 * 1000L));
    }

    [Fact]
    public async Task RefreshAsync_NoRotatedRefreshToken_FallsBackToProviderRefreshToken()
    {
        var strategy = new AnthropicOAuthRefreshStrategy();
        var provider = Provider(tokenType: "oauth", api: "anthropic-messages", refreshToken: "old-refresh");
        // Response omits refresh_token → strategy keeps the provider's existing one.
        using var http = MakeClient(HttpStatusCode.OK, TokenResponse(
            accessToken: "new-access", refreshToken: null, expiresIn: 3600));

        var outcome = await strategy.RefreshAsync(provider, http, CancellationToken.None);

        Assert.Equal("old-refresh", outcome.RefreshToken);
    }

    [Fact]
    public async Task RefreshAsync_ExpiresInZero_ExpiresAtMsIsZero()
    {
        var strategy = new AnthropicOAuthRefreshStrategy();
        var provider = Provider(tokenType: "oauth", api: "anthropic-messages", refreshToken: "old-refresh");
        using var http = MakeClient(HttpStatusCode.OK, TokenResponse(
            accessToken: "new-access", refreshToken: "new-refresh", expiresIn: 0));

        var outcome = await strategy.RefreshAsync(provider, http, CancellationToken.None);

        Assert.Equal(0L, outcome.ExpiresAtMs);
    }

    [Fact]
    public async Task RefreshAsync_NonSuccessStatus_Throws()
    {
        var strategy = new AnthropicOAuthRefreshStrategy();
        var provider = Provider(tokenType: "oauth", api: "anthropic-messages", refreshToken: "old-refresh");
        using var http = MakeClient(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => strategy.RefreshAsync(provider, http, CancellationToken.None));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static LlmProviderConfig Provider(string tokenType, string api, string? refreshToken) =>
        new()
        {
            Name = "anthropic",
            Api = api,
            TokenType = tokenType,
            ApiKey = "old-access",
            RefreshToken = refreshToken,
            TokenExpiresAt = 0,
        };

    private static string TokenResponse(string accessToken, string? refreshToken, int expiresIn)
    {
        var payload = refreshToken is null
            ? (object)new { access_token = accessToken, expires_in = expiresIn, token_type = "bearer" }
            : new { access_token = accessToken, refresh_token = refreshToken, expires_in = expiresIn, token_type = "bearer" };
        return JsonSerializer.Serialize(payload);
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

    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
