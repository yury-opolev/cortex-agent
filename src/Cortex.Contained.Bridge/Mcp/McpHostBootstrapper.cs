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

        try
        {
            var settings = this.configStore.GetSettings();
            this.LogReconciling(settings.Servers.Count, settings.Enabled);
            await this.hostService.ReconcileAsync(settings, stoppingToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Startup reconcile failures must not crash the Bridge.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogReconcileFailed(ex.Message);
        }
#pragma warning restore CA1031
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP host bootstrapping: {ServerCount} configured servers (master enabled={Enabled})")]
    private partial void LogReconciling(int serverCount, bool enabled);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP host startup reconcile failed: {Error}")]
    private partial void LogReconcileFailed(string error);
}
