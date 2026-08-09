namespace Cortex.Contained.Bridge.Connectors.Media;

/// <summary>The stored bytes and metadata behind an attachment handle.</summary>
public sealed record ConnectorAttachmentContent
{
    /// <summary>The verified MIME type of the content.</summary>
    public required string MimeType { get; init; }

    /// <summary>The stored bytes.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Display file name, or null when none was supplied.</summary>
    public string? FileName { get; init; }

    /// <summary>Alt text / caption, or null when none was supplied.</summary>
    public string? Caption { get; init; }
}

/// <summary>
/// Resolves a Bridge-issued attachment handle to its bytes.
/// </summary>
/// <remarks>
/// Deliberately narrow — the inbound message path only ever needs to turn a handle into content,
/// so it depends on this rather than on the whole attachment store. Implementations MUST scope
/// resolution to the issuing channel: a handle issued to one connector must be indistinguishable
/// from a non-existent one when presented by another.
/// <para>
/// Implementations are expected to return content whose bytes genuinely match the reported
/// <see cref="ConnectorAttachmentContent.MimeType"/> — validation belongs at the point of upload.
/// The inbound path nonetheless re-checks both the allow-list and the magic bytes on read,
/// because policy can be narrowed after an upload and because a store defect must not be the only
/// thing standing between a connector and a disallowed type.
/// </para>
/// </remarks>
public interface IConnectorAttachmentResolver
{
    /// <summary>
    /// Returns the content behind <paramref name="handle"/>, or null when the handle is unknown,
    /// expired, already consumed, or was issued to a different channel. The caller cannot
    /// distinguish those cases, by design.
    /// </summary>
    /// <param name="handle">The connector-supplied handle.</param>
    /// <param name="channelId">The channel presenting the handle.</param>
    ConnectorAttachmentContent? Resolve(string handle, string channelId);
}
