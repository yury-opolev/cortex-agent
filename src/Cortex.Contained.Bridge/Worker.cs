using Cortex.Contained.Bridge.Channels;
using Cortex.Contained.Bridge.Hosting;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge;

/// <summary>
/// Background service that orchestrates the Bridge lifecycle. Thin orchestrator:
/// it owns only the startup order, the connect retry loop, the keep-alive health
/// check, and graceful shutdown — delegating the real work to focused collaborators:
/// <list type="bullet">
/// <item><see cref="ChannelLifecycleManager"/> — channel registration, slash-command
/// routing, active-channel list, voice-handler reconciliation.</item>
/// <item><see cref="TenantConnectionBootstrapper"/> — per-tenant hub-client wiring
/// (dispatcher, coding binder, hub events, reconnect re-push).</item>
/// <item><see cref="CredentialsPusher"/> — pushing credentials/channels/memory settings
/// to the agent and OAuth token refresh/reload.</item>
/// </list>
/// </summary>
public sealed partial class Worker : BackgroundService
{
    private readonly Tenants.TenantRouter tenantRouter;
    private readonly ChannelManager channelManager;
    private readonly HubMessageDispatcher dispatcher;
    private readonly BridgeConfig config;
    private readonly Tenants.IContainerManager? containerManager;
    private readonly ChannelLifecycleManager channelLifecycle;
    private readonly TenantConnectionBootstrapper connectionBootstrapper;
    private readonly CredentialsPusher credentialsPusher;
    private readonly ILogger<Worker> logger;

    public Worker(
        Tenants.TenantRouter tenantRouter,
        ChannelManager channelManager,
        HubMessageDispatcher dispatcher,
        BridgeConfig config,
        ChannelLifecycleManager channelLifecycle,
        TenantConnectionBootstrapper connectionBootstrapper,
        CredentialsPusher credentialsPusher,
        ILogger<Worker> logger,
        Tenants.IContainerManager? containerManager = null)
    {
        this.tenantRouter = tenantRouter;
        this.channelManager = channelManager;
        this.dispatcher = dispatcher;
        this.config = config;
        this.channelLifecycle = channelLifecycle;
        this.connectionBootstrapper = connectionBootstrapper;
        this.credentialsPusher = credentialsPusher;
        this.containerManager = containerManager;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this.LogBridgeStarting();

        // 1. Register channels
        this.channelLifecycle.RegisterChannels();

        // 2. Initialize dispatcher (bidirectional message routing)
        this.dispatcher.Initialize();

        // Reconnect re-push needs the active channel list — supply it from the
        // channel-lifecycle manager so the bootstrapper stays decoupled.
        this.connectionBootstrapper.ActiveChannelIdsProvider = this.channelLifecycle.BuildActiveChannelIds;

        // 3. Set up auto-wiring so every tenant connection gets events attached
        this.tenantRouter.OnClientConnected = this.connectionBootstrapper.OnTenantClientConnected;

        // 4. Connect default tenant to Agent Hub with retry (fires OnClientConnected)
        await ConnectWithRetryAsync(stoppingToken).ConfigureAwait(false);

        // 5. Push LLM credentials to the agent
        await this.credentialsPusher.PushCredentialsAsync(stoppingToken).ConfigureAwait(false);

        // 6. Tell the agent which channels are active
        await this.credentialsPusher.PushActiveChannelsAsync(
            this.channelLifecycle.BuildActiveChannelIds(), stoppingToken).ConfigureAwait(false);

        // 7. Push persisted memory settings so the agent uses host-side values
        await this.credentialsPusher.PushMemorySettingsAsync(stoppingToken).ConfigureAwait(false);

        // 7b. Push the effective voice-id flag so enrollment tools hide when disabled
        await this.credentialsPusher.PushSpeakerIdConfigAsync(stoppingToken).ConfigureAwait(false);

        // 7c. Push the Bridge-authoritative subagent concurrency cap — the Agent's own
        // YAML-mounted config can be stale/mismatched, so the Bridge value always wins.
        await this.credentialsPusher.PushAgentConfigAsync(stoppingToken).ConfigureAwait(false);

        // 8. Connect all channels
        await this.channelManager.ConnectAllAsync(stoppingToken).ConfigureAwait(false);

        // 9. Reconcile voice handlers from tenant config (after Discord connects)
        await this.channelLifecycle.ReconcileVoiceHandlersFromConfigAsync().ConfigureAwait(false);

        this.LogBridgeRunning();

        // 10. Keep alive until shutdown
        try
        {
            var consecutiveDisconnectedTicks = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                // Periodic health check
                var healthClient = this.tenantRouter.GetDefaultClient();
                if (healthClient?.IsConnected == true)
                {
                    consecutiveDisconnectedTicks = 0;
                    try
                    {
                        var health = await healthClient.PingAsync(stoppingToken).ConfigureAwait(false);
                        this.LogHealthCheck(health.Healthy, health.Version);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        this.LogHealthCheckFailed(ex.Message);
                    }
                }
                else
                {
                    // Stuck-connection watchdog. SignalR's automatic reconnect can wedge
                    // forever (observed 2026-06-10: a reconnect attempt hung on a WebSocket
                    // connect that never timed out, so the retry policy never fired again
                    // and the Bridge stayed disconnected even after the agent came back).
                    // After 3 consecutive disconnected ticks (~90s), force-rebuild the
                    // connection and re-push agent state, then keep retrying every tick.
                    consecutiveDisconnectedTicks++;
                    if (consecutiveDisconnectedTicks >= 3)
                    {
                        this.LogForcingConnectionRebuild(consecutiveDisconnectedTicks * 30);
                        try
                        {
                            await this.tenantRouter.ReconnectDefaultAsync(
                                this.config.AgentHubUrl, this.config.HubToken, stoppingToken).ConfigureAwait(false);

                            await this.credentialsPusher.PushCredentialsAsync(stoppingToken).ConfigureAwait(false);
                            await this.credentialsPusher.PushActiveChannelsAsync(
                                this.channelLifecycle.BuildActiveChannelIds(), stoppingToken).ConfigureAwait(false);
                            await this.credentialsPusher.PushMemorySettingsAsync(stoppingToken).ConfigureAwait(false);
                            await this.credentialsPusher.PushAgentConfigAsync(stoppingToken).ConfigureAwait(false);

                            consecutiveDisconnectedTicks = 0;
                            this.LogConnectionRebuilt();
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            this.LogConnectionRebuildFailed(ex.Message);
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }

        this.LogBridgeStopping();
    }

    private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        var hubUrl = this.config.AgentHubUrl;
        var token = this.config.HubToken;

        const int maxRetries = 10;
        var delay = TimeSpan.FromSeconds(2);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                this.LogConnectionAttempt(attempt, hubUrl);
                await this.tenantRouter.ConnectDefaultAsync(hubUrl, token, cancellationToken).ConfigureAwait(false);
                this.LogConnectionSuccess(attempt);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.LogConnectionFailed(attempt, maxRetries, ex.Message);

                if (attempt == maxRetries)
                {
                    throw new InvalidOperationException(
                        $"Failed to connect to Agent Hub after {maxRetries} attempts.", ex);
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
    }

    /// <summary>
    /// Builds the <see cref="LlmCredentials"/> payload from the current config and pushes it
    /// to the agent over SignalR. Called at startup, after reconnect, after setup-wizard saves,
    /// and after every OAuth token refresh so the agent always has the latest access tokens.
    /// Delegates to <see cref="CredentialsPusher"/>; retained on Worker for the Bridge endpoints
    /// (setup/settings) that re-push after a config change.
    /// </summary>
    public Task PushCredentialsAsync(CancellationToken cancellationToken)
        => this.credentialsPusher.PushCredentialsAsync(cancellationToken);

    /// <summary>
    /// Handles a token-refresh request. Delegates to <see cref="CredentialsPusher"/>;
    /// retained on Worker for the settings endpoint's refresh-models OAuth retry path.
    /// </summary>
    public Task<TokenRefreshResult> HandleTokenRefreshRequestAsync(
        string providerName, CancellationToken cancellationToken)
        => this.credentialsPusher.HandleTokenRefreshRequestAsync(providerName, cancellationToken);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        this.LogBridgeStopping();

        // Stop all tenant agent containers in parallel so they save session snapshots.
        // This must happen before disposing the TenantRouter (which closes SignalR connections).
        await this.StopAllContainersAsync(cancellationToken).ConfigureAwait(false);

        await this.channelManager.DisconnectAllAsync().ConfigureAwait(false);
        await this.tenantRouter.DisposeAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gracefully stops all connected tenant containers in parallel.
    /// Each container receives SIGTERM, saves its session snapshot, and exits.
    /// </summary>
    private async Task StopAllContainersAsync(CancellationToken cancellationToken)
    {
        if (this.containerManager is null)
        {
            return;
        }

        var tenantIds = this.tenantRouter.GetConnectedTenantIds();
        if (tenantIds.Count == 0)
        {
            return;
        }

        this.LogStoppingContainers(tenantIds.Count);

        var stopTasks = tenantIds.Select(id =>
            this.containerManager.StopContainerAsync(id, cancellationToken));

        await Task.WhenAll(stopTasks).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cortex Bridge starting...")]
    private partial void LogBridgeStarting();

    [LoggerMessage(Level = LogLevel.Information, Message = "Cortex Bridge running and connected to Agent Hub")]
    private partial void LogBridgeRunning();

    [LoggerMessage(Level = LogLevel.Information, Message = "Cortex Bridge stopping...")]
    private partial void LogBridgeStopping();

    [LoggerMessage(Level = LogLevel.Information, Message = "Connecting to Agent Hub (attempt {Attempt}): {HubUrl}")]
    private partial void LogConnectionAttempt(int attempt, string hubUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to Agent Hub on attempt {Attempt}")]
    private partial void LogConnectionSuccess(int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection attempt {Attempt}/{MaxRetries} failed: {ErrorMessage}")]
    private partial void LogConnectionFailed(int attempt, int maxRetries, string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Health check: healthy={Healthy}, version={Version}")]
    private partial void LogHealthCheck(bool healthy, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Health check failed: {ErrorMessage}")]
    private partial void LogHealthCheckFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent Hub disconnected for {Seconds}s — forcing connection rebuild")]
    private partial void LogForcingConnectionRebuild(int seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent Hub connection rebuilt and state re-pushed")]
    private partial void LogConnectionRebuilt();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent Hub connection rebuild failed: {ErrorMessage}")]
    private partial void LogConnectionRebuildFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping {Count} agent container(s)...")]
    private partial void LogStoppingContainers(int count);
}
