using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tokens;

/// <summary>
/// Proactively refreshes LLM provider OAuth tokens before they expire, then re-pushes the
/// fresh credentials to connected agents. Runs as a hosted service: a startup delay followed
/// by a fixed-interval sweep (<see cref="RunSweepAsync"/>) that refreshes any provider whose
/// access token is due to expire within <see cref="AheadOfExpiryBufferMs"/>.
///
/// Modelled on <c>TenantHealthService</c> (startup delay + interval loop, per-item try/catch
/// so one failure can't abort the sweep).
///
/// Scheme coverage: the sweep refreshes any provider carrying a non-zero
/// <see cref="LlmProviderConfig.TokenExpiresAt"/>. Anthropic OAuth providers always do. As of
/// Phase 3, a Copilot bearer push also sets <c>TokenExpiresAt</c> to the minted bearer's expiry,
/// so the sweep now proactively re-mints Copilot bearers too (the Bridge holds the PAT and
/// re-mints; the PAT never enters the container). Providers with <c>TokenExpiresAt == 0</c>
/// (static keys, setup tokens) are skipped — there is nothing to proactively refresh.
/// </summary>
internal sealed partial class TokenRefreshBackgroundService : BackgroundService
{
    /// <summary>Delay before the first sweep, letting startup (connect + initial push) settle.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    /// <summary>Interval between sweeps. Frequent enough to refresh well inside the buffer window.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Refresh a token this far ahead of its expiry. 5 minutes gives ample margin over the
    /// 60s sweep interval and the service's own 60s cache buffer, so a token never lapses
    /// between sweeps.
    /// </summary>
    private const long AheadOfExpiryBufferMs = 5 * 60 * 1000;

    private readonly BridgeConfig config;
    private readonly TokenRefreshService tokenRefreshService;
    private readonly ICredentialReplisher repush;
    private readonly ILogger<TokenRefreshBackgroundService> logger;

    public TokenRefreshBackgroundService(
        BridgeConfig config,
        TokenRefreshService tokenRefreshService,
        ICredentialReplisher repush,
        ILogger<TokenRefreshBackgroundService> logger)
    {
        this.config = config;
        this.tokenRefreshService = tokenRefreshService;
        this.repush = repush;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for startup (initial connect + first credential push) to settle.
        await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);

        this.LogServiceStarted((int)SweepInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await this.RunSweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Sweep loop must not crash the host
            catch (Exception ex)
            {
                this.LogSweepError(ex.Message);
            }
#pragma warning restore CA1031

            await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Refreshes every provider whose access token is within <see cref="AheadOfExpiryBufferMs"/>
    /// of expiry, isolating per-provider failures. If any provider refreshed successfully,
    /// re-pushes credentials to connected agents exactly once at the end. Returns the number
    /// of providers refreshed. Exposed (and not <c>private</c>) as the unit-test seam.
    /// </summary>
    internal async Task<int> RunSweepAsync(CancellationToken cancellationToken)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var refreshedCount = 0;

        foreach (var provider in this.config.LlmProviders)
        {
            // TokenExpiresAt == 0 means "no known expiry" (static key, setup token, or
            // PAT-derived token) — nothing to proactively refresh.
            if (provider.TokenExpiresAt <= 0)
            {
                continue;
            }

            // Not yet inside the ahead-of-expiry window — leave it for a later sweep.
            if (nowMs < provider.TokenExpiresAt - AheadOfExpiryBufferMs)
            {
                continue;
            }

            try
            {
                var result = await this.tokenRefreshService
                    .RefreshAsync(provider, cancellationToken).ConfigureAwait(false);

                if (result.Success)
                {
                    refreshedCount++;
                    this.LogProviderRefreshed(provider.Name);
                }
                else
                {
                    this.LogProviderRefreshFailed(provider.Name, result.Error ?? "unknown");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // Propagate shutdown
            }
#pragma warning disable CA1031 // One provider's failure must not stop the sweep
            catch (Exception ex)
            {
                this.LogProviderRefreshFailed(provider.Name, ex.Message);
            }
#pragma warning restore CA1031
        }

        // Re-push exactly once if anything changed, so agents pick up the fresh tokens.
        if (refreshedCount > 0)
        {
            await this.repush.PushCredentialsAsync(cancellationToken).ConfigureAwait(false);
            this.LogCredentialsRepushed(refreshedCount);
        }

        return refreshedCount;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Proactive token-refresh service started (interval: {IntervalSeconds}s)")]
    private partial void LogServiceStarted(int intervalSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Token-refresh sweep error: {Error}")]
    private partial void LogSweepError(string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Proactively refreshed token for provider '{ProviderName}'")]
    private partial void LogProviderRefreshed(string providerName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Proactive refresh failed for provider '{ProviderName}': {Error}")]
    private partial void LogProviderRefreshFailed(string providerName, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-pushed credentials after refreshing {RefreshedCount} provider token(s)")]
    private partial void LogCredentialsRepushed(int refreshedCount);
}
