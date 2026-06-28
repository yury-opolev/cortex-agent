using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>Adapts a connected <see cref="Hub.HubClient"/> to <see cref="IMcpCatalogPushTarget"/>.</summary>
internal sealed class HubClientMcpCatalogPushTarget : IMcpCatalogPushTarget
{
    private readonly Hub.HubClient client;

    public HubClientMcpCatalogPushTarget(string tenantId, Hub.HubClient client)
    {
        this.TenantId = tenantId;
        this.client = client;
    }

    public string TenantId { get; }

    public Task PushMcpToolCatalogAsync(McpToolCatalog catalog, CancellationToken cancellationToken)
        => this.client.PushMcpToolCatalogAsync(catalog, cancellationToken);
}
