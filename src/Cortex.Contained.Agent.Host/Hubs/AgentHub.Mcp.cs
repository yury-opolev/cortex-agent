namespace Cortex.Contained.Agent.Host.Hubs;

using Cortex.Contained.Contracts.Hub;

public sealed partial class AgentHub
{
    /// <inheritdoc />
    public Task UpdateMcpToolCatalog(McpToolCatalog catalog)
    {
        // Tolerate a null catalog/Tools defensively even though the hub contract is non-nullable;
        // McpToolStore.Update normalizes null to an empty set.
        this.mcpToolStore.Update(catalog);
        this.LogMcpCatalogUpdated(catalog?.Tools?.Count ?? 0);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP catalog updated: {Count} tools")]
    private partial void LogMcpCatalogUpdated(int count);
}
