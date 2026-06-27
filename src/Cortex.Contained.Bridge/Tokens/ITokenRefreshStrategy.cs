using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tokens;

/// <summary>
/// A per-provider OAuth token-refresh scheme. Each strategy knows how to recognise
/// the providers it handles (<see cref="CanHandle"/>) and how to mint a fresh access
/// token for them (<see cref="RefreshAsync"/>). Strategies are pure: they perform the
/// network exchange and map the response to a <see cref="TokenRefreshOutcome"/>, but do
/// NOT persist tokens or mutate provider config — <see cref="TokenRefreshService"/>
/// owns caching, single-flight, persistence, and the reload fallback.
/// </summary>
internal interface ITokenRefreshStrategy
{
    /// <summary>Returns <c>true</c> when this strategy can refresh the given provider.</summary>
    bool CanHandle(LlmProviderConfig provider);

    /// <summary>
    /// Performs the refresh exchange for <paramref name="provider"/> using
    /// <paramref name="httpClient"/>. Throws on a non-success response.
    /// </summary>
    Task<TokenRefreshOutcome> RefreshAsync(
        LlmProviderConfig provider, HttpClient httpClient, CancellationToken cancellationToken);
}
