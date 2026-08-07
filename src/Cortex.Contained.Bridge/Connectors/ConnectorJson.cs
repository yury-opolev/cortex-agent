using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>Shared <see cref="JsonSerializerOptions"/> for connector frame serialisation.</summary>
public static class ConnectorJson
{
    /// <summary>Cached options used for all connector frame serialisation and deserialisation.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}
