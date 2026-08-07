using Cortex.Contained.Contracts.Channels;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Utilities for constructing and validating plugin channel identifiers.
/// </summary>
/// <remarks>
/// The validation rules live in <see cref="PluginChannelId"/> (Contracts) because both
/// the Bridge and the Agent Host need them. This class is a thin forwarder that preserves
/// the existing public API so all Bridge code and tests continue to compile unchanged.
/// </remarks>
public static class ConnectorChannelId
{
    /// <inheritdoc cref="PluginChannelId.Prefix"/>
    public const string Prefix = PluginChannelId.Prefix;

    /// <inheritdoc cref="PluginChannelId.Create"/>
    public static string Create(string key, string instanceId) => PluginChannelId.Create(key, instanceId);

    /// <inheritdoc cref="PluginChannelId.TryParse"/>
    public static bool TryParse(string channelId, out string? key, out string? instanceId) =>
        PluginChannelId.TryParse(channelId, out key, out instanceId);

    /// <inheritdoc cref="PluginChannelId.IsPluginChannelId"/>
    public static bool IsPluginChannelId(string channelId) => PluginChannelId.IsPluginChannelId(channelId);

    /// <inheritdoc cref="PluginChannelId.IsValidSegment"/>
    public static bool IsValidSegment(string? segment) => PluginChannelId.IsValidSegment(segment);

    /// <inheritdoc cref="PluginChannelId.Normalize"/>
    public static string? Normalize(string? segment) => PluginChannelId.Normalize(segment);
}
