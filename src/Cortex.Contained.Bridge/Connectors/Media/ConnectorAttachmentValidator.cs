using System.Globalization;
using Cortex.Contained.Bridge.Connectors.Protocol;

namespace Cortex.Contained.Bridge.Connectors.Media;

/// <summary>
/// One attachment that has passed validation. Either <see cref="Data"/> holds the verified bytes
/// (inline carrying mode) or <see cref="Handle"/> names bytes the Bridge is holding, which the
/// caller must still resolve.
/// </summary>
public sealed record ValidatedAttachment
{
    /// <summary>The normalised, verified MIME type.</summary>
    public required string MimeType { get; init; }

    /// <summary>Sanitised display file name, or null when the sender supplied none.</summary>
    public string? FileName { get; init; }

    /// <summary>Alt text / caption, truncated to a sane length.</summary>
    public string? Caption { get; init; }

    /// <summary>Decoded and content-verified bytes; null when this attachment arrived as a handle.</summary>
    public byte[]? Data { get; init; }

    /// <summary>Bridge-issued handle; null when the bytes arrived inline.</summary>
    public string? Handle { get; init; }
}

/// <summary>Outcome of validating the attachment list on one connector message.</summary>
public sealed record ConnectorAttachmentValidationResult
{
    /// <summary>Whether every attachment passed.</summary>
    public required bool Success { get; init; }

    /// <summary>One of <see cref="ConnectorErrorCodes"/> when <see cref="Success"/> is false.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable, sender-safe failure description.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The validated attachments; empty when the message carried none or validation failed.</summary>
    public IReadOnlyList<ValidatedAttachment> Attachments { get; init; } = [];

    /// <summary>A successful validation.</summary>
    /// <param name="attachments">The validated attachments.</param>
    public static ConnectorAttachmentValidationResult Ok(IReadOnlyList<ValidatedAttachment> attachments) =>
        new() { Success = true, Attachments = attachments };

    /// <summary>A failed validation.</summary>
    /// <param name="errorCode">One of <see cref="ConnectorErrorCodes"/>.</param>
    /// <param name="errorMessage">A sender-safe description.</param>
    public static ConnectorAttachmentValidationResult Fail(string errorCode, string errorMessage) =>
        new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}

/// <summary>
/// Validates the attachments on an inbound connector frame against
/// <see cref="ConnectorMediaPolicy"/>, decoding inline base64 and verifying content against the
/// declared MIME type.
/// </summary>
/// <remarks>
/// Every check here treats connector input as hostile:
/// <list type="bullet">
/// <item>A <c>url</c> field is rejected outright — the Bridge never dereferences a
/// connector-supplied location.</item>
/// <item><c>sizeBytes</c> is a hint and is never used as the size; the decoded length is.</item>
/// <item>Oversize payloads are rejected from the ENCODED length before any decode allocates,
/// so a connector cannot force a large allocation just to have it rejected afterwards.</item>
/// <item>The declared MIME type is verified against magic bytes.</item>
/// <item>Inline attachments are additionally checked against a whole-message budget, because
/// individually-legal attachments can still overflow the frame in aggregate.</item>
/// </list>
/// Pure and stateless: no I/O, no clock, no handle resolution. Resolving a handle to bytes is the
/// caller's job, which keeps this class trivially testable.
/// </remarks>
public sealed class ConnectorAttachmentValidator
{
    /// <summary>Maximum length of an attachment file name; longer names are truncated.</summary>
    internal const int MaxFileNameLength = 256;

    /// <summary>Maximum length of an attachment caption; longer captions are truncated.</summary>
    internal const int MaxCaptionLength = 1024;

    /// <summary>Maximum length of a handle string accepted from a connector.</summary>
    internal const int MaxHandleLength = 128;

    /// <summary>
    /// Longest file name sanitised on the stack. Longer names fall back to the heap, because the
    /// input length is attacker-controlled and must never size a stack allocation.
    /// </summary>
    private const int MaxStackFileNameChars = 512;

    private readonly ConnectorMediaPolicy policy;

    /// <summary>Initialises a new <see cref="ConnectorAttachmentValidator"/>.</summary>
    /// <param name="policy">The effective media policy supplying every limit.</param>
    public ConnectorAttachmentValidator(ConnectorMediaPolicy policy)
    {
        this.policy = policy;
    }

    /// <summary>
    /// Validates <paramref name="attachments"/> from an inbound frame.
    /// </summary>
    /// <param name="attachments">The attachments as they arrived; may be null or empty.</param>
    /// <param name="supportsMedia">
    /// Whether the sending connector negotiated <c>capabilities.media</c>.
    /// </param>
    public ConnectorAttachmentValidationResult Validate(
        IReadOnlyList<ConnectorAttachmentPayload>? attachments,
        bool supportsMedia)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return ConnectorAttachmentValidationResult.Ok([]);
        }

        if (!this.policy.Enabled)
        {
            return ConnectorAttachmentValidationResult.Fail(
                ConnectorErrorCodes.MediaNotSupported,
                "Media attachments are disabled by policy.");
        }

        if (!supportsMedia)
        {
            return ConnectorAttachmentValidationResult.Fail(
                ConnectorErrorCodes.MediaNotSupported,
                "Attachments require capabilities.media to be declared in the hello frame.");
        }

        if (attachments.Count > this.policy.MaxAttachmentsPerMessage)
        {
            return ConnectorAttachmentValidationResult.Fail(
                ConnectorErrorCodes.TooManyAttachments,
                $"Message carries {attachments.Count} attachments; at most {this.policy.MaxAttachmentsPerMessage} are allowed.");
        }

        var validated = new List<ValidatedAttachment>(attachments.Count);
        var totalInlineBytes = 0L;

        for (var i = 0; i < attachments.Count; i++)
        {
            var result = this.ValidateOne(attachments[i], i, ref totalInlineBytes);
            if (!result.Success)
            {
                return result;
            }

            validated.Add(result.Attachments[0]);
        }

        return ConnectorAttachmentValidationResult.Ok(validated);
    }

    private ConnectorAttachmentValidationResult ValidateOne(
        ConnectorAttachmentPayload attachment,
        int index,
        ref long totalInlineBytes)
    {
        if (attachment is null)
        {
            return ConnectorAttachmentValidationResult.Fail(
                ConnectorErrorCodes.InvalidPayload,
                $"Attachment [{index}] is null.");
        }

        // SECURITY: the Bridge must never dereference a connector-supplied location. Rejecting
        // the field is what makes that guarantee visible instead of relying on the deserialiser
        // silently dropping an unknown property.
        if (attachment.Url is not null)
        {
            return ConnectorAttachmentValidationResult.Fail(
                ConnectorErrorCodes.InvalidPayload,
                $"Attachment [{index}] specifies 'url', which is never accepted. Use 'data' or 'handle'.");
        }

        var hasData = !string.IsNullOrEmpty(attachment.Data);
        var hasHandle = !string.IsNullOrEmpty(attachment.Handle);

        if (hasData == hasHandle)
        {
            return ConnectorAttachmentValidationResult.Fail(
                ConnectorErrorCodes.InvalidPayload,
                $"Attachment [{index}] must specify exactly one of 'data' or 'handle'.");
        }

        var mimeType = ConnectorMediaPolicy.NormalizeMimeType(attachment.MimeType);
        if (mimeType is null)
        {
            return ConnectorAttachmentValidationResult.Fail(
                ConnectorErrorCodes.InvalidPayload,
                $"Attachment [{index}] is missing 'mimeType'.");
        }

        if (!this.policy.IsMimeTypeAllowed(mimeType))
        {
            return ConnectorAttachmentValidationResult.Fail(
                ConnectorErrorCodes.AttachmentTypeNotAllowed,
                $"Attachment [{index}] declares MIME type '{mimeType}', which is not allowed.");
        }

        var fileName = ConnectorText.Truncate(SanitizeFileName(attachment.FileName), MaxFileNameLength);
        var caption = ConnectorText.Truncate(attachment.Caption, MaxCaptionLength);

        return hasHandle
            ? ValidateHandle(attachment.Handle!, mimeType, fileName, caption, index)
            : this.ValidateInline(attachment.Data!, mimeType, fileName, caption, index, ref totalInlineBytes);
    }

    private static ConnectorAttachmentValidationResult ValidateHandle(
        string handle,
        string mimeType,
        string? fileName,
        string? caption,
        int index)
    {
        // A handle is opaque and Bridge-issued. Length and charset are checked here purely to
        // keep obviously-forged values out of the store lookup and the logs; whether the handle
        // actually resolves is decided by the store, which alone knows channel scope and expiry.
        if (handle.Length > MaxHandleLength || !IsWellFormedHandle(handle))
        {
            return ConnectorAttachmentValidationResult.Fail(
                ConnectorErrorCodes.AttachmentNotFound,
                $"Attachment [{index}] references an unknown handle.");
        }

        return ConnectorAttachmentValidationResult.Ok(
        [
            new ValidatedAttachment
            {
                MimeType = mimeType,
                FileName = fileName,
                Caption = caption,
                Handle = handle,
            },
        ]);
    }

    private ConnectorAttachmentValidationResult ValidateInline(
        string base64,
        string mimeType,
        string? fileName,
        string? caption,
        int index,
        ref long totalInlineBytes)
    {
        // Reject from the ENCODED length first: this is the cheap check that runs before any
        // allocation, so an oversize payload cannot force a large decode just to be thrown away.
        // It must use the MINIMUM size the string could decode to — an upper bound here would
        // refuse a payload of exactly MaxInlineBytes, since base64 padding makes the naive
        // length*3/4 estimate overshoot by up to two bytes.
        var minimumBytes = MinDecodedLength(base64.Length);
        if (minimumBytes > this.policy.MaxInlineBytes)
        {
            return ConnectorAttachmentValidationResult.Fail(
                ConnectorErrorCodes.AttachmentTooLarge,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Attachment [{index}] is at least {minimumBytes:N0} bytes inline; the maximum is {this.policy.MaxInlineBytes:N0}. Upload it and send a handle instead."));
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return ConnectorAttachmentValidationResult.Fail(
                ConnectorErrorCodes.InvalidPayload,
                $"Attachment [{index}] 'data' is not valid base64.");
        }

        if (data.Length == 0)
        {
            return ConnectorAttachmentValidationResult.Fail(
                ConnectorErrorCodes.InvalidPayload,
                $"Attachment [{index}] is empty.");
        }

        // Re-check against the true decoded length; the bound above is a lower bound used only
        // to avoid the allocation, never as the authoritative size.
        if (data.Length > this.policy.MaxInlineBytes)
        {
            return ConnectorAttachmentValidationResult.Fail(
                ConnectorErrorCodes.AttachmentTooLarge,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Attachment [{index}] is {data.Length:N0} bytes inline; the maximum is {this.policy.MaxInlineBytes:N0}."));
        }

        // Individually-legal attachments can still overflow the frame in aggregate, so the
        // whole-message budget is enforced as well as the per-attachment cap.
        totalInlineBytes += data.Length;
        if (totalInlineBytes > this.policy.MaxTotalInlineBytes)
        {
            return ConnectorAttachmentValidationResult.Fail(
                ConnectorErrorCodes.AttachmentTooLarge,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Inline attachments total {totalInlineBytes:N0} bytes; at most {this.policy.MaxTotalInlineBytes:N0} may be carried inline on one message."));
        }

        // SECURITY: the declared type is a claim, the magic bytes are the evidence.
        if (!ImageContentSniffer.MatchesDeclaredType(data, mimeType))
        {
            return ConnectorAttachmentValidationResult.Fail(
                ConnectorErrorCodes.AttachmentTypeNotAllowed,
                $"Attachment [{index}] content does not match its declared MIME type '{mimeType}'.");
        }

        return ConnectorAttachmentValidationResult.Ok(
        [
            new ValidatedAttachment
            {
                MimeType = mimeType,
                FileName = fileName,
                Caption = caption,
                Data = data,
            },
        ]);
    }

    /// <summary>
    /// Lower bound on the bytes <paramref name="base64Length"/> characters can decode to, used to
    /// reject clearly-oversize payloads before allocating anything. A lower bound is required
    /// rather than an estimate: rejecting on an upper bound would refuse a payload of exactly the
    /// maximum size, because up to two padding characters make the naive length*3/4 overshoot.
    /// </summary>
    /// <param name="base64Length">Length of the base64 string.</param>
    internal static long MinDecodedLength(int base64Length) =>
        Math.Max(0, ((long)base64Length / 4 * 3) - 2);

    /// <summary>
    /// Returns true when <paramref name="handle"/> consists only of the characters the Bridge
    /// itself issues, so a forged value cannot inject separators into a lookup key or a log line.
    /// </summary>
    /// <param name="handle">The connector-supplied handle.</param>
    internal static bool IsWellFormedHandle(string handle)
    {
        if (handle.Length == 0)
        {
            return false;
        }

        foreach (var c in handle)
        {
            var ok = (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c is '_' or '-';

            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Strips path components, control characters and Unicode formatting characters from
    /// <paramref name="fileName"/>. Length capping is deliberately NOT done here — the caller
    /// applies <see cref="ConnectorText.Truncate"/>, which cuts without splitting a surrogate
    /// pair. The name is display metadata only and is never used to open a file, but it does
    /// reach logs and other channels.
    /// </summary>
    /// <param name="fileName">The sender-supplied file name; may be null.</param>
    internal static string? SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        // Untrusted length, so only short names go on the stack.
        char[]? rented = fileName.Length > MaxStackFileNameChars ? new char[fileName.Length] : null;
        Span<char> buffer = rented ?? stackalloc char[MaxStackFileNameChars];
        var length = 0;

        foreach (var c in fileName)
        {
            // Drop path separators, control characters, and Unicode format characters. The last
            // group matters because a right-to-left override can make "shot.png<RLO>gpj.exe"
            // RENDER as "shot.pngexe.jpg" in a log viewer.
            if (c is '/' or '\\' or ':'
                || char.IsControl(c)
                || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                continue;
            }

            buffer[length++] = c;
        }

        var sanitized = new string(buffer[..length]).Trim();

        // A name consisting only of dots would render as a relative path segment.
        return sanitized.Length == 0 || sanitized.All(c => c == '.') ? null : sanitized;
    }
}
