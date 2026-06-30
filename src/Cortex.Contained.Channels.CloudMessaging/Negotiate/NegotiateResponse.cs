using System.Text.Json.Serialization;

namespace Cortex.Contained.Channels.CloudMessaging.Negotiate;

/// <summary>
/// JSON body returned by <c>POST {serviceBaseUrl}/negotiate-bridge</c>.
/// </summary>
internal sealed class NegotiateResponse
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("tenants")]
    public required IReadOnlyList<string> Tenants { get; init; }
}
