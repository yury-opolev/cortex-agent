using Cortex.Contained.Contracts.Channels;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Creates the appropriate <see cref="PluginChannel"/> subtype based on negotiated capabilities.
/// </summary>
/// <remarks>
/// When <see cref="ChannelCapabilities.SupportsStreaming"/> is <see langword="true"/>,
/// a <see cref="StreamingPluginChannel"/> (which implements <see cref="IChannelWithStreaming"/>)
/// is returned. Otherwise a plain <see cref="PluginChannel"/> is returned so that
/// <see cref="HubMessageDispatcher"/> never attempts to stream to a non-streaming connector.
/// </remarks>
public static class PluginChannelFactory
{
    /// <summary>
    /// Creates a <see cref="PluginChannel"/> (or a <see cref="StreamingPluginChannel"/> when
    /// <paramref name="capabilities"/> has <see cref="ChannelCapabilities.SupportsStreaming"/>
    /// set to <see langword="true"/>).
    /// </summary>
    /// <param name="pluginKey">Connector type key (e.g. <c>terminal</c>).</param>
    /// <param name="instanceId">Connector instance identifier (e.g. <c>default</c>).</param>
    /// <param name="capabilities">Capabilities negotiated during the <c>hello</c> handshake.</param>
    /// <param name="displayName">Human-readable name advertised by the connector.</param>
    /// <param name="loggerFactory">Logger factory used to create typed loggers.</param>
    /// <returns>
    /// A <see cref="StreamingPluginChannel"/> when streaming is supported; otherwise a plain
    /// <see cref="PluginChannel"/>.
    /// </returns>
    public static PluginChannel Create(
        string pluginKey,
        string instanceId,
        ChannelCapabilities capabilities,
        string displayName,
        ILoggerFactory loggerFactory)
    {
        if (capabilities.SupportsStreaming)
        {
            return new StreamingPluginChannel(pluginKey, instanceId, capabilities, displayName, loggerFactory);
        }

        return new PluginChannel(
            pluginKey,
            instanceId,
            capabilities,
            displayName,
            loggerFactory.CreateLogger<PluginChannel>());
    }
}
