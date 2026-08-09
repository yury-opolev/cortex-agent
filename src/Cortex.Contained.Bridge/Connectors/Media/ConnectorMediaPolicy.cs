using System.Collections.Frozen;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Connectors.Media;

/// <summary>
/// The effective, validated media policy for the connector subsystem: raw
/// <see cref="ConnectorMediaConfig"/> values resolved once into clamped limits and an immutable
/// MIME allow-list. Everything media-related reads its limits from here so a hand-edited or
/// hostile config cannot push any single component into an unsafe range.
/// </summary>
/// <remarks>
/// Two derivations deserve calling out, because neither is expressible in flat config:
/// <list type="bullet">
/// <item>
/// <see cref="MaxInlineBytes"/> is hard-capped by a ceiling derived from
/// <c>ConnectorLimitsConfig.MaxFrameBytes</c>. Configuring it separately is convenient, but
/// letting it drift above what a frame can carry would make every inline attachment overflow the
/// frame — fatal inbound (<c>frame_too_large</c> closes the session), and a dropped message
/// outbound.
/// </item>
/// <item>
/// <see cref="MaxTotalInlineBytes"/> bounds the whole message rather than one attachment.
/// A per-attachment cap alone is not enough: the default 4 x 256 KB is 1 MB raw, which is
/// ~1.37 MB once base64-encoded and would blow a 1 MiB frame even though every individual
/// attachment was within its limit.
/// </item>
/// </list>
/// <para>
/// The guarantee both rest on: base64 of <see cref="MaxTotalInlineBytes"/> raw bytes is at most
/// <c>MaxFrameBytes</c> minus a fixed envelope reserve, so a fully-loaded message still has room
/// for its text, ids and JSON structure.
/// </para>
/// </remarks>
public sealed class ConnectorMediaPolicy
{
    /// <summary>
    /// Bytes reserved inside every frame for the JSON envelope, message text, ids and captions,
    /// so an attachment that fits the inline budget cannot on its own push the frame past
    /// <c>MaxFrameBytes</c> — which would be a FATAL <c>frame_too_large</c> close.
    /// </summary>
    private const int JsonEnvelopeReserveBytes = 8 * 1024;

    /// <summary>Base64 encodes 3 bytes as 4 characters, so encoded size is ~4/3 of raw size.</summary>
    private const double Base64Inflation = 4.0 / 3.0;

    /// <summary>
    /// Floor applied to the caller-supplied frame size. Guards the class invariant ("an inline
    /// attachment fits inside the frame") against a nonsensical or hostile configured value;
    /// the real default is 1 MiB.
    /// </summary>
    public const int MinFrameBytes = 16 * 1024;

    private const int MinAttachmentsPerMessage = 1;
    private const int MaxAttachmentsPerMessageCeiling = 16;
    private const long MinAttachmentBytes = 1;
    private const long MaxAttachmentBytesCeiling = 64L * 1024 * 1024;
    private const long MaxStoredBytesCeiling = 1024L * 1024 * 1024;
    private const int MaxUploadsPerMinuteCeiling = 10_000;

    private static readonly TimeSpan MinHandleTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxHandleTtl = TimeSpan.FromHours(1);

    private readonly FrozenSet<string> allowedMimeTypes;

    private ConnectorMediaPolicy(
        bool enabled,
        int maxAttachmentsPerMessage,
        long maxAttachmentBytes,
        int maxInlineBytes,
        int maxTotalInlineBytes,
        TimeSpan handleTtl,
        long maxStoredBytesPerConnector,
        int maxUploadsPerMinute,
        FrozenSet<string> allowedMimeTypes)
    {
        this.Enabled = enabled;
        this.MaxAttachmentsPerMessage = maxAttachmentsPerMessage;
        this.MaxAttachmentBytes = maxAttachmentBytes;
        this.MaxInlineBytes = maxInlineBytes;
        this.MaxTotalInlineBytes = maxTotalInlineBytes;
        this.HandleTtl = handleTtl;
        this.MaxStoredBytesPerConnector = maxStoredBytesPerConnector;
        this.MaxUploadsPerMinute = maxUploadsPerMinute;
        this.allowedMimeTypes = allowedMimeTypes;
    }

    /// <summary>Whether connector media is enabled at all.</summary>
    public bool Enabled { get; }

    /// <summary>Maximum attachments carried by one message.</summary>
    public int MaxAttachmentsPerMessage { get; }

    /// <summary>Maximum decoded size of a single attachment, by any carrying mode.</summary>
    public long MaxAttachmentBytes { get; }

    /// <summary>Maximum decoded size of a single attachment carried inline as base64.</summary>
    public int MaxInlineBytes { get; }

    /// <summary>Maximum combined decoded size of all inline attachments on one message.</summary>
    public int MaxTotalInlineBytes { get; }

    /// <summary>Lifetime of an issued attachment handle.</summary>
    public TimeSpan HandleTtl { get; }

    /// <summary>Ceiling on live handle bytes held for one connector.</summary>
    public long MaxStoredBytesPerConnector { get; }

    /// <summary>Uploads accepted from one connector per rolling minute; zero means unlimited.</summary>
    public int MaxUploadsPerMinute { get; }

    /// <summary>The resolved MIME allow-list.</summary>
    public IReadOnlyCollection<string> AllowedMimeTypes => this.allowedMimeTypes;

    /// <summary>
    /// Resolves <paramref name="config"/> into an effective policy.
    /// </summary>
    /// <param name="config">Raw media configuration; null yields defaults.</param>
    /// <param name="maxFrameBytes">
    /// The connector frame cap that inline attachments must fit inside.
    /// </param>
    public static ConnectorMediaPolicy From(ConnectorMediaConfig? config, int maxFrameBytes)
    {
        config ??= new ConnectorMediaConfig();

        var maxAttachments = Math.Clamp(
            config.MaxAttachmentsPerMessage,
            MinAttachmentsPerMessage,
            MaxAttachmentsPerMessageCeiling);

        var maxAttachmentBytes = Math.Clamp(
            config.MaxAttachmentBytes,
            MinAttachmentBytes,
            MaxAttachmentBytesCeiling);

        // The most raw bytes that can be base64-encoded and still leave the envelope its reserve.
        // Subtracting the reserve BEFORE dividing is what makes the guarantee exact rather than
        // proportional: whatever comes out, encoded, is at most maxFrameBytes - reserve.
        var effectiveFrameBytes = Math.Max(maxFrameBytes, MinFrameBytes);
        var encodedBudget = effectiveFrameBytes - JsonEnvelopeReserveBytes;
        var frameCeiling = (int)Math.Max(1, encodedBudget / Base64Inflation);

        var maxInlineBytes = Math.Clamp(config.MaxInlineBytes, 1, frameCeiling);

        // An inline attachment can never exceed the per-attachment cap either.
        maxInlineBytes = (int)Math.Min(maxInlineBytes, maxAttachmentBytes);

        // The aggregate budget is the frame ceiling, but it is pointless for it to exceed what
        // MaxAttachmentsPerMessage attachments could actually occupy.
        var maxTotalInlineBytes = (int)Math.Min(frameCeiling, (long)maxInlineBytes * maxAttachments);

        var handleTtl = config.HandleTtl < MinHandleTtl
            ? MinHandleTtl
            : config.HandleTtl > MaxHandleTtl
                ? MaxHandleTtl
                : config.HandleTtl;

        // The store must be able to hold at least one maximum-size attachment, or the upload
        // endpoint would reject every request the rest of the policy permits.
        var maxStoredBytes = Math.Clamp(
            config.MaxStoredBytesPerConnector,
            maxAttachmentBytes,
            Math.Max(maxAttachmentBytes, MaxStoredBytesCeiling));

        var maxUploadsPerMinute = Math.Clamp(config.MaxUploadsPerMinute, 0, MaxUploadsPerMinuteCeiling);

        // Empty means "defaults" — see the remarks on ConnectorMediaConfig.AllowedMimeTypes for
        // why the config object cannot simply seed them itself.
        IEnumerable<string> configured = config.AllowedMimeTypes is { Count: > 0 }
            ? config.AllowedMimeTypes
            : ConnectorMediaConfig.DefaultAllowedMimeTypes;

        var allowed = configured
            .Select(NormalizeMimeType)
            .Where(m => m is not null)
            .Select(m => m!)
            .ToFrozenSet(StringComparer.Ordinal);

        // A list whose every entry was blank survives the Count > 0 check above but normalises
        // to nothing, which would silently reject ALL media while still reporting Enabled=true.
        // Fall back to the defaults rather than becoming an invisible kill-switch; the explicit
        // way to turn media off is Enabled=false.
        if (allowed.Count == 0)
        {
            allowed = ConnectorMediaConfig.DefaultAllowedMimeTypes.ToFrozenSet(StringComparer.Ordinal);
        }

        return new ConnectorMediaPolicy(
            config.Enabled,
            maxAttachments,
            maxAttachmentBytes,
            maxInlineBytes,
            maxTotalInlineBytes,
            handleTtl,
            maxStoredBytes,
            maxUploadsPerMinute,
            allowed);
    }

    /// <summary>
    /// Returns true when <paramref name="mimeType"/> is permitted. Comparison is
    /// case-insensitive and ignores any parameters after a <c>;</c>.
    /// </summary>
    /// <param name="mimeType">The declared MIME type; may be null.</param>
    public bool IsMimeTypeAllowed(string? mimeType)
    {
        var normalized = NormalizeMimeType(mimeType);
        return normalized is not null && this.allowedMimeTypes.Contains(normalized);
    }

    /// <summary>
    /// Lower-cases <paramref name="mimeType"/> and strips any parameters (everything from the
    /// first <c>;</c> onward), returning null when the input is null, empty, or whitespace.
    /// </summary>
    /// <param name="mimeType">The declared MIME type; may be null.</param>
    public static string? NormalizeMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return null;
        }

        var semicolon = mimeType.IndexOf(';', StringComparison.Ordinal);
        var essence = semicolon >= 0 ? mimeType[..semicolon] : mimeType;
        essence = essence.Trim();

        return essence.Length == 0 ? null : essence.ToLowerInvariant();
    }
}
