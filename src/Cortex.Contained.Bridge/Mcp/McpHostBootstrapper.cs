using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Starts the MCP host at Bridge startup: subscribes the catalog pusher to the host service's
/// catalog-change event, then reconciles connections against the configured servers. Runs in the
/// background so spawning/handshaking MCP servers never blocks host startup. Per-agent (re)connect
/// catalog pushes and invoke routing are wired in <see cref="Hosting.TenantConnectionBootstrapper"/>.
/// </summary>
public sealed partial class McpHostBootstrapper : BackgroundService
{
    /// <summary>
    /// Cadence of the background reconcile sweep. Each tick re-reconciles the configured servers,
    /// which retries any connection that is failed/dropped (subject to its exponential backoff) and
    /// re-pushes the catalog when it changes.
    /// </summary>
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    private readonly McpHostService hostService;
    private readonly McpConfigStore configStore;
    private readonly McpCatalogPusher catalogPusher;
    private readonly ILogger<McpHostBootstrapper> logger;

    public McpHostBootstrapper(
        McpHostService hostService,
        McpConfigStore configStore,
        McpCatalogPusher catalogPusher,
        ILogger<McpHostBootstrapper> logger)
    {
        this.hostService = hostService;
        this.configStore = configStore;
        this.catalogPusher = catalogPusher;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Push the freshly-aggregated catalog to connected agents whenever it changes.
        this.hostService.CatalogChanged += this.catalogPusher.PushCatalogAsync;

        var settings = this.configStore.GetSettings();
        this.LogReconciling(settings.Servers.Count, settings.Enabled);

        // Initial reconcile, then a periodic sweep so failed/dropped connections auto-retry with
        // backoff without needing a config change. Each tick is best-effort and never crashes the host.
        await this.ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(ReconcileInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await this.ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Bridge is shutting down.
        }
    }

    private async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.hostService.ReconcileAsync(this.configStore.GetSettings(), cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Reconcile failures must not crash the Bridge.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // SECURITY: content-free — only the exception TYPE (a reconcile fault message could
            // echo a connection detail from a misconfigured server), consistent with the
            // Bridge-side MCP redaction guarantee (docs/security.md).
            this.LogReconcileFailed(ex.GetType().Name);
        }
#pragma warning restore CA1031
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP host bootstrapping: {ServerCount} configured servers (master enabled={Enabled})")]
    private partial void LogReconciling(int serverCount, bool enabled);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP host startup reconcile failed: {Error}")]
    private partial void LogReconcileFailed(string error);
}
