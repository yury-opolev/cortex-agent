using System.Collections.Concurrent;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Tokens;

/// <summary>
/// Owns the per-provider OAuth token-refresh lifecycle on the Bridge: result caching,
/// single-flight serialization, strategy dispatch, persistence to DPAPI, in-memory config
/// mutation, and the reload-from-secrets fallback. Refresh schemes themselves live behind
/// <see cref="ITokenRefreshStrategy"/>; this service is provider-agnostic.
///
/// Extracted from <c>CredentialsPusher</c> with behaviour preserved: the cache (60s buffer),
/// the per-provider single-flight lock, the double-checked cache read, and the persist-then-mutate
/// order are carried over verbatim. The exception fallback is now scheme-aware — the
/// reload-from-secrets path runs only for rotating-refresh-token providers (Anthropic), never for
/// re-mint-from-PAT providers (Copilot), so a transient Copilot failure can never surface the
/// durable PAT as a bearer. Anthropic's fallback behaviour is unchanged.
/// </summary>
internal sealed partial class TokenRefreshService
{
    private readonly IReadOnlyList<ITokenRefreshStrategy> strategies;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly SecretManager secretManager;
    private readonly ILogger<TokenRefreshService> logger;

    /// <summary>
    /// Cached result of the most recent successful OAuth token refresh per provider.
    /// When multiple containers share the same OAuth provider, the second container to
    /// request a refresh gets the cached tokens instead of performing another refresh
    /// (which would fail because the refresh token was already consumed).
    /// </summary>
    private readonly ConcurrentDictionary<string, CachedRefreshResult> refreshCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Serializes token refresh requests per provider. Prevents concurrent refreshes
    /// of the same provider (which would fail because rotating refresh tokens are
    /// single-use).
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> refreshLocks = new(StringComparer.OrdinalIgnoreCase);

    public TokenRefreshService(
        IEnumerable<ITokenRefreshStrategy> strategies,
        IHttpClientFactory httpClientFactory,
        SecretManager secretManager,
        ILogger<TokenRefreshService> logger)
    {
        this.strategies = strategies.ToList();
        this.httpClientFactory = httpClientFactory;
        this.secretManager = secretManager;
        this.logger = logger;
    }

    /// <summary>
    /// Refreshes the OAuth token for <paramref name="provider"/> via the matching strategy,
    /// persisting and applying the new tokens. Caches the result (60s buffer) and serializes
    /// concurrent refreshes per provider. On failure, falls back to re-reading secrets.json
    /// ONLY for rotating-refresh-token schemes (Anthropic), in case another process rotated the
    /// token; for re-mint-from-PAT schemes (Copilot) a failure surfaces cleanly so the durable
    /// PAT is never reloaded and returned as a bearer.
    /// </summary>
    public async Task<TokenRefreshResult> RefreshAsync(
        LlmProviderConfig provider, CancellationToken cancellationToken)
    {
        var providerName = provider.Name;

        var strategy = this.strategies.FirstOrDefault(s => s.CanHandle(provider));
        if (strategy is null)
        {
            this.LogTokenRefreshNotApplicable(providerName);
            return new TokenRefreshResult { Success = false, Error = "Provider is not OAuth or has no refresh token" };
        }

        // ── Check cache: if we recently refreshed, return the cached result ──
        // This prevents multiple containers from burning through rotating refresh tokens.
        // A cached result is valid if the access token hasn't expired yet (with 60s buffer).
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (this.refreshCache.TryGetValue(providerName, out var cached)
            && cached.Result.Success
            && cached.Result.ExpiresAtMs > nowMs + 60_000)
        {
            var secondsAgo = (nowMs - cached.RefreshedAtMs) / 1000;
            var expiresInSeconds = (cached.Result.ExpiresAtMs - nowMs) / 1000;
            this.LogTokenRefreshServedFromCache(providerName, secondsAgo, expiresInSeconds);
            return cached.Result;
        }

        // ── Serialize refreshes per provider ──
        // Prevents two concurrent requests from both trying to use the same
        // single-use rotating refresh token.
        var refreshLock = this.refreshLocks.GetOrAdd(providerName, _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check cache after acquiring lock — another thread may have just refreshed
            nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (this.refreshCache.TryGetValue(providerName, out cached)
                && cached.Result.Success
                && cached.Result.ExpiresAtMs > nowMs + 60_000)
            {
                var secondsAgo = (nowMs - cached.RefreshedAtMs) / 1000;
                var expiresInSeconds = (cached.Result.ExpiresAtMs - nowMs) / 1000;
                this.LogTokenRefreshServedFromCache(providerName, secondsAgo, expiresInSeconds);
                return cached.Result;
            }

            using var httpClient = this.httpClientFactory.CreateClient("oauth-refresh");
            var outcome = await strategy.RefreshAsync(provider, httpClient, cancellationToken).ConfigureAwait(false);

            // Persist to DPAPI + update in-memory config so any future push sends the fresh tokens.
            this.ApplyAndPersist(provider, outcome);

            this.LogTokenRefreshSuccess(providerName);

            var result = new TokenRefreshResult
            {
                Success = true,
                AccessToken = outcome.AccessToken,
                RefreshToken = outcome.RefreshToken,
                ExpiresAtMs = outcome.ExpiresAtMs,
            };

            // Cache the successful result so other containers get it immediately
            this.refreshCache[providerName] = new CachedRefreshResult
            {
                Result = result,
                RefreshedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            return result;
        }
        catch (Exception ex)
        {
            this.LogTokenRefreshFailed(providerName, ex.Message);

            // The reload-from-secrets fallback only makes sense for ROTATING-refresh-token
            // schemes (Anthropic OAuth), where another process (e.g. evals) may have rotated
            // the token on disk — re-reading secrets.json can surface that fresher token.
            //
            // For schemes with NO rotating refresh token (Copilot PAT exchange) the durable
            // secret in provider.ApiKey is the long-lived PAT itself. Reloading it would return
            // the PAT as result.AccessToken — i.e. push the PAT to the agent as the "bearer"
            // (which 401s) and leak the durable credential into the container. So a strategy
            // failure here is transient and must surface as a clean failure, never a PAT.
            //
            // The presence of a refresh token is the same signal ApplyAndPersist uses to
            // distinguish the Anthropic persist branch from the Copilot no-persist branch.
            if (HasRotatingRefreshToken(provider))
            {
                var reloaded = TryReloadTokenFromSecrets(this.secretManager, provider);
                if (reloaded)
                {
                    this.LogTokenReloadedFromSecrets(providerName);
                    var result = new TokenRefreshResult
                    {
                        Success = true,
                        AccessToken = provider.ApiKey,
                        RefreshToken = provider.RefreshToken,
                        ExpiresAtMs = provider.TokenExpiresAt,
                    };

                    // Also cache the reloaded result
                    this.refreshCache[providerName] = new CachedRefreshResult
                    {
                        Result = result,
                        RefreshedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    };

                    return result;
                }
            }

            return new TokenRefreshResult { Success = false, Error = ex.Message };
        }
        finally
        {
            refreshLock.Release();
        }
    }

    /// <summary>
    /// Persists the refreshed tokens to DPAPI and applies them to the in-memory provider
    /// config. Branches on whether the scheme has a rotating refresh token:
    /// <list type="bullet">
    ///   <item><b>Rotating refresh token present (Anthropic OAuth):</b> the minted access
    ///   token <i>is</i> the durable credential — persist it (and the rotated refresh token)
    ///   to DPAPI and overwrite <c>provider.ApiKey</c> so the next push sends the fresh token.</item>
    ///   <item><b>No refresh token (GitHub Copilot bearer):</b> the durable credential is the
    ///   long-lived PAT in <c>provider.ApiKey</c>, used for the next exchange. The minted bearer
    ///   is short-lived and re-derivable from the PAT, so we must NOT persist it and must NOT
    ///   overwrite the PAT. We update only <c>provider.TokenExpiresAt</c> (so the proactive
    ///   sweep tracks the bearer); the minted bearer itself lives in the result cache that
    ///   <c>RefreshAsync</c> populates. This keeps the PAT on the Bridge and out of the container.</item>
    /// </list>
    /// </summary>
    private void ApplyAndPersist(LlmProviderConfig provider, TokenRefreshOutcome outcome)
    {
        if (outcome.RefreshToken is null)
        {
            // Copilot (PAT-exchange) path: PAT stays the durable secret. Track only the
            // bearer's expiry so the proactive sweep re-mints ahead of rotation. Do NOT
            // persist and do NOT touch provider.ApiKey (the PAT used for the next exchange).
            provider.TokenExpiresAt = outcome.ExpiresAtMs;
            return;
        }

        // Anthropic OAuth path: the access token is the durable credential.
        // Persist to DPAPI so the tokens survive a Bridge restart.
        this.secretManager.StoreOAuthTokens(
            provider.Name, outcome.AccessToken, outcome.RefreshToken, outcome.ExpiresAtMs);

        // Update in-memory config so any future PushCredentialsAsync sends the fresh tokens.
        provider.ApiKey = outcome.AccessToken;
        provider.RefreshToken = outcome.RefreshToken;
        provider.TokenExpiresAt = outcome.ExpiresAtMs;
    }

    /// <summary>
    /// Whether <paramref name="provider"/> uses a rotating refresh-token scheme (e.g. Anthropic
    /// OAuth) as opposed to a re-mint-from-durable-secret scheme (e.g. Copilot PAT exchange).
    /// A non-empty <see cref="LlmProviderConfig.RefreshToken"/> is the same signal
    /// <see cref="ApplyAndPersist"/> branches on: only rotating schemes carry one, and only for
    /// them is the durable secret distinct from the minted access token — so only for them is
    /// re-reading secrets.json a safe fallback. For Copilot the durable secret IS the PAT in
    /// <c>ApiKey</c>, which must never be reloaded and returned as a bearer.
    /// </summary>
    private static bool HasRotatingRefreshToken(LlmProviderConfig provider)
    {
        return !string.IsNullOrEmpty(provider.RefreshToken);
    }

    /// <summary>
    /// Re-reads the API key and refresh token from DPAPI-encrypted secrets.json.
    /// Returns <c>true</c> if a different (presumably fresher) token was found.
    /// Shared by the refresh fallback and <c>CredentialsPusher.HandleTokenReloadRequestAsync</c>.
    /// </summary>
    internal static bool TryReloadTokenFromSecrets(SecretManager secretManager, LlmProviderConfig provider)
    {
        try
        {
            var apiKey = secretManager.GetApiKey(provider.Name);
            var refreshToken = secretManager.GetRefreshToken(provider.Name);

            if (string.IsNullOrEmpty(apiKey) || apiKey == provider.ApiKey)
            {
                return false; // same token or not found — nothing to reload
            }

            provider.ApiKey = apiKey;
            if (!string.IsNullOrEmpty(refreshToken))
            {
                provider.RefreshToken = refreshToken;
            }

            // Reset expiry — we don't know when this token was issued,
            // but it's presumably fresher than the revoked one.
            provider.TokenExpiresAt = 0;

            return true;
        }
        catch
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Token refresh not applicable for provider {ProviderName} (not OAuth or missing refresh token)")]
    private partial void LogTokenRefreshNotApplicable(string providerName);

    [LoggerMessage(Level = LogLevel.Information, Message = "OAuth token refreshed successfully for provider: {ProviderName}")]
    private partial void LogTokenRefreshSuccess(string providerName);

    [LoggerMessage(Level = LogLevel.Error, Message = "OAuth token refresh failed for provider {ProviderName}: {ErrorMessage}")]
    private partial void LogTokenRefreshFailed(string providerName, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "OAuth token reloaded from secrets.json for provider {ProviderName} (another process may have refreshed it)")]
    private partial void LogTokenReloadedFromSecrets(string providerName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Token refresh for '{ProviderName}' served from cache (refreshed {SecondsAgo}s ago, expires in {ExpiresInSeconds}s)")]
    private partial void LogTokenRefreshServedFromCache(string providerName, long secondsAgo, long expiresInSeconds);
}

/// <summary>
/// Cached result of a successful token refresh. Used to avoid redundant refreshes
/// when multiple containers share the same OAuth provider.
/// </summary>
internal sealed record CachedRefreshResult
{
    public required TokenRefreshResult Result { get; init; }
    public required long RefreshedAtMs { get; init; }
}
