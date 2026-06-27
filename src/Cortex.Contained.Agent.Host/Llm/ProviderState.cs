using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Llm;

/// <summary>
/// Per-provider mutable credential/token state shared by <see cref="DirectLlmClient"/>,
/// the provider API clients, and <see cref="OAuthTokenManager"/>.
/// </summary>
internal sealed class ProviderState
{
    public LlmProviderCredential Credential { get; }

    // ── Bridge-refreshed token state (Anthropic OAuth + Copilot bearer) ─────────
    // Initialised from the credential; updated in-place by UpdateOAuthTokens
    // whenever the Bridge re-pushes (or returns inline) after a token refresh/mint.
    // Uses Volatile.Read/Write for fields read outside OAuthTokenManager's refresh lock
    // to ensure visibility across threads (required on ARM; redundant on x86
    // but makes the intent explicit).

    /// <summary>Current OAuth access token. Updated after each refresh.</summary>
    public string? CurrentAccessToken
    {
        get => Volatile.Read(ref this.currentAccessToken);
        private set => Volatile.Write(ref currentAccessToken, value);
    }
    private string? currentAccessToken;

    /// <summary>Current OAuth refresh token. Updated after each refresh.</summary>
    public string? CurrentRefreshToken
    {
        get => Volatile.Read(ref this.currentRefreshToken);
        private set => Volatile.Write(ref currentRefreshToken, value);
    }
    private string? currentRefreshToken;

    /// <summary>Unix ms when <see cref="CurrentAccessToken"/> expires. 0 = no info.</summary>
    public long CurrentAccessTokenExpiresAtMs
    {
        get => Volatile.Read(ref this.currentAccessTokenExpiresAtMs);
        private set => Volatile.Write(ref currentAccessTokenExpiresAtMs, value);
    }
    private long currentAccessTokenExpiresAtMs;

    /// <summary>
    /// Pending awaiter created by <see cref="OAuthTokenManager.EnsureFreshTokenAsync"/>.
    /// Completed by <see cref="UpdateOAuthTokens"/> when the Bridge re-pushes credentials.
    /// </summary>
    private TaskCompletionSource<bool>? pendingRefresh;

    public ProviderState(LlmProviderCredential credential)
    {
        Credential = credential;

        // Seed the mutable OAuth fields for every kind whose access token is refreshed
        // via the Bridge round-trip: Anthropic OAuth (with a rotating refresh token) and
        // the Bridge-minted Copilot bearer (no refresh token — re-minted from the PAT,
        // which never enters the container). Seeding uniformly lets the proactive-expiry
        // guard and UpdateOAuthTokens work identically for both kinds.
        if (credential.Kind is CredentialKind.AnthropicOAuth or CredentialKind.GitHubCopilotBearer)
        {
            CurrentAccessToken = credential.AccessToken;
            CurrentRefreshToken = credential.RefreshToken;
            CurrentAccessTokenExpiresAtMs = credential.AccessTokenExpiresAt;
        }
    }

    /// <summary>
    /// Returns a <see cref="Task{bool}"/> that completes when <see cref="UpdateOAuthTokens"/>
    /// is called. Multiple concurrent callers share the same <see cref="TaskCompletionSource{T}"/>.
    /// </summary>
    public Task<bool> GetOrCreateRefreshAwaiter()
    {
        var fresh = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        // If no TCS exists yet, set ours; otherwise return the existing one's Task
        return (Interlocked.CompareExchange(ref this.pendingRefresh, fresh, null) ?? fresh).Task;
    }

    /// <summary>
    /// Updates the mutable token fields and signals any callers waiting in
    /// <see cref="OAuthTokenManager.EnsureFreshTokenAsync"/>.
    /// Called by <see cref="DirectLlmClient.ConfigureCredentials"/> when the Bridge
    /// re-pushes credentials, and inline by the manager when the Bridge returns a fresh
    /// token via SignalR Client Results. A null <paramref name="refreshToken"/> leaves the
    /// existing refresh token unchanged (Copilot bearer carries none).
    /// </summary>
    public void UpdateOAuthTokens(string accessToken, string? refreshToken, long expiresAtMs)
    {
        CurrentAccessToken = accessToken;
        if (!string.IsNullOrEmpty(refreshToken))
        {
            CurrentRefreshToken = refreshToken;
        }
        CurrentAccessTokenExpiresAtMs = expiresAtMs;

        // Release any task waiting for the refresh to complete
        Interlocked.Exchange(ref this.pendingRefresh, null)?.TrySetResult(true);
    }
}
