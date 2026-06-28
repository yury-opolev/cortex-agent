using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>One connected tenant the MCP catalog is pushed to. Abstracted for unit-testing the push loop.</summary>
internal interface IMcpCatalogPushTarget
{
    /// <summary>The tenant id, used for per-tenant failure logging.</summary>
    string TenantId { get; }

    /// <summary>Pushes the catalog to this tenant's agent.</summary>
    Task PushMcpToolCatalogAsync(McpToolCatalog catalog, CancellationToken cancellationToken);
}
