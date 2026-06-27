using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Agent.Host.Reminders;

/// <summary>
/// Production implementation of <see cref="IVoiceCueDeliverer"/> that routes
/// pre-composed voice cues through the Bridge proactive-message path —
/// the same path used by <c>SendMessageTool</c>.
/// </summary>
internal sealed partial class BridgeVoiceCueDeliverer : IVoiceCueDeliverer
{
    private readonly BridgeClientAccessor bridgeClientAccessor;
    private readonly MessageStore messageStore;
    private readonly ILogger<BridgeVoiceCueDeliverer> logger;

    public BridgeVoiceCueDeliverer(
        BridgeClientAccessor bridgeClientAccessor,
        MessageStore messageStore,
        ILogger<BridgeVoiceCueDeliverer> logger)
    {
        this.bridgeClientAccessor = bridgeClientAccessor;
        this.messageStore = messageStore;
        this.logger = logger;
    }

    public async Task SpeakAsync(string conversationId, string channelId, string text, CancellationToken cancellationToken)
    {
        var client = this.bridgeClientAccessor.Client;
        if (client is null)
        {
            this.LogBridgeNotConnected(conversationId, channelId);
            return;
        }

        try
        {
            var proactiveMessage = new ProactiveMessage
            {
                Text = text,
                ChannelId = channelId,
                CorrelationId = Guid.NewGuid().ToString("N"),
            };

            var result = await client.OnProactiveMessage(proactiveMessage).ConfigureAwait(false);

            if (result.Success)
            {
                await this.messageStore.SaveMessageAsync(
                    userId: "assistant",
                    channelId: channelId,
                    role: "assistant",
                    content: text,
                    timestamp: DateTimeOffset.UtcNow,
                    category: MessageCategory.Proactive,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                this.LogCueDelivered(conversationId, channelId, text);
            }
            else
            {
                this.LogDeliveryFailed(conversationId, channelId, result.Error ?? "OnProactiveMessage returned failure");
            }
        }
#pragma warning disable CA1031 // Must not throw on transient failures
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogDeliveryFailed(conversationId, channelId, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Voice cue delivered to {ConversationId}/{ChannelId}: '{Text}'")]
    private partial void LogCueDelivered(string conversationId, string channelId, string text);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bridge not connected; voice cue dropped for {ConversationId}/{ChannelId}")]
    private partial void LogBridgeNotConnected(string conversationId, string channelId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Voice cue delivery failed for {ConversationId}/{ChannelId}: {ErrorMessage}")]
    private partial void LogDeliveryFailed(string conversationId, string channelId, string errorMessage);
}
