using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// OAuth token refresh/reload callbacks the agent pushes to the Bridge.
/// Part of the composed <see cref="IAgentHubClient"/> surface — these callbacks
/// share the single SignalR hub connection and route by method name.
/// </summary>
public interface ICredentialsHubClient
{
    /// <summary>
    /// The agent needs the Bridge to refresh an OAuth token.
    /// The Bridge performs the OAuth refresh, persists the new tokens to DPAPI,
    /// and returns the fresh tokens directly via SignalR Client Results.
    /// This avoids a deadlock where a separate <c>ProvideCredentials</c> call
    /// would be queued behind the in-progress hub method.
    /// </summary>
    Task<TokenRefreshResult> OnTokenRefreshRequested(string providerName);

    /// <summary>
    /// The agent requests the Bridge to re-read credentials from secrets.json.
    /// Used when the OAuth token has been revoked (403) by another process
    /// (e.g. evals rotated the token). Unlike <see cref="OnTokenRefreshRequested"/>,
    /// this skips the OAuth refresh call and goes straight to re-reading stored secrets.
    /// </summary>
    Task<TokenRefreshResult> OnTokenReloadRequested(string providerName);
}
