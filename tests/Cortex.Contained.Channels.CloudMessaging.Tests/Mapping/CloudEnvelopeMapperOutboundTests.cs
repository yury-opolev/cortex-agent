using Cortex.Contained.Channels.CloudMessaging.Envelope;
using Cortex.Contained.Channels.CloudMessaging.Mapping;
using Cortex.Contained.Contracts.Messages;

namespace Cortex.Contained.Channels.CloudMessaging.Tests.Mapping;

/// <summary>
/// Tests for the outbound directions:
/// OutboundMessage → text, stream-chunk, finalize, typing envelopes.
/// </summary>
public class CloudEnvelopeMapperOutboundTests
{
    private static OutboundMessage MakeOutbound(string text = "Reply", bool isThinking = false) => new()
    {
        MessageId = "out-001",
        ConversationId = "conv-xyz",
        ChannelId = "cloud-ch",
        Content = new MessageContent { Text = text },
        IsThinking = isThinking,
    };

    // ── ToTextEnvelope ────────────────────────────────────────────────

    [Fact]
    public void ToTextEnvelope_MapsAllFields()
    {
        var message = MakeOutbound("Agent reply");

        var result = CloudEnvelopeMapper.ToTextEnvelope(message, "tenant-1");

        Assert.Equal(1, result.V);
        Assert.Equal("text", result.Type);
        Assert.Equal("tenant-1", result.TenantId);
        Assert.Equal("conv-xyz", result.ConversationId);
        Assert.Equal("agent", result.From);
        Assert.Equal("out-001", result.Id);
        Assert.Equal("Agent reply", result.Payload?.Text);
    }

    [Fact]
    public void ToTextEnvelope_NullMessage_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CloudEnvelopeMapper.ToTextEnvelope(null!, "tenant-1"));
    }

    // ── ToStreamChunkEnvelope ─────────────────────────────────────────

    [Fact]
    public void ToStreamChunkEnvelope_MapsAllFields()
    {
        var result = CloudEnvelopeMapper.ToStreamChunkEnvelope(
            "conv-xyz", "partial", "tenant-1", "chunk-id-001");

        Assert.Equal("stream-chunk", result.Type);
        Assert.Equal("tenant-1", result.TenantId);
        Assert.Equal("conv-xyz", result.ConversationId);
        Assert.Equal("agent", result.From);
        Assert.Equal("chunk-id-001", result.Id);
        Assert.Equal("partial", result.Payload?.Text);
    }

    // ── ToFinalizeEnvelope ────────────────────────────────────────────

    [Fact]
    public void ToFinalizeEnvelope_NormalMessage_IsThinkingOmitted()
    {
        var message = MakeOutbound("Final answer", isThinking: false);

        var result = CloudEnvelopeMapper.ToFinalizeEnvelope("conv-xyz", message, "tenant-1");

        Assert.Equal("finalize", result.Type);
        Assert.Equal("Final answer", result.Payload?.FinalText);
        Assert.Null(result.Payload?.IsThinking); // false → omitted from JSON
    }

    [Fact]
    public void ToFinalizeEnvelope_ThinkingMessage_IsThinkingTrue()
    {
        var message = MakeOutbound("Thinking narration", isThinking: true);

        var result = CloudEnvelopeMapper.ToFinalizeEnvelope("conv-xyz", message, "tenant-1");

        Assert.Equal("finalize", result.Type);
        Assert.Equal(true, result.Payload?.IsThinking);
    }

    [Fact]
    public void ToFinalizeEnvelope_NullMessage_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CloudEnvelopeMapper.ToFinalizeEnvelope("conv-xyz", null!, "tenant-1"));
    }

    // ── ToTypingEnvelope ──────────────────────────────────────────────

    [Fact]
    public void ToTypingEnvelope_MapsFields_NoPayload()
    {
        var result = CloudEnvelopeMapper.ToTypingEnvelope("conv-xyz", "tenant-1");

        Assert.Equal("typing", result.Type);
        Assert.Equal("tenant-1", result.TenantId);
        Assert.Equal("conv-xyz", result.ConversationId);
        Assert.Equal("agent", result.From);
        Assert.Null(result.Payload);
    }

    // ── Envelope JSON round-trip ──────────────────────────────────────

    private static readonly System.Text.Json.JsonSerializerOptions RoundTripOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void CloudEnvelope_JsonRoundTrip_PreservesAllFields()
    {
        var envelope = new CloudEnvelope
        {
            V = 1,
            Type = "text",
            TenantId = "t1",
            ConversationId = "c1",
            From = "user",
            Id = "i1",
            Ts = 12345L,
            Payload = new CloudPayload { Text = "hello", IsThinking = true },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(envelope, RoundTripOptions);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<CloudEnvelope>(json, RoundTripOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(envelope.V, deserialized.V);
        Assert.Equal(envelope.Type, deserialized.Type);
        Assert.Equal(envelope.TenantId, deserialized.TenantId);
        Assert.Equal(envelope.ConversationId, deserialized.ConversationId);
        Assert.Equal(envelope.From, deserialized.From);
        Assert.Equal(envelope.Id, deserialized.Id);
        Assert.Equal(envelope.Ts, deserialized.Ts);
        Assert.Equal(envelope.Payload!.Text, deserialized.Payload!.Text);
        Assert.Equal(envelope.Payload.IsThinking, deserialized.Payload.IsThinking);
    }

    [Fact]
    public void CloudEnvelope_JsonRoundTrip_ToleratesUnknownFields()
    {
        var json = """
            {
              "v": 1,
              "type": "text",
              "tenantId": "t1",
              "conversationId": "c1",
              "from": "user",
              "id": "i1",
              "ts": 99,
              "payload": { "text": "hi" },
              "futureField": "ignored"
            }
            """;

        var result = System.Text.Json.JsonSerializer.Deserialize<CloudEnvelope>(json, RoundTripOptions);

        Assert.NotNull(result);
        Assert.Equal("text", result.Type);
        Assert.Equal("hi", result.Payload?.Text);
    }
}
