namespace Cortex.Contained.Bridge.Connectors.Media;

/// <summary>
/// Issues a handle for content the Bridge holds on a connector's behalf.
/// </summary>
/// <remarks>
/// Separate from <see cref="IConnectorAttachmentResolver"/> so each caller depends only on the
/// direction it uses: the outbound path issues, the inbound path resolves. One implementation
/// (the attachment store) satisfies both.
/// </remarks>
public interface IConnectorAttachmentIssuer
{
    /// <summary>
    /// Stores <paramref name="content"/> for <paramref name="channelId"/> and returns an opaque
    /// handle, or null when the channel's storage quota would be exceeded.
    /// </summary>
    /// <param name="channelId">The channel the handle is issued to and scoped for.</param>
    /// <param name="content">The content to hold.</param>
    string? Issue(string channelId, ConnectorAttachmentContent content);
}
