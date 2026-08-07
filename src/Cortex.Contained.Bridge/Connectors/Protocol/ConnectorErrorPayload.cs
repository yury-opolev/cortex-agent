using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors.Protocol;

/// <summary>Payload of an <c>error</c> frame sent by the Bridge.</summary>
public sealed record ConnectorErrorPayload
{
    /// <summary>Machine-readable error code; one of <see cref="ConnectorErrorCodes"/>.</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    /// <summary>Human-readable description of the error.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
