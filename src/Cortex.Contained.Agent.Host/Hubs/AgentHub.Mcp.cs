namespace Cortex.Contained.Agent.Host.Hubs;

using Cortex.Contained.Contracts.Hub;

public sealed partial class AgentHub
{
    /// <inheritdoc />
    public Task UpdateMcpToolCatalog(McpToolCatalog catalog)
    {
        var normalized = catalog.Tools is null ? new McpToolCatalog() : catalog;
        this.mcpToolStore.Update(normalized);
        this.LogMcpCatalogUpdated(normalized.Tools.Count);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP catalog updated: {Count} tools")]
    private partial void LogMcpCatalogUpdated(int count);
}
