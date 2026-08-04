using Cortex.Contained.Agent.Host.Llm.Providers.Copilot;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Llm;

/// <summary>
/// Per-provider mutable credential/token state shared by <see cref="DirectLlmClient"/>,
/// the provider API clients, and <see cref="OAuthTokenManager"/>.
/// </summary>
internal sealed class ProviderState
{
    /// <summary>
    /// The provider credential and its pushed configuration (base URL, model list, and per-model
    /// <see cref="LlmModelMetadata"/>/supported endpoints). Swapped in place by
    /// <see cref="UpdateConfiguration"/> on every Bridge re-push so a subsequent push's endpoint
    /// metadata takes effect without a reconnect. Read via <see cref="Volatile"/> so other threads
    /// observe the latest reference.
    /// </summary>
    public LlmProviderCredential Credential
    {
        get => Volatile.Read(ref this.credential);
        private set => Volatile.Write(ref this.credential, value);
    }
    private LlmProviderCredential credential;

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
        this.credential = credential;

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

    /// <summary>
    /// Replaces <see cref="Credential"/> with a freshly pushed one, refreshing the provider
    /// configuration/metadata (base URL, model list, and per-model <see cref="LlmModelMetadata"/>
    /// supported endpoints) while deliberately leaving the live token state
    /// (<see cref="CurrentAccessToken"/>/<see cref="CurrentRefreshToken"/>/
    /// <see cref="CurrentAccessTokenExpiresAtMs"/>) and any pending refresh awaiter untouched.
    /// <para>
    /// Called by <see cref="DirectLlmClient.ConfigureCredentials"/> when the Bridge re-pushes
    /// credentials for a provider that is refreshed in place (Anthropic OAuth / Copilot bearer),
    /// so a subsequent push's endpoint metadata takes effect without a reconnect. The token fields
    /// remain authoritative and are updated separately via <see cref="UpdateOAuthTokens"/>; this
    /// method must run before that call so a released refresh awaiter observes both fresh config
    /// and fresh token.
    /// </para>
    /// </summary>
    public void UpdateConfiguration(LlmProviderCredential credential)
    {
        Credential = credential;

        // Freshly pushed metadata is authoritative: drop anything learned from a rejection so a
        // corrected snapshot is never shadowed by a stale in-memory override.
        this.endpointOverrides.Clear();
    }

    /// <summary>
    /// Endpoints learned from a Copilot rejection, keyed by model. Populated when a request is
    /// refused because the model is not served on the endpoint the pushed metadata selected, so
    /// later turns skip the known-bad endpoint instead of paying the failure every time. Cleared
    /// whenever the Bridge re-pushes metadata.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CopilotEndpoint> endpointOverrides =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records the endpoint that actually served <paramref name="model"/>.</summary>
    public void SetEndpointOverride(string model, CopilotEndpoint endpoint)
        => this.endpointOverrides[model] = endpoint;

    /// <summary>The learned endpoint for <paramref name="model"/>, if a rejection taught us one.</summary>
    public CopilotEndpoint? GetEndpointOverride(string model)
        => this.endpointOverrides.TryGetValue(model, out var endpoint) ? endpoint : null;

    /// <summary>
    /// Finds the <see cref="LlmModelMetadata"/> for <paramref name="model"/> in
    /// <see cref="Credential"/>'s pushed metadata, matching model IDs with
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>. Returns <see langword="null"/> when no
    /// metadata was pushed or no entry matches.
    /// </summary>
    public LlmModelMetadata? FindModelMetadata(string model)
    {
        var metadata = Credential.ModelMetadata;
        if (metadata is null)
        {
            return null;
        }

        foreach (var entry in metadata)
        {
            if (string.Equals(entry.Id, model, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }
}
