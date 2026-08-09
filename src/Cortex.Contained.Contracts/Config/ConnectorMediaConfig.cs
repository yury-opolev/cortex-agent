using System.Collections.Immutable;

namespace Cortex.Contained.Contracts.Config;

/// <summary>
/// Media-attachment policy for the connector plugin system.
/// </summary>
/// <remarks>
/// Defaults deliberately mirror what the agent itself already allows (see the agent host's
/// <c>AttachmentLoader</c>: 8 MB per image, PNG/JPEG/GIF/WebP) so a connector is never the
/// narrower of the two limits by accident.
/// <para>
/// <see cref="MaxInlineBytes"/> is the one limit that is NOT derived from the agent: base64
/// inflates payloads by roughly a third, so anything approaching
/// <c>ConnectorLimitsConfig.MaxFrameBytes</c> would trip the fatal <c>frame_too_large</c> check.
/// Attachments larger than this must travel as a Bridge-issued handle instead.
/// </para>
/// </remarks>
public sealed class ConnectorMediaConfig
{
    /// <summary>
    /// Master switch for connector media. When false, <c>capabilities.media</c> is ignored and
    /// every connector behaves exactly as it did before media support existed. This is the ONLY
    /// supported way to disable media — zeroing the numeric limits does not disable it, because
    /// each is clamped to a workable minimum.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum attachments accepted on (or sent in) a single message.</summary>
    public int MaxAttachmentsPerMessage { get; set; } = 4;

    /// <summary>Maximum decoded size of a single attachment in bytes.</summary>
    public long MaxAttachmentBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Maximum decoded size an attachment may have and still travel inline as base64 inside a
    /// frame. Larger attachments must use the upload/fetch handle flow.
    /// </summary>
    public int MaxInlineBytes { get; set; } = 256 * 1024;

    /// <summary>How long an issued attachment handle remains resolvable before eviction.</summary>
    public TimeSpan HandleTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Ceiling on the total bytes of live (unexpired) handles held for any one connector.
    /// Bounds the memory a single connector can pin through the upload endpoint.
    /// </summary>
    public long MaxStoredBytesPerConnector { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Maximum attachment uploads accepted from one connector per rolling minute. The inbound
    /// frame rate limit does not cover the REST upload endpoint, so this is its own budget.
    /// </summary>
    /// <remarks>
    /// Zero or negative means UNLIMITED, matching the convention of
    /// <c>ConnectorLimitsConfig.MaxMessagesPerMinute</c>. Zero does NOT mean "block all uploads" —
    /// to turn media off entirely set <see cref="Enabled"/> to false.
    /// </remarks>
    public int MaxUploadsPerMinute { get; set; } = 30;

    /// <summary>
    /// MIME types a connector may send or receive. Content is sniffed against magic bytes and
    /// must match the declared type — the declared value alone is never trusted.
    /// </summary>
    /// <remarks>
    /// Defaults to EMPTY, which policy consumers must read as "use
    /// <see cref="DefaultAllowedMimeTypes"/>". This is not a style choice:
    /// <c>IConfiguration.Bind</c> APPENDS to a pre-populated collection rather than replacing it,
    /// so seeding the defaults here would make it impossible to NARROW the allow-list from YAML —
    /// a user asking for PNG-only would silently also get JPEG, GIF and WebP. Resolve through
    /// <c>ConnectorMediaPolicy</c> rather than reading this directly.
    /// </remarks>
    public IList<string> AllowedMimeTypes { get; set; } = [];

    /// <summary>
    /// The MIME types applied when <see cref="AllowedMimeTypes"/> is left empty. Mirrors the
    /// agent host's own image allow-list.
    /// </summary>
    public static IReadOnlyList<string> DefaultAllowedMimeTypes { get; } =
        ImmutableArray.Create("image/png", "image/jpeg", "image/gif", "image/webp");
}
