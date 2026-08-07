using Cortex.Contained.Bridge.Connectors.Protocol;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Messages;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// A <see cref="PluginChannel"/> that additionally implements <see cref="IChannelWithStreaming"/>
/// for connectors that negotiated <c>streaming: true</c>.
/// </summary>
/// <remarks>
/// <see cref="HubMessageDispatcher"/> decides whether to stream by checking <c>channel is IChannelWithStreaming</c>,
/// so streaming must be expressed at the type level — a non-streaming connector MUST NOT implement this interface.
/// </remarks>
public sealed partial class StreamingPluginChannel : PluginChannel, IChannelWithStreaming
{
    private readonly ILogger<StreamingPluginChannel> logger;

    /// <summary>Initialises a new <see cref="StreamingPluginChannel"/>.</summary>
    public StreamingPluginChannel(
        string pluginKey,
        string instanceId,
        ChannelCapabilities capabilities,
        string displayName,
        ILoggerFactory loggerFactory)
        : base(pluginKey, instanceId, capabilities, displayName, loggerFactory.CreateLogger<PluginChannel>())
    {
        this.logger = loggerFactory.CreateLogger<StreamingPluginChannel>();
    }

    /// <inheritdoc />
    public async Task SendTypingIndicatorAsync(string conversationId, CancellationToken ct = default)
    {
        var sink = this.FrameSink;
        if (sink is null)
        {
            this.LogFrameSinkNull(this.ChannelId, ConnectorFrameTypes.Typing);
            return;
        }

        try
        {
            var json = ConnectorFrame.Serialize(ConnectorFrameTypes.Typing, new ConnectorTypingPayload
            {
                ConversationId = conversationId,
            });
            await sink(json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogFrameSinkFailed(this.ChannelId, ConnectorFrameTypes.Typing, ex);
        }
    }

    /// <inheritdoc />
    public async Task SendStreamingUpdateAsync(string conversationId, string partialText, CancellationToken ct = default)
    {
        var sink = this.FrameSink;
        if (sink is null)
        {
            this.LogFrameSinkNull(this.ChannelId, ConnectorFrameTypes.Stream);
            return;
        }

        try
        {
            var json = ConnectorFrame.Serialize(ConnectorFrameTypes.Stream, new ConnectorStreamPayload
            {
                ConversationId = conversationId,
                Delta = partialText,
            });
            await sink(json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogFrameSinkFailed(this.ChannelId, ConnectorFrameTypes.Stream, ex);
        }
    }

    /// <inheritdoc />
    public async Task FinalizeStreamingAsync(string conversationId, OutboundMessage finalMessage, CancellationToken ct = default)
    {
        try
        {
            var result = await this.SendMessageAsync(finalMessage, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                // SendMessageAsync reports a missing sink or a dead socket by returning an
                // error result rather than throwing, so without this the final message of a
                // streamed turn would be dropped in silence.
                this.LogFinalizeRejected(this.ChannelId, result.ErrorMessage ?? "unknown error");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogFinalizeFailed(this.ChannelId, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "StreamingPluginChannel {ChannelId}: frame sink is null, dropping {FrameType} frame.")]
    private partial void LogFrameSinkNull(string channelId, string frameType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "StreamingPluginChannel {ChannelId}: frame sink threw for {FrameType} frame.")]
    private partial void LogFrameSinkFailed(string channelId, string frameType, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "StreamingPluginChannel {ChannelId}: FinalizeStreamingAsync failed.")]
    private partial void LogFinalizeFailed(string channelId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "StreamingPluginChannel {ChannelId}: final streamed message was not delivered: {ErrorMessage}")]
    private partial void LogFinalizeRejected(string channelId, string errorMessage);
}
