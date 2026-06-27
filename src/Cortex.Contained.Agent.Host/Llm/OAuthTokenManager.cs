using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Llm;

/// <summary>
/// Centralises provider authentication token lifecycle management for
/// <see cref="DirectLlmClient"/>: the Bridge-driven refresh state machine
/// (proactive refresh before expiry, plus the 401 "expired" / 403 "revoked"
/// retry paths). One implementation, shared by both the complete and stream
/// paths of every provider, so the single-flight refresh lock and "refresh
/// once then retry" semantics live in exactly one place.
///
/// The refresh path is kind-agnostic: it serves every credential whose access
/// token is minted/refreshed by the Bridge — <see cref="CredentialKind.AnthropicOAuth"/>
/// (rotating refresh token) and <see cref="CredentialKind.GitHubCopilotBearer"/>
/// (no refresh token; the Bridge re-mints from the durable PAT, which never
/// enters the container). Both signal the Bridge via the same SignalR round-trip
/// and apply the result via <see cref="ProviderState.UpdateOAuthTokens"/>.
/// </summary>
internal sealed partial class OAuthTokenManager : IDisposable
{
    private readonly ILogger logger;
    private readonly Cortex.Contained.Agent.Host.Agent.AgentMetrics? metrics;

    /// <summary>
    /// Serialises Bridge token-refresh requests so a rotating refresh token is
    /// never used more than once concurrently.
    /// </summary>
    private readonly SemaphoreSlim refreshLock = new(1, 1);

    /// <summary>
    /// Credential kinds whose access token is minted/refreshed by the Bridge and
    /// therefore handled by the proactive-expiry guard + on-401 force refresh.
    /// </summary>
    private static bool IsBridgeRefreshable(CredentialKind kind) =>
        kind is CredentialKind.AnthropicOAuth or CredentialKind.GitHubCopilotBearer;

    /// <summary>
    /// Callback wired by <see cref="Cortex.Contained.Agent.Host.Hubs.AgentHub"/>
    /// that signals the Bridge to refresh the token and return the fresh value
    /// directly via SignalR Client Results. Set to <c>null</c> when no Bridge
    /// connection is active.
    /// </summary>
    private volatile Func<string, Task<TokenRefreshResult>>? requestTokenRefreshCallback;

    /// <summary>
    /// Callback to request the Bridge to re-read tokens from secrets.json.
    /// Used when the token is revoked (403) by another process.
    /// </summary>
    private volatile Func<string, Task<TokenRefreshResult>>? requestTokenReloadCallback;

    public OAuthTokenManager(
        ILogger logger,
        Cortex.Contained.Agent.Host.Agent.AgentMetrics? metrics)
    {
        this.logger = logger;
        this.metrics = metrics;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.refreshLock.Dispose();
    }

    /// <summary>
    /// Sets the callback used to request a token refresh from the Bridge.
    /// Pass <c>null</c> to clear (e.g., on disconnect).
    /// </summary>
    public void SetRequestTokenRefreshCallback(Func<string, Task<TokenRefreshResult>>? callback)
    {
        this.requestTokenRefreshCallback = callback;
    }

    /// <summary>
    /// Sets the callback used to request a token reload from the Bridge.
    /// Used when the token is revoked (403) by another process (e.g. evals).
    /// </summary>
    public void SetRequestTokenReloadCallback(Func<string, Task<TokenRefreshResult>>? callback)
    {
        this.requestTokenReloadCallback = callback;
    }

    // ── Bridge-driven token refresh ──────────────────────────────────

    /// <summary>
    /// How far before expiry to proactively request a refresh.
    /// 5 minutes gives the Bridge enough time to complete the HTTP call and re-push.
    /// </summary>
    private const long OAuthRefreshBufferMs = 5 * 60 * 1_000L;

    /// <summary>
    /// Ensures the provider's Bridge-minted access token is fresh before sending a
    /// request. If the token is expired or within <see cref="OAuthRefreshBufferMs"/>
    /// of expiry, this signals the Bridge via <see cref="requestTokenRefreshCallback"/>,
    /// which returns the fresh token directly via SignalR Client Results. The Bridge
    /// performs the actual HTTP refresh/mint call.
    ///
    /// Kind-agnostic: serves both <see cref="CredentialKind.AnthropicOAuth"/> and
    /// <see cref="CredentialKind.GitHubCopilotBearer"/>. A null rotating refresh token
    /// is fine for Copilot — the Bridge re-mints from the durable PAT (which never
    /// enters the container), so the refresh signal carries only the provider name.
    ///
    /// Previous versions waited for a separate <c>ProvideCredentials</c> hub call,
    /// but that caused a deadlock: SignalR processes hub methods sequentially per
    /// connection, so the credential push was queued behind the in-progress
    /// <c>SendMessage</c> call. Returning the token inline avoids this entirely.
    ///
    /// A <see cref="SemaphoreSlim"/> prevents multiple concurrent callers from
    /// sending duplicate refresh signals — a rotating refresh token is single-use,
    /// so only one refresh must run at a time.
    /// </summary>
    public async Task EnsureFreshTokenAsync(
        ProviderState provider, CancellationToken cancellationToken)
    {
        if (!IsBridgeRefreshable(provider.Credential.Kind))
        {
            return;
        }

        // No expiry information available — assume the token is valid
        if (provider.CurrentAccessTokenExpiresAtMs == 0)
        {
            return;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowMs < provider.CurrentAccessTokenExpiresAtMs - OAuthRefreshBufferMs)
        {
            return; // still valid with comfortable margin
        }

        // Serialize so only one refresh signal is sent even under concurrent load
        await this.refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring lock — a concurrent caller may have just refreshed
            nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (nowMs < provider.CurrentAccessTokenExpiresAtMs - OAuthRefreshBufferMs)
            {
                return;
            }

            var cb = this.requestTokenRefreshCallback;
            if (cb is null)
            {
                this.LogTokenRefreshSkipped(provider.Credential.Name);
                return;
            }

            this.LogTokenRefreshRequesting(provider.Credential.Name);

            // Signal Bridge and get the fresh token directly via SignalR Client Results.
            // No separate ProvideCredentials call needed — avoids the hub-method deadlock.
            TokenRefreshResult result;
            try
            {
                using var timeout = CancellationTokenSource
                    .CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                result = await cb(provider.Credential.Name).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                this.LogTokenRefreshTimeout(provider.Credential.Name);
                return; // proceed with stale token; the request may return 401
            }
            catch (Exception ex)
            {
                this.LogTokenRefreshSignalFailed(provider.Credential.Name, ex.Message);
                return; // proceed with stale token; will get 401 with a clear error message
            }

            if (result.Success && !string.IsNullOrEmpty(result.AccessToken))
            {
                // Apply the fresh token directly — same as what ConfigureCredentials/UpdateOAuthTokens does.
                // For Copilot the result.RefreshToken is null and UpdateOAuthTokens leaves it unchanged.
                provider.UpdateOAuthTokens(
                    result.AccessToken,
                    result.RefreshToken,
                    result.ExpiresAtMs);
                this.metrics?.IncrementTokenRefreshSuccess();
                this.LogTokenRefreshReceived(provider.Credential.Name);
            }
            else
            {
                // Intentional: if the Bridge strategy declines (e.g. an Anthropic provider with
                // no rotating refresh token, or a transient Copilot mint failure), this is a
                // bounded no-op round-trip — we log it and proceed with the existing token. A
                // genuinely stale token then surfaces as a 401, which the caller's on-401 path
                // handles via ForceRefreshAsync.
                this.metrics?.IncrementTokenRefreshFailure();
                this.LogTokenRefreshSignalFailed(
                    provider.Credential.Name,
                    result.Error ?? "Bridge returned failure with no details");
            }
        }
        finally
        {
            this.refreshLock.Release();
        }
    }

    /// <summary>
    /// Forces a Bridge token refresh after a 401 (expired) response: logs the
    /// refresh request, marks the current token as expired (so the proactive guard
    /// in <see cref="EnsureFreshTokenAsync"/> fires), then drives the refresh. Used
    /// by the provider API clients' 401 retry paths so the "force expire → refresh"
    /// sequence and its logging live in one place. Kind-agnostic — same path for
    /// Anthropic OAuth and the Bridge-minted Copilot bearer.
    /// </summary>
    public Task ForceRefreshAsync(
        ProviderState provider, CancellationToken cancellationToken)
    {
        this.LogTokenRefreshRequesting(provider.Credential.Name);
        // Mark the token as definitely-expired so EnsureFreshTokenAsync's buffer check fires.
        // Use 1 (not 0): expiry 0 means "no expiry information available", which the proactive
        // guard treats as still-valid and would swallow this forced refresh.
        provider.UpdateOAuthTokens(
            provider.CurrentAccessToken ?? string.Empty,
            provider.CurrentRefreshToken,
            1);
        return this.EnsureFreshTokenAsync(provider, cancellationToken);
    }

    /// <summary>
    /// Requests the Bridge to re-read credentials from secrets.json.
    /// Used when the token has been revoked (403) by another process (e.g. evals).
    /// Unlike <see cref="EnsureFreshTokenAsync"/>, this skips the refresh call
    /// entirely — the refresh token is likely also stale.
    /// </summary>
    public async Task RequestTokenReloadAsync(
        ProviderState provider, CancellationToken cancellationToken)
    {
        var cb = this.requestTokenReloadCallback;
        if (cb is null)
        {
            this.LogTokenRefreshSkipped(provider.Credential.Name);
            return;
        }

        // Same lock as EnsureFreshTokenAsync — both paths mutate
        // the provider's token state via UpdateOAuthTokens.
        await this.refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.LogTokenReloadRequesting(provider.Credential.Name);

            TokenRefreshResult result;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                result = await cb(provider.Credential.Name).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.LogTokenRefreshSignalFailed(provider.Credential.Name, ex.Message);
                return;
            }

            if (result.Success && !string.IsNullOrEmpty(result.AccessToken))
            {
                provider.UpdateOAuthTokens(
                    result.AccessToken,
                    result.RefreshToken,
                    result.ExpiresAtMs);
                this.metrics?.IncrementTokenRefreshSuccess();
                this.LogTokenReloadReceived(provider.Credential.Name);
            }
            else
            {
                this.metrics?.IncrementTokenRefreshFailure();
                this.LogTokenRefreshSignalFailed(
                    provider.Credential.Name,
                    result.Error ?? "Bridge returned failure — re-authenticate via setup");
            }
        }
        finally
        {
            this.refreshLock.Release();
        }
    }

    // ── LoggerMessage ────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "Token refresh skipped for {Provider}: no callback available")]
    private partial void LogTokenRefreshSkipped(string provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "Access token expired/expiring — requesting Bridge to refresh: provider={Provider}")]
    private partial void LogTokenRefreshRequesting(string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Token refresh signal failed for {Provider}: {ErrorMessage}")]
    private partial void LogTokenRefreshSignalFailed(string provider, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Token refresh received from Bridge: provider={Provider}")]
    private partial void LogTokenRefreshReceived(string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Timed out waiting for Bridge to refresh access token: provider={Provider}")]
    private partial void LogTokenRefreshTimeout(string provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "Requesting Bridge to reload token from secrets.json (token revoked): provider={Provider}")]
    private partial void LogTokenReloadRequesting(string provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "Token reloaded from secrets.json via Bridge: provider={Provider}")]
    private partial void LogTokenReloadReceived(string provider);
}
