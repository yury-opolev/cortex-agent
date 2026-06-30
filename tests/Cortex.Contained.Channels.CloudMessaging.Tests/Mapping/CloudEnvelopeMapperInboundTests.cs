using Cortex.Contained.Channels.CloudMessaging.Envelope;
using Cortex.Contained.Channels.CloudMessaging.Mapping;
using Cortex.Contained.Contracts.Channels;

namespace Cortex.Contained.Channels.CloudMessaging.Tests.Mapping;

/// <summary>
/// Tests for the inbound direction: "text" <see cref="CloudEnvelope"/> → <see cref="Cortex.Contained.Contracts.Messages.InboundMessage"/>.
/// </summary>
public class CloudEnvelopeMapperInboundTests
{
    private static CloudEnvelope MakeTextEnvelope(string text = "Hello") => new()
    {
        V = 1,
        Type = "text",
        TenantId = "tenant-1",
        ConversationId = "conv-abc",
        From = "user",
        Id = "msg-001",
        Ts = 1_700_000_000_000L,
        Payload = new CloudPayload { Text = text },
    };

    [Fact]
    public void ToInboundMessage_TextEnvelope_MapsAllFields()
    {
        var envelope = MakeTextEnvelope("Hello world");

        var result = CloudEnvelopeMapper.ToInboundMessage(envelope, "cloud-ch");

        Assert.NotNull(result);
        Assert.Equal("msg-001", result.MessageId);
        Assert.Equal("conv-abc", result.ConversationId);
        Assert.Equal("cloud-ch", result.ChannelId);
        Assert.Equal(ChannelType.CloudMessaging, result.ChannelType);
        Assert.Equal("user", result.Sender.Id);
        Assert.Equal("Hello world", result.Content.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L), result.Timestamp);
        Assert.False(result.IsGroup);
    }

    [Fact]
    public void ToInboundMessage_NonTextType_ReturnsNull()
    {
        var envelope = new CloudEnvelope
        {
            V = 1,
            Type = "typing",
            TenantId = "tenant-1",
            ConversationId = "conv-abc",
            From = "user",
            Id = "msg-002",
            Ts = 1_700_000_000_000L,
            Payload = null,
        };

        var result = CloudEnvelopeMapper.ToInboundMessage(envelope, "cloud-ch");

        Assert.Null(result);
    }

    [Fact]
    public void ToInboundMessage_MissingPayloadText_ReturnsNull()
    {
        var envelope = new CloudEnvelope
        {
            V = 1,
            Type = "text",
            TenantId = "tenant-1",
            ConversationId = "conv-abc",
            From = "user",
            Id = "msg-003",
            Ts = 1_700_000_000_000L,
            Payload = new CloudPayload { Text = null },
        };

        var result = CloudEnvelopeMapper.ToInboundMessage(envelope, "cloud-ch");

        Assert.Null(result);
    }

    [Fact]
    public void ToInboundMessage_NullEnvelope_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CloudEnvelopeMapper.ToInboundMessage(null!, "cloud-ch"));
    }

    [Fact]
    public void ToInboundMessage_EmptyPayloadText_ReturnsNull()
    {
        var envelope = MakeTextEnvelope(string.Empty);

        var result = CloudEnvelopeMapper.ToInboundMessage(envelope, "cloud-ch");

        Assert.Null(result);
    }
}
