using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// A single frame exchanged over the connector WebSocket. Every message is
/// <c>{"type":"...","payload":{...}}</c>.
/// </summary>
public sealed record ConnectorFrame
{
    /// <summary>The frame type identifier.</summary>
    public required string Type { get; init; }

    /// <summary>Raw JSON payload; defaults to an empty object when absent from the wire.</summary>
    public JsonElement Payload { get; init; }

    /// <summary>
    /// Serialise a frame with a typed payload to a JSON string.
    /// </summary>
    public static string Serialize<TPayload>(string type, TPayload payload)
    {
        var wrapper = new FrameWrapper<TPayload> { Type = type, Payload = payload };
        return JsonSerializer.Serialize(wrapper, ConnectorJson.Options);
    }

    /// <summary>
    /// Serialise a frame with no payload (e.g. <c>ping</c>). Emits <c>"payload":{}</c>.
    /// </summary>
    public static string Serialize(string type)
    {
        return $"{{\"type\":{JsonSerializer.Serialize(type)},\"payload\":{{}}}}";
    }

    private sealed class FrameWrapper<TPayload>
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("payload")]
        public required TPayload Payload { get; init; }
    }
}
