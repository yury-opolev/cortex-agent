using Cortex.Contained.Bridge.Connectors;

namespace Cortex.Contained.Bridge.Tests.Connectors;

public class ConnectorFrameParserTests
{
    // ── Null / empty / whitespace ─────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_NullEmptyWhitespace_ReturnsMalformedFrame(string? input)
    {
        var result = ConnectorFrameParser.TryParse(input!, out var frame, out var errorCode, out _);

        Assert.False(result);
        Assert.Null(frame);
        Assert.Equal(ConnectorErrorCodes.MalformedFrame, errorCode);
    }

    // ── Invalid JSON ──────────────────────────────────────────────────────

    [Fact]
    public void TryParse_InvalidJson_ReturnsMalformedFrame_DoesNotThrow()
    {
        var result = ConnectorFrameParser.TryParse("not json }{", out _, out var errorCode, out _);

        Assert.False(result);
        Assert.Equal(ConnectorErrorCodes.MalformedFrame, errorCode);
    }

    // ── Valid JSON but not an object ──────────────────────────────────────

    [Theory]
    [InlineData("[]")]
    [InlineData("\"str\"")]
    [InlineData("5")]
    [InlineData("true")]
    public void TryParse_JsonNotObject_ReturnsMalformedFrame(string input)
    {
        var result = ConnectorFrameParser.TryParse(input, out _, out var errorCode, out _);

        Assert.False(result);
        Assert.Equal(ConnectorErrorCodes.MalformedFrame, errorCode);
    }

    // ── Missing or invalid 'type' ─────────────────────────────────────────

    [Fact]
    public void TryParse_MissingType_ReturnsInvalidPayload()
    {
        var result = ConnectorFrameParser.TryParse("{\"payload\":{}}", out _, out var errorCode, out _);

        Assert.False(result);
        Assert.Equal(ConnectorErrorCodes.InvalidPayload, errorCode);
    }

    [Fact]
    public void TryParse_TypeNotString_ReturnsInvalidPayload()
    {
        var result = ConnectorFrameParser.TryParse("{\"type\":42,\"payload\":{}}", out _, out var errorCode, out _);

        Assert.False(result);
        Assert.Equal(ConnectorErrorCodes.InvalidPayload, errorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_TypeEmptyOrWhitespace_ReturnsInvalidPayload(string type)
    {
        var json = $"{{\"type\":\"{type}\",\"payload\":{{}}}}";
        var result = ConnectorFrameParser.TryParse(json, out _, out var errorCode, out _);

        Assert.False(result);
        Assert.Equal(ConnectorErrorCodes.InvalidPayload, errorCode);
    }

    // ── Unknown type ──────────────────────────────────────────────────────

    [Fact]
    public void TryParse_UnknownType_ReturnsUnknownFrameType()
    {
        var result = ConnectorFrameParser.TryParse("{\"type\":\"stream\",\"payload\":{}}", out _, out var errorCode, out _);

        Assert.False(result);
        Assert.Equal(ConnectorErrorCodes.UnknownFrameType, errorCode);
    }

    // ── Missing payload treated as empty object ───────────────────────────

    [Fact]
    public void TryParse_MissingPayload_Succeeds_PongFrame()
    {
        var result = ConnectorFrameParser.TryParse("{\"type\":\"pong\"}", out var frame, out _, out _);

        Assert.True(result);
        Assert.NotNull(frame);
        Assert.Equal("pong", frame.Type);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, frame.Payload.ValueKind);
    }

    // ── Payload not a JSON object ─────────────────────────────────────────

    [Fact]
    public void TryParse_PayloadNotObject_ReturnsInvalidPayload()
    {
        var result = ConnectorFrameParser.TryParse("{\"type\":\"hello\",\"payload\":[]}", out _, out var errorCode, out _);

        Assert.False(result);
        Assert.Equal(ConnectorErrorCodes.InvalidPayload, errorCode);
    }

    // ── Happy path ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hello")]
    [InlineData("inbound")]
    [InlineData("abort")]
    [InlineData("pong")]
    public void TryParse_ValidKnownFrame_Succeeds(string type)
    {
        var json = $"{{\"type\":\"{type}\",\"payload\":{{}}}}";
        var result = ConnectorFrameParser.TryParse(json, out var frame, out var errorCode, out var errorMessage);

        Assert.True(result);
        Assert.NotNull(frame);
        Assert.Equal(type, frame.Type);
        Assert.Null(errorCode);
        Assert.Null(errorMessage);
    }

    // ── TryDeserializePayload ─────────────────────────────────────────────

    [Fact]
    public void TryDeserializePayload_ValidPayload_ReturnsDeserialized()
    {
        var parsed = ConnectorFrameParser.TryParse(
            "{\"type\":\"hello\",\"payload\":{\"key\":\"terminal\",\"instanceId\":\"default\"}}",
            out var frame, out _, out _);
        Assert.True(parsed);

        var ok = ConnectorFrameParser.TryDeserializePayload<Bridge.Connectors.Protocol.ConnectorHelloPayload>(
            frame!, out var payload, out _);

        Assert.True(ok);
        Assert.NotNull(payload);
        Assert.Equal("terminal", payload.Key);
        Assert.Equal("default", payload.InstanceId);
    }
}
