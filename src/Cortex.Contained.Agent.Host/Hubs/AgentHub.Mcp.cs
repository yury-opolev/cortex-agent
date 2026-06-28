namespace Cortex.Contained.Agent.Host.Hubs;

using Cortex.Contained.Contracts.Hub;

public sealed partial class AgentHub
{
    // Temporary no-op: the real catalog-registration handler (delegating to
    // McpToolStore) lands together with its DI wiring in the catalog-registration task.
    /// <inheritdoc />
    public Task UpdateMcpToolCatalog(McpToolCatalog catalog) => Task.CompletedTask;
}
