using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tokens;

/// <summary>
/// Refreshes Anthropic Claude Pro/Max OAuth tokens. Handles providers whose
/// <see cref="LlmProviderConfig.TokenType"/> is <c>"oauth"</c>, whose
/// <see cref="LlmProviderConfig.Api"/> is <c>"anthropic-messages"</c>, and that carry a
/// rotating refresh token. The refresh exchange is delegated to
/// <see cref="SetupHelpers.RefreshAnthropicTokenAsync"/>; this type only maps the
/// response onto a <see cref="TokenRefreshOutcome"/> — persistence and config mutation
/// are <see cref="TokenRefreshService"/>'s responsibility.
///
/// This logic is ported verbatim (behaviour-preserving) from the old inline refresh
/// block in <c>CredentialsPusher.HandleTokenRefreshRequestAsync</c>.
/// </summary>
internal sealed class AnthropicOAuthRefreshStrategy : ITokenRefreshStrategy
{
    public bool CanHandle(LlmProviderConfig provider)
    {
        return string.Equals(provider.TokenType, "oauth", StringComparison.OrdinalIgnoreCase)
            && string.Equals(provider.Api, "anthropic-messages", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(provider.RefreshToken);
    }

    public async Task<TokenRefreshOutcome> RefreshAsync(
        LlmProviderConfig provider, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var tokenResp = await SetupHelpers
            .RefreshAnthropicTokenAsync(provider.RefreshToken!, httpClient, cancellationToken)
            .ConfigureAwait(false);

        var expiresAtMs = tokenResp.ExpiresIn > 0
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (tokenResp.ExpiresIn * 1000L)
            : 0L;

        return new TokenRefreshOutcome
        {
            AccessToken = tokenResp.AccessToken!,
            RefreshToken = tokenResp.RefreshToken ?? provider.RefreshToken,
            ExpiresAtMs = expiresAtMs,
        };
    }
}
