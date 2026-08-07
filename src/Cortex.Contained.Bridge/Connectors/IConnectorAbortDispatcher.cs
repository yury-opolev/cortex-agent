namespace Cortex.Contained.Bridge.Connectors;

/// <summary>Asks the agent to cancel an in-flight generation on behalf of a connector.</summary>
public interface IConnectorAbortDispatcher
{
    /// <summary>Asks the agent to cancel the in-flight generation for a conversation on a plugin channel.</summary>
    /// <param name="channelId">The channel id of the plugin channel requesting the abort.</param>
    /// <param name="conversationId">The conversation to abort.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AbortAsync(string channelId, string conversationId, CancellationToken ct);
}
