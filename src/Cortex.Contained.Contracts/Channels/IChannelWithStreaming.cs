using Cortex.Contained.Contracts.Messages;

namespace Cortex.Contained.Contracts.Channels;

/// <summary>
/// Extended interface for channels that support message streaming
/// (typing indicators, partial messages).
/// </summary>
public interface IChannelWithStreaming : IChannel
{
    /// <summary>Send a typing indicator.</summary>
    Task SendTypingIndicatorAsync(string conversationId, CancellationToken ct = default);

    /// <summary>Send a partial/streaming message update.</summary>
    Task SendStreamingUpdateAsync(string conversationId, string partialText, CancellationToken ct = default);

    /// <summary>Finalize a streaming message.</summary>
    Task FinalizeStreamingAsync(string conversationId, OutboundMessage finalMessage, CancellationToken ct = default);
}
