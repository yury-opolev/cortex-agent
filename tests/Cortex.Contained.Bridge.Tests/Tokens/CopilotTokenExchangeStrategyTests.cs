using System.Net;
using System.Text;
using Cortex.Contained.Bridge.Tokens;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Tokens;

/// <summary>
/// Unit tests for <see cref="CopilotTokenExchangeStrategy"/>. A stub
/// <see cref="HttpMessageHandler"/> stands in for GitHub's <c>copilot_internal/v2/token</c>
/// endpoint so no real network calls are made. These pin the exchange ported from the agent's
/// <c>OAuthTokenManager.EnsureCopilotTokenAsync</c>: the CanHandle gate (copilot + PAT only),
/// the request shape (<c>Authorization: Token {pat}</c>, the UA, the URL), and the
/// token + expires_at(seconds)→ms mapping.
/// </summary>
public sealed class CopilotTokenExchangeStrategyTests
{
    private const string TokenUrl = "https://api.github.com/copilot_internal/v2/token";

    // ── CanHandle truth table ─────────────────────────────────────────────────

    [Fact]
    public void CanHandle_CopilotApiWithPat_True()
    {
        var strategy = new CopilotTokenExchangeStrategy();
        var provider = Provider(api: "github-copilot-api", tokenType: "pat");

        Assert.True(strategy.CanHandle(provider));
    }

    [Fact]
    public void CanHandle_IsCaseInsensitiveForApiAndTokenType()
    {
        var strategy = new CopilotTokenExchangeStrategy();
        var provider = Provider(api: "GitHub-Copilot-API", tokenType: "PAT");

        Assert.True(strategy.CanHandle(provider));
    }

    [Fact]
    public void CanHandle_Anthropic_False()
    {
        var strategy = new CopilotTokenExchangeStrategy();
        var provider = Provider(api: "anthropic-messages", tokenType: "oauth");

        Assert.False(strategy.CanHandle(provider));
    }

    [Fact]
    public void CanHandle_PlainOpenAi_False()
    {
        var strategy = new CopilotTokenExchangeStrategy();
        var provider = Provider(api: "openai-completions", tokenType: "bearer");

        Assert.False(strategy.CanHandle(provider));
    }

    [Fact]
    public void CanHandle_CopilotOAuth_False()
    {
        // Scope boundary: GitHub Copilot OAuth keeps its existing direct-Bearer path.
        var strategy = new CopilotTokenExchangeStrategy();
        var provider = Provider(api: "github-copilot-api", tokenType: "oauth");

        Assert.False(strategy.CanHandle(provider));
    }

    // ── RefreshAsync request shape ────────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_SendsTokenAuthAndUserAgentToTokenUrl()
    {
        var strategy = new CopilotTokenExchangeStrategy();
        var provider = Provider(api: "github-copilot-api", tokenType: "pat", apiKey: "ghp_thepat");

        HttpRequestMessage? captured = null;
        using var http = MakeClient(HttpStatusCode.OK, TokenResponse("minted-token", expiresAt: 9999999999), req => captured = req);

        await strategy.RefreshAsync(provider, http, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured!.Method);
        Assert.Equal(TokenUrl, captured.RequestUri!.ToString());
        Assert.Equal("Token", captured.Headers.Authorization!.Scheme);
        Assert.Equal("ghp_thepat", captured.Headers.Authorization.Parameter);
        Assert.Contains(captured.Headers.UserAgent, p => p.Product?.Name == "cortex-agent" && p.Product?.Version == "1.0.0");
        Assert.Contains(captured.Headers.Accept, a => a.MediaType == "application/json");
    }

    // ── RefreshAsync mapping ──────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_MapsTokenAndExpiresAtSecondsToMs()
    {
        var strategy = new CopilotTokenExchangeStrategy();
        var provider = Provider(api: "github-copilot-api", tokenType: "pat", apiKey: "ghp_thepat");

        const long expiresAtSeconds = 1_900_000_000L;
        using var http = MakeClient(HttpStatusCode.OK, TokenResponse("minted-token", expiresAtSeconds));

        var outcome = await strategy.RefreshAsync(provider, http, CancellationToken.None);

        Assert.Equal("minted-token", outcome.AccessToken);
        Assert.Equal(expiresAtSeconds * 1000L, outcome.ExpiresAtMs);
        Assert.Null(outcome.RefreshToken);
    }

    [Fact]
    public async Task RefreshAsync_NonSuccessStatus_Throws()
    {
        var strategy = new CopilotTokenExchangeStrategy();
        var provider = Provider(api: "github-copilot-api", tokenType: "pat", apiKey: "ghp_thepat");
        using var http = MakeClient(HttpStatusCode.Unauthorized, """{"error":"bad credentials"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => strategy.RefreshAsync(provider, http, CancellationToken.None));
    }

    [Fact]
    public async Task RefreshAsync_NullToken_Throws()
    {
        var strategy = new CopilotTokenExchangeStrategy();
        var provider = Provider(api: "github-copilot-api", tokenType: "pat", apiKey: "ghp_thepat");
        // Valid 200 but no token field.
        using var http = MakeClient(HttpStatusCode.OK, """{"expires_at":9999999999}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => strategy.RefreshAsync(provider, http, CancellationToken.None));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static LlmProviderConfig Provider(string api, string tokenType, string apiKey = "ghp_pat") =>
        new()
        {
            Name = "github-copilot-api",
            Api = api,
            TokenType = tokenType,
            ApiKey = apiKey,
        };

    private static string TokenResponse(string token, long expiresAt) =>
        $$"""{"token":"{{token}}","expires_at":{{expiresAt}}}""";

    private static HttpClient MakeClient(
        HttpStatusCode statusCode, string body, Action<HttpRequestMessage>? onRequest = null)
    {
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            onRequest?.Invoke(req);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        });
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
