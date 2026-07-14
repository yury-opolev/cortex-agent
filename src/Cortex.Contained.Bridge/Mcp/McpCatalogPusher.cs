using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Pushes the aggregated MCP tool catalog to every connected tenant's agent over SignalR. Mirrors
/// <c>CredentialsPusher</c>: it loops connected tenants, pushes, and isolates per-tenant failures so
/// one tenant's error never blocks the others. Re-pushed on host-service catalog change and on agent
/// (re)connect.
/// </summary>
public sealed partial class McpCatalogPusher
{
    private readonly Tenants.TenantRouter tenantRouter;
    private readonly McpHostService hostService;
    private readonly ILogger<McpCatalogPusher> logger;

    public McpCatalogPusher(
        Tenants.TenantRouter tenantRouter,
        McpHostService hostService,
        ILogger<McpCatalogPusher> logger)
    {
        this.tenantRouter = tenantRouter;
        this.hostService = hostService;
        this.logger = logger;
    }

    /// <summary>Pushes the host service's current catalog to all connected tenants.</summary>
    public Task PushCurrentCatalogAsync(CancellationToken cancellationToken)
        => this.PushCatalogAsync(this.hostService.CurrentCatalog, cancellationToken);

    /// <summary>Pushes <paramref name="catalog"/> to all connected tenant agents.</summary>
    public Task PushCatalogAsync(McpToolCatalog catalog, CancellationToken cancellationToken)
    {
        var targets = this.tenantRouter.GetConnectedTenantIds()
            .Select(tenantId => (tenantId, client: this.tenantRouter.GetClient(tenantId)))
            .Where(t => t.client?.IsConnected == true)
            .Select(t => (IMcpCatalogPushTarget)new HubClientMcpCatalogPushTarget(t.tenantId, t.client!))
            .ToList();

        return this.PushToTargetsAsync(catalog, targets, cancellationToken);
    }

    /// <summary>
    /// Pushes <paramref name="catalog"/> to each target, isolating per-tenant failures. Extracted as
    /// the testable seam for the multi-tenant push loop (the router/clients are sealed and not mockable).
    /// </summary>
    internal async Task PushToTargetsAsync(
        McpToolCatalog catalog, IReadOnlyList<IMcpCatalogPushTarget> targets, CancellationToken cancellationToken)
    {
        this.LogPushing(catalog.Tools.Count, targets.Count);

        foreach (var target in targets)
        {
            try
            {
                await target.PushMcpToolCatalogAsync(catalog, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Individual tenant failures should not block others
            catch (Exception ex)
            {
                // SECURITY: content-free — only the exception TYPE, consistent with the
                // Bridge-side MCP redaction guarantee (docs/security.md).
                this.LogPushToTenantFailed(target.TenantId, ex.GetType().Name);
            }
#pragma warning restore CA1031
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pushing MCP tool catalog: {ToolCount} tools to {TenantCount} tenant(s)")]
    private partial void LogPushing(int toolCount, int tenantCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to push MCP tool catalog to tenant '{TenantId}': {Error}")]
    private partial void LogPushToTenantFailed(string tenantId, string error);
}
