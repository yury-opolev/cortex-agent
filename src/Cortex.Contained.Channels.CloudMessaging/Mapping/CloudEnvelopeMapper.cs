using Cortex.Contained.Channels.CloudMessaging.Envelope;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Messages;

namespace Cortex.Contained.Channels.CloudMessaging.Mapping;

/// <summary>
/// Pure, stateless mapper between wire <see cref="CloudEnvelope"/> frames and the
/// <see cref="WebChat.WebChatChannel"/> seam (<see cref="InboundMessage"/> /
/// <see cref="OutboundMessage"/> / streaming events).
/// All methods are static so they are testable without any DI setup.
/// </summary>
public static class CloudEnvelopeMapper
{
    // ── Inbound: envelope → InboundMessage ───────────────────────────

    /// <summary>
    /// Maps an inbound "text" envelope from the cloud service into an
    /// <see cref="InboundMessage"/> suitable for
    /// <see cref="WebChat.WebChatChannel.ReceiveFromBrowserAsync"/>.
    /// Returns null when the envelope type is not "text" or the payload is missing.
    /// </summary>
    public static InboundMessage? ToInboundMessage(CloudEnvelope envelope, string channelId)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!string.Equals(envelope.Type, EnvelopeTypes.Text, StringComparison.Ordinal))
        {
            return null;
        }

        var text = envelope.Payload?.Text;
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return new InboundMessage
        {
            MessageId = envelope.Id,
            ConversationId = envelope.ConversationId,
            ChannelId = channelId,
            ChannelType = ChannelType.CloudMessaging,
            Sender = new SenderInfo
            {
                Id = envelope.From,
                DisplayName = null,
                IsVerified = false,
            },
            Content = new MessageContent
            {
                Text = text,
            },
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(envelope.Ts),
            IsGroup = false,
        };
    }

    // ── Outbound: OutboundMessage → text envelope ─────────────────────

    /// <summary>
    /// Maps an outbound <see cref="OutboundMessage"/> (full/non-streaming) to a
    /// "text" <see cref="CloudEnvelope"/> destined for the tenant group.
    /// </summary>
    public static CloudEnvelope ToTextEnvelope(OutboundMessage message, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new CloudEnvelope
        {
            V = 1,
            Type = EnvelopeTypes.Text,
            TenantId = tenantId,
            ConversationId = message.ConversationId,
            From = EnvelopeFrom.Agent,
            Id = message.MessageId,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = new CloudPayload
            {
                Text = message.Content.Text,
            },
        };
    }

    // ── Outbound: streaming chunk ─────────────────────────────────────

    /// <summary>
    /// Maps a streaming partial update to a "stream-chunk" envelope.
    /// </summary>
    public static CloudEnvelope ToStreamChunkEnvelope(
        string conversationId,
        string partialText,
        string tenantId,
        string messageId)
    {
        return new CloudEnvelope
        {
            V = 1,
            Type = EnvelopeTypes.StreamChunk,
            TenantId = tenantId,
            ConversationId = conversationId,
            From = EnvelopeFrom.Agent,
            Id = messageId,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = new CloudPayload
            {
                Text = partialText,
            },
        };
    }

    // ── Outbound: finalize ────────────────────────────────────────────

    /// <summary>
    /// Maps a streaming finalize event to a "finalize" envelope.
    /// <see cref="OutboundMessage.IsThinking"/> is forwarded as <c>isThinking</c>
    /// so the browser can collapse the thinking lane.
    /// </summary>
    public static CloudEnvelope ToFinalizeEnvelope(
        string conversationId,
        OutboundMessage finalMessage,
        string tenantId)
    {
        ArgumentNullException.ThrowIfNull(finalMessage);

        return new CloudEnvelope
        {
            V = 1,
            Type = EnvelopeTypes.Finalize,
            TenantId = tenantId,
            ConversationId = conversationId,
            From = EnvelopeFrom.Agent,
            Id = finalMessage.MessageId,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = new CloudPayload
            {
                FinalText = finalMessage.Content.Text,
                IsThinking = finalMessage.IsThinking ? true : null, // omit false to keep JSON clean
            },
        };
    }

    // ── Outbound: typing indicator ────────────────────────────────────

    /// <summary>
    /// Maps a typing indicator event to a "typing" envelope.
    /// </summary>
    public static CloudEnvelope ToTypingEnvelope(string conversationId, string tenantId)
    {
        return new CloudEnvelope
        {
            V = 1,
            Type = EnvelopeTypes.Typing,
            TenantId = tenantId,
            ConversationId = conversationId,
            From = EnvelopeFrom.Agent,
            Id = Guid.NewGuid().ToString("N"),
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = null,
        };
    }
}
