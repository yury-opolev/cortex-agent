using System.Collections.Concurrent;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Tenants;

/// <summary>
/// Background service that periodically health-checks all registered tenants,
/// tracks their status, last activity, and handles idle timeout.
/// </summary>
public sealed partial class TenantHealthService : BackgroundService
{
    private readonly TenantRouter router;
    private readonly TenantRegistry registry;
    private readonly ILogger<TenantHealthService> logger;

    /// <summary>Health check interval.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>Per-tenant health state, keyed by tenant ID.</summary>
    private readonly ConcurrentDictionary<string, TenantHealthState> healthStates = new(StringComparer.OrdinalIgnoreCase);

    public TenantHealthService(
        TenantRouter router,
        TenantRegistry registry,
        ILogger<TenantHealthService> logger)
    {
        this.router = router;
        this.registry = registry;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait briefly for startup to complete
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

        this.LogHealthServiceStarted();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAllTenantsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Health check loop must not crash
            catch (Exception ex)
            {
                this.LogHealthCheckError(ex.Message);
            }
#pragma warning restore CA1031

            await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the health state for a tenant. Creates a new entry if not yet tracked.
    /// </summary>
    public TenantHealthState GetHealthState(string tenantId)
        => this.healthStates.GetOrAdd(tenantId, _ => new TenantHealthState());

    /// <summary>
    /// Returns health states for all tracked tenants.
    /// </summary>
    public IReadOnlyDictionary<string, TenantHealthState> GetAllHealthStates()
        => this.healthStates;

    /// <summary>
    /// Records activity for a tenant (called when a message is dispatched).
    /// Resets the idle timer.
    /// </summary>
    public void RecordActivity(string tenantId)
    {
        var state = GetHealthState(tenantId);
        state.LastActivityAt = DateTimeOffset.UtcNow;
        state.MessageCount++;
    }

    // ── Core health check loop ────────────────────────────────────────

    private async Task CheckAllTenantsAsync(CancellationToken cancellationToken)
    {
        foreach (var (tenantId, config) in this.registry.GetAll())
        {
            if (!config.Enabled)
            {
                continue;
            }

            var state = GetHealthState(tenantId);
            var client = this.router.GetClient(tenantId);

            if (client is null || !client.IsConnected)
            {
                if (state.Status != TenantConnectionStatus.Disconnected)
                {
                    state.Status = TenantConnectionStatus.Disconnected;
                    this.LogTenantStatusChanged(tenantId, "Disconnected");
                }
                continue;
            }

            // Ping the container
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                var health = await client.PingAsync(cts.Token).ConfigureAwait(false);

                state.Status = health.Healthy
                    ? TenantConnectionStatus.Connected
                    : TenantConnectionStatus.Unhealthy;
                state.LastPingAt = DateTimeOffset.UtcNow;
                state.AgentVersion = health.Version;
                state.LastError = null;
                state.ConsecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // Propagate shutdown
            }
#pragma warning disable CA1031 // Individual tenant failures must not stop the loop
            catch (Exception ex)
            {
                state.ConsecutiveFailures++;
                state.LastError = ex.Message;

                if (state.ConsecutiveFailures >= 3 && state.Status != TenantConnectionStatus.Unreachable)
                {
                    state.Status = TenantConnectionStatus.Unreachable;
                    this.LogTenantUnreachable(tenantId, state.ConsecutiveFailures, ex.Message);
                }
                else if (state.Status != TenantConnectionStatus.Unreachable)
                {
                    state.Status = TenantConnectionStatus.Error;
                }
            }
#pragma warning restore CA1031

            // Check idle timeout
            if (config.IdleTimeoutMinutes > 0
                && state.LastActivityAt.HasValue
                && state.Status == TenantConnectionStatus.Connected)
            {
                var idleSince = DateTimeOffset.UtcNow - state.LastActivityAt.Value;
                if (idleSince.TotalMinutes >= config.IdleTimeoutMinutes)
                {
                    this.LogTenantIdleTimeout(tenantId, (int)idleSince.TotalMinutes, config.IdleTimeoutMinutes);
                    state.Status = TenantConnectionStatus.IdleStopped;

                    // Disconnect the tenant (container stop would be done by Docker provisioning layer)
                    await this.router.DisconnectTenantAsync(tenantId).ConfigureAwait(false);
                }
            }
        }
    }

    // ── Logging ───────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant health service started (interval: 30s)")]
    private partial void LogHealthServiceStarted();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Health check loop error: {Error}")]
    private partial void LogHealthCheckError(string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant '{TenantId}' status changed to {Status}")]
    private partial void LogTenantStatusChanged(string tenantId, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant '{TenantId}' unreachable ({FailureCount} consecutive failures): {Error}")]
    private partial void LogTenantUnreachable(string tenantId, int failureCount, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant '{TenantId}' idle for {IdleMinutes}min (timeout: {TimeoutMinutes}min), stopping")]
    private partial void LogTenantIdleTimeout(string tenantId, int idleMinutes, int timeoutMinutes);
}

/// <summary>Connection status for a tenant container.</summary>
public enum TenantConnectionStatus
{
    /// <summary>Not yet checked or unknown.</summary>
    Unknown,

    /// <summary>Connected and healthy.</summary>
    Connected,

    /// <summary>SignalR connection exists but ping reports unhealthy.</summary>
    Unhealthy,

    /// <summary>SignalR connection lost.</summary>
    Disconnected,

    /// <summary>Ping failed — transient error.</summary>
    Error,

    /// <summary>Multiple consecutive ping failures.</summary>
    Unreachable,

    /// <summary>Stopped due to idle timeout.</summary>
    IdleStopped,
}

/// <summary>
/// Mutable health state for a single tenant.
/// Updated by <see cref="TenantHealthService"/> on each check cycle.
/// </summary>
public sealed class TenantHealthState
{
    /// <summary>Current connection status.</summary>
    public TenantConnectionStatus Status { get; set; } = TenantConnectionStatus.Unknown;

    /// <summary>When the last successful ping was received.</summary>
    public DateTimeOffset? LastPingAt { get; set; }

    /// <summary>When the tenant last had a message dispatched to it.</summary>
    public DateTimeOffset? LastActivityAt { get; set; }

    /// <summary>Total messages dispatched to this tenant since Bridge started.</summary>
    public long MessageCount { get; set; }

    /// <summary>Agent version reported by the last successful ping.</summary>
    public string? AgentVersion { get; set; }

    /// <summary>Last error message from a failed ping.</summary>
    public string? LastError { get; set; }

    /// <summary>Number of consecutive ping failures.</summary>
    public int ConsecutiveFailures { get; set; }
}
