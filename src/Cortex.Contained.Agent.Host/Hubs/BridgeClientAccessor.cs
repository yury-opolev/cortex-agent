using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary>
/// Provides access to the connected Bridge's <see cref="IAgentHubClient"/> proxy
/// outside of a SignalR hub method call context. This allows agent tools and the
/// scheduler to send proactive messages through the Bridge without requiring an
/// active <c>Clients.Caller</c> reference.
/// </summary>
/// <remarks>
/// <para>
/// Instead of capturing <c>Clients.Caller</c> directly from <c>OnConnectedAsync</c>,
/// this class stores the connection ID and resolves the client proxy via
/// <see cref="IHubContext{THub,T}"/> on demand. This avoids the SignalR restriction
/// that prevents Client Results from being invoked on a caller proxy captured
/// inside <c>OnConnectedAsync</c>.
/// </para>
/// <para>
/// Only one Bridge instance connects at a time, so a single connection ID suffices.
/// </para>
/// </remarks>
public sealed class BridgeClientAccessor : IBridgeClientProvider
{
    private readonly IHubContext<AgentHub, IAgentHubClient> hubContext;
    private volatile string? connectionId;

    public BridgeClientAccessor(IHubContext<AgentHub, IAgentHubClient> hubContext)
    {
        this.hubContext = hubContext;
    }

    /// <summary>
    /// The current Bridge connection ID, or null if no Bridge is connected.
    /// Used by <see cref="AgentHub"/> to reject duplicate connections.
    /// </summary>
    public string? CurrentConnectionId => this.connectionId;

    /// <summary>
    /// Get the current Bridge client proxy, or null if no Bridge is connected.
    /// Resolves the proxy via <see cref="IHubContext{THub,T}"/> so that Client
    /// Results (methods returning values) work correctly.
    /// </summary>
    public IAgentHubClient? Client =>
        this.connectionId is { } id ? this.hubContext.Clients.Client(id) : null;

    /// <summary>
    /// Store the Bridge's connection ID. Called by <see cref="AgentHub.OnConnectedAsync"/>.
    /// </summary>
    internal void SetConnectionId(string connectionId)
    {
        this.connectionId = connectionId;
    }

    /// <summary>
    /// Clear the Bridge connection if it matches the given ID.
    /// Only clears when the disconnecting connection is the current one,
    /// preventing a stale disconnect from wiping a newer connection.
    /// Called by <see cref="AgentHub.OnDisconnectedAsync"/>.
    /// </summary>
    internal void ClearConnection(string connectionId)
    {
        Interlocked.CompareExchange(ref this.connectionId, null, connectionId);
    }
}
