using System.Text.Json;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>Parses raw WebSocket text messages into <see cref="ConnectorFrame"/> instances.</summary>
public static class ConnectorFrameParser
{
    private static readonly JsonDocument emptyPayloadDocument = JsonDocument.Parse("{}");

    private static readonly HashSet<string> connectorFrameTypes = new(StringComparer.Ordinal)
    {
        ConnectorFrameTypes.Hello,
        ConnectorFrameTypes.Inbound,
        ConnectorFrameTypes.Abort,
        ConnectorFrameTypes.Pong,
    };

    /// <summary>
    /// Returns true if <paramref name="type"/> is a frame type that a connector may send to the Bridge.
    /// </summary>
    public static bool IsConnectorFrameType(string type) => connectorFrameTypes.Contains(type);

    /// <summary>
    /// Attempts to parse a raw JSON string into a <see cref="ConnectorFrame"/>.
    /// </summary>
    /// <param name="json">Raw WebSocket text message.</param>
    /// <param name="frame">Parsed frame on success; null on failure.</param>
    /// <param name="errorCode">One of <see cref="ConnectorErrorCodes"/> on failure; null on success.</param>
    /// <param name="errorMessage">Human-readable description on failure; null on success.</param>
    /// <returns>True when parsing succeeded; false otherwise.</returns>
    public static bool TryParse(
        string? json,
        out ConnectorFrame? frame,
        out string? errorCode,
        out string? errorMessage)
    {
        frame = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            errorCode = ConnectorErrorCodes.MalformedFrame;
            errorMessage = "Frame is null, empty, or whitespace.";
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            errorCode = ConnectorErrorCodes.MalformedFrame;
            errorMessage = ex.Message;
            return false;
        }

        if (doc.RootElement.ValueKind is not JsonValueKind.Object)
        {
            doc.Dispose();
            errorCode = ConnectorErrorCodes.MalformedFrame;
            errorMessage = "Frame root must be a JSON object.";
            return false;
        }

        if (!doc.RootElement.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind is not JsonValueKind.String)
        {
            doc.Dispose();
            errorCode = ConnectorErrorCodes.InvalidPayload;
            errorMessage = "Frame must have a 'type' string property.";
            return false;
        }

        var type = typeProp.GetString();
        if (string.IsNullOrWhiteSpace(type))
        {
            doc.Dispose();
            errorCode = ConnectorErrorCodes.InvalidPayload;
            errorMessage = "Frame 'type' must not be empty or whitespace.";
            return false;
        }

        if (!IsConnectorFrameType(type))
        {
            doc.Dispose();
            errorCode = ConnectorErrorCodes.UnknownFrameType;
            errorMessage = $"Unknown connector frame type: '{type}'.";
            return false;
        }

        JsonElement payload;
        if (doc.RootElement.TryGetProperty("payload", out var payloadProp))
        {
            if (payloadProp.ValueKind is not JsonValueKind.Object)
            {
                doc.Dispose();
                errorCode = ConnectorErrorCodes.InvalidPayload;
                errorMessage = "Frame 'payload' must be a JSON object when present.";
                return false;
            }

            payload = payloadProp.Clone();
        }
        else
        {
            payload = emptyPayloadDocument.RootElement;
        }

        doc.Dispose();

        frame = new ConnectorFrame { Type = type, Payload = payload };
        errorCode = null;
        errorMessage = null;
        return true;
    }

    /// <summary>
    /// Attempts to deserialise <see cref="ConnectorFrame.Payload"/> into a typed DTO.
    /// </summary>
    /// <typeparam name="TPayload">The expected payload type.</typeparam>
    /// <param name="frame">The frame whose payload to deserialise.</param>
    /// <param name="payload">The deserialised payload on success; null on failure.</param>
    /// <param name="errorMessage">Error description on failure; null on success.</param>
    /// <returns>True when deserialisation succeeded and the result is non-null.</returns>
    public static bool TryDeserializePayload<TPayload>(
        ConnectorFrame frame,
        out TPayload? payload,
        out string? errorMessage)
    {
        try
        {
            payload = frame.Payload.Deserialize<TPayload>(ConnectorJson.Options);
        }
        catch (JsonException ex)
        {
            payload = default;
            errorMessage = ex.Message;
            return false;
        }

        if (payload is null)
        {
            errorMessage = "payload was null";
            return false;
        }

        errorMessage = null;
        return true;
    }
}
