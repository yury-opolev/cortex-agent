using System.Net.Http.Headers;
using System.Text.Json;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tokens;

/// <summary>
/// Exchanges a GitHub Personal Access Token (PAT) for a short-lived GitHub Copilot API
/// token. Handles providers whose <see cref="LlmProviderConfig.Api"/> is
/// <c>"github-copilot-api"</c> and whose <see cref="LlmProviderConfig.TokenType"/> is
/// <c>"pat"</c>.
///
/// Ported from the agent's <c>OAuthTokenManager.EnsureCopilotTokenAsync</c>: the same
/// <c>GET copilot_internal/v2/token</c> endpoint, the <c>Authorization: Token {pat}</c>
/// header, the <c>cortex-agent/1.0.0</c> User-Agent, and the <c>{ token, expires_at }</c> response
/// (where <c>expires_at</c> is Unix <b>seconds</b>). The minted token has no rotating refresh
/// token (it is re-minted from the long-lived PAT), so <see cref="TokenRefreshOutcome.RefreshToken"/>
/// is always <c>null</c>.
///
/// Scope boundary: this strategy intentionally handles the <c>"pat"</c> token type only.
/// GitHub Copilot <c>"oauth"</c> providers keep their existing direct-Bearer path (the
/// OAuth access token is used as the bearer credential directly) and are NOT handled here.
/// </summary>
internal sealed class CopilotTokenExchangeStrategy : ITokenRefreshStrategy
{
    private const string TokenExchangeUrl = "https://api.github.com/copilot_internal/v2/token";

    /// <summary>Matches the agent's snake_case deserialization of the exchange response.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public bool CanHandle(LlmProviderConfig provider)
    {
        return string.Equals(provider.Api, "github-copilot-api", StringComparison.OrdinalIgnoreCase)
            && string.Equals(provider.TokenType, "pat", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<TokenRefreshOutcome> RefreshAsync(
        LlmProviderConfig provider, HttpClient httpClient, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TokenExchangeUrl);

        // GitHub requires "Token" auth (not "Bearer") for a PAT on this endpoint.
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", provider.ApiKey);
        request.Headers.UserAgent.ParseAdd("cortex-agent/1.0.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content
                .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Copilot token exchange failed: HTTP {(int)response.StatusCode}: {Truncate(errorBody)}");
        }

        var json = await response.Content
            .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var tokenResponse = JsonSerializer.Deserialize<CopilotTokenResponse>(json, JsonOptions);

        if (string.IsNullOrEmpty(tokenResponse?.Token))
        {
            throw new InvalidOperationException("Copilot token exchange returned null token.");
        }

        return new TokenRefreshOutcome
        {
            AccessToken = tokenResponse.Token,
            // The minted Copilot token is derived from the long-lived PAT, so there is no
            // rotating refresh token; we always re-mint from the PAT on the next refresh.
            RefreshToken = null,
            // GitHub returns expires_at in Unix SECONDS; the outcome expects Unix ms.
            ExpiresAtMs = tokenResponse.ExpiresAt * 1000L,
        };
    }

    private static string Truncate(string error)
    {
        const int maxLength = 500;
        return error.Length <= maxLength ? error : string.Concat(error.AsSpan(0, maxLength), "...");
    }

    /// <summary>
    /// Response from GitHub's <c>copilot_internal/v2/token</c> exchange endpoint. Kept local
    /// to this file so the Bridge does not depend on the agent project's equivalent DTO.
    /// </summary>
    private sealed class CopilotTokenResponse
    {
        public string? Token { get; set; }

        /// <summary>Token expiry as a Unix timestamp in <b>seconds</b>.</summary>
        public long ExpiresAt { get; set; }
    }
}
