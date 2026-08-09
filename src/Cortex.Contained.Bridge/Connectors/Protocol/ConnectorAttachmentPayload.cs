using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors.Protocol;

/// <summary>
/// A single media attachment on a connector message. The same shape is used in both directions,
/// so a connector parses attachments identically on <c>inbound</c> and <c>outbound</c>.
/// </summary>
/// <remarks>
/// Exactly one of <see cref="Data"/> or <see cref="Handle"/> carries the bytes. There is
/// deliberately no <c>url</c> field: dereferencing a connector-supplied location would hand an
/// untrusted local process a fetch primitive inside the Bridge's credential boundary
/// (<c>file:///…/secrets.json</c> exfiltration, or SSRF to <c>169.254.169.254</c>). The
/// <see cref="Url"/> property below exists ONLY so such a frame can be positively detected and
/// rejected rather than silently ignored by the deserialiser.
/// </remarks>
public sealed record ConnectorAttachmentPayload
{
    /// <summary>Declared MIME type. Verified against the content's magic bytes, never trusted.</summary>
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; init; }

    /// <summary>Display file name; sanitised and truncated by the Bridge.</summary>
    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }

    /// <summary>Alt text / caption.</summary>
    [JsonPropertyName("caption")]
    public string? Caption { get; init; }

    /// <summary>Base64-encoded bytes for small attachments. Mutually exclusive with <see cref="Handle"/>.</summary>
    [JsonPropertyName("data")]
    public string? Data { get; init; }

    /// <summary>Opaque Bridge-issued handle for large attachments. Mutually exclusive with <see cref="Data"/>.</summary>
    [JsonPropertyName("handle")]
    public string? Handle { get; init; }

    /// <summary>Declared size in bytes. A hint only — the Bridge verifies the actual length.</summary>
    [JsonPropertyName("sizeBytes")]
    public long? SizeBytes { get; init; }

    /// <summary>
    /// Always rejected. Present solely so a frame carrying a URL fails loudly instead of having
    /// the field dropped by the deserialiser. See the remarks on this type.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }
}
