using System.Text.Json.Serialization;

namespace Cortex.Contained.Channels.CloudMessaging.Negotiate;

/// <summary>
/// JSON body returned by <c>POST {serviceBaseUrl}/negotiate-bridge</c>.
/// The service returns a path-relative SignalR hub URL and the tenant list
/// for this bridge (shape: <c>{ "hubUrl": "/hub", "tenants": ["t1","t2"] }</c>).
/// </summary>
internal sealed class NegotiateResponse
{
    [JsonPropertyName("hubUrl")]
    public required string HubUrl { get; init; }

    [JsonPropertyName("tenants")]
    public required IReadOnlyList<string> Tenants { get; init; }
}
