using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cortex.Contained.Bridge.Hub;

/// <summary>
/// MCP plugin-system extensions for <see cref="HubClient"/>:
///   - Outbound push of the namespaced tool catalog to the agent (<see cref="IAgentHub.UpdateMcpToolCatalog"/>).
///   - Inbound callback for agent-initiated tool invocations (<see cref="IAgentHubClient.InvokeMcpTool"/>),
///     surfaced as the <see cref="OnInvokeMcpTool"/> event which the host wires to the MCP host service.
/// </summary>
public sealed partial class HubClient
{
    /// <summary>Agent → Bridge: invoke an MCP tool. The host attaches auth and calls the real server.</summary>
    public event Func<McpToolInvocation, Task<McpToolResult>>? OnInvokeMcpTool;

    /// <summary>Bridge → Agent: replace the agent's MCP tool catalog with <paramref name="catalog"/>.</summary>
    public async Task PushMcpToolCatalogAsync(McpToolCatalog catalog, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(nameof(IAgentHub.UpdateMcpToolCatalog), catalog, cancellationToken).ConfigureAwait(false);
    }

    private void RegisterMcpCallbacks(HubConnection connection)
    {
        connection.On<McpToolInvocation, McpToolResult>(
            nameof(IAgentHubClient.InvokeMcpTool),
            invocation => this.OnInvokeMcpTool?.Invoke(invocation)
                ?? Task.FromResult(McpToolResult.Fail("No MCP host handler registered on the Bridge.")));
    }
}
