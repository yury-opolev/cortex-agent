using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Channels.WebChat;

/// <summary>
/// Abstraction over the agent hub operations that <see cref="WebChatHub"/>
/// needs. Implemented by the Bridge using <c>HubClient</c>, keeping the
/// WebChat library decoupled from Bridge internals.
/// Conversation CRUD methods removed — history is now served via Bridge REST API.
/// </summary>
public interface IWebChatHubProxy
{
    /// <summary>Get agent status.</summary>
    Task<AgentStatusInfo> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Abort an in-progress generation.</summary>
    Task AbortGenerationAsync(string conversationId, CancellationToken cancellationToken);
}
