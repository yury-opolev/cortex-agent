using Cortex.Contained.Bridge.Connectors.Security;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Connectors.Media;

/// <summary>Why an attachment upload or fetch was refused.</summary>
public enum ConnectorAttachmentAccessError
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>The bearer token is absent, malformed, unknown, or belongs to a disabled connector.</summary>
    Unauthorized,

    /// <summary>Connector media is switched off by policy.</summary>
    MediaDisabled,

    /// <summary>The channel has exceeded its upload rate budget.</summary>
    RateLimited,

    /// <summary>The payload is empty, too large, or not an allowed image.</summary>
    ContentRejected,

    /// <summary>The channel's storage quota is exhausted.</summary>
    QuotaExceeded,

    /// <summary>The handle is unknown, expired, consumed, or belongs to another channel.</summary>
    NotFound,
}

/// <summary>Outcome of an upload.</summary>
public sealed record ConnectorAttachmentUploadResult
{
    /// <summary>The issued handle when successful.</summary>
    public string? Handle { get; init; }

    /// <summary>When the handle stops resolving.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The failure reason, or <see cref="ConnectorAttachmentAccessError.None"/>.</summary>
    public ConnectorAttachmentAccessError Error { get; init; }

    /// <summary>A sender-safe description of the failure.</summary>
    public string? Message { get; init; }

    /// <summary>Whether the upload succeeded.</summary>
    public bool Success => this.Error == ConnectorAttachmentAccessError.None;

    /// <summary>A successful upload.</summary>
    /// <param name="handle">The issued handle.</param>
    /// <param name="expiresAt">When the handle stops resolving.</param>
    public static ConnectorAttachmentUploadResult Ok(string handle, DateTimeOffset expiresAt) =>
        new() { Handle = handle, ExpiresAt = expiresAt };

    /// <summary>A refused upload.</summary>
    /// <param name="error">The failure reason.</param>
    /// <param name="message">A sender-safe description.</param>
    public static ConnectorAttachmentUploadResult Fail(ConnectorAttachmentAccessError error, string message) =>
        new() { Error = error, Message = message };
}

/// <summary>Outcome of a fetch.</summary>
public sealed record ConnectorAttachmentFetchResult
{
    /// <summary>The content when found.</summary>
    public ConnectorAttachmentContent? Content { get; init; }

    /// <summary>The failure reason, or <see cref="ConnectorAttachmentAccessError.None"/>.</summary>
    public ConnectorAttachmentAccessError Error { get; init; }

    /// <summary>Whether the fetch succeeded.</summary>
    public bool Success => this.Error == ConnectorAttachmentAccessError.None;

    /// <summary>A successful fetch.</summary>
    /// <param name="content">The resolved content.</param>
    public static ConnectorAttachmentFetchResult Ok(ConnectorAttachmentContent content) =>
        new() { Content = content };

    /// <summary>A refused fetch.</summary>
    /// <param name="error">The failure reason.</param>
    public static ConnectorAttachmentFetchResult Fail(ConnectorAttachmentAccessError error) =>
        new() { Error = error };
}

/// <summary>
/// The authorisation and policy core behind the connector attachment REST endpoints, separated
/// from the HTTP plumbing so the security decisions are directly testable without a web host.
/// </summary>
/// <remarks>
/// The Bridge's management endpoints authenticate with the Web UI session; a connector has no
/// session, only the pairing token it was issued. This service is what turns that bearer token
/// into an authorised channel id, and every access is scoped to that channel — a connector can
/// never reach another's attachments.
/// </remarks>
public sealed partial class ConnectorAttachmentService
{
    /// <summary>The scheme expected on the <c>Authorization</c> header.</summary>
    public const string BearerScheme = "Bearer";

    private readonly ConnectorTokenStore tokenStore;
    private readonly ConnectorAttachmentStore attachmentStore;
    private readonly ConnectorUploadRateLimiter rateLimiter;
    private readonly ConnectorMediaPolicy policy;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ConnectorAttachmentService> logger;

    /// <summary>Initialises a new <see cref="ConnectorAttachmentService"/>.</summary>
    /// <param name="tokenStore">Registry used to resolve a bearer token to a paired connector.</param>
    /// <param name="attachmentStore">Store that issues and resolves handles.</param>
    /// <param name="rateLimiter">Per-channel upload rate limiter.</param>
    /// <param name="policy">Effective media policy.</param>
    /// <param name="timeProvider">Time source for expiry reporting.</param>
    /// <param name="logger">Logger.</param>
    public ConnectorAttachmentService(
        ConnectorTokenStore tokenStore,
        ConnectorAttachmentStore attachmentStore,
        ConnectorUploadRateLimiter rateLimiter,
        ConnectorMediaPolicy policy,
        TimeProvider timeProvider,
        ILogger<ConnectorAttachmentService> logger)
    {
        this.tokenStore = tokenStore;
        this.attachmentStore = attachmentStore;
        this.rateLimiter = rateLimiter;
        this.policy = policy;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>The largest request body worth reading, in bytes.</summary>
    /// <remarks>
    /// Exposed so the HTTP layer can refuse an over-long body before buffering it rather than
    /// reading megabytes only to reject them.
    /// </remarks>
    public long MaxUploadBytes => this.policy.MaxAttachmentBytes;

    /// <summary>
    /// Resolves an <c>Authorization</c> header value to the channel id it authorises, or null.
    /// </summary>
    /// <param name="authorizationHeader">The raw header value.</param>
    public string? ResolveChannelId(string? authorizationHeader)
    {
        var token = ExtractBearerToken(authorizationHeader);
        return token is null ? null : this.tokenStore.FindByToken(token)?.ChannelId;
    }

    /// <summary>
    /// Stores <paramref name="data"/> for the connector authorised by
    /// <paramref name="authorizationHeader"/> and returns a handle.
    /// </summary>
    /// <param name="authorizationHeader">The raw <c>Authorization</c> header value.</param>
    /// <param name="data">The uploaded bytes.</param>
    /// <param name="declaredMimeType">The declared content type; verified against magic bytes.</param>
    /// <param name="fileName">Optional display file name.</param>
    /// <param name="caption">Optional alt text.</param>
    public ConnectorAttachmentUploadResult Upload(
        string? authorizationHeader,
        byte[]? data,
        string? declaredMimeType,
        string? fileName = null,
        string? caption = null)
    {
        if (!this.policy.Enabled)
        {
            return ConnectorAttachmentUploadResult.Fail(
                ConnectorAttachmentAccessError.MediaDisabled,
                "Connector media is disabled.");
        }

        var channelId = this.ResolveChannelId(authorizationHeader);
        if (channelId is null)
        {
            return ConnectorAttachmentUploadResult.Fail(
                ConnectorAttachmentAccessError.Unauthorized,
                "A valid connector token is required.");
        }

        // Rate limit BEFORE any validation work, so a connector cannot spend the Bridge's CPU on
        // content checks for uploads it is not entitled to make.
        if (!this.rateLimiter.TryAcquire(channelId))
        {
            this.LogUploadRateLimited(channelId);
            return ConnectorAttachmentUploadResult.Fail(
                ConnectorAttachmentAccessError.RateLimited,
                "Upload rate limit exceeded.");
        }

        if (data is not { Length: > 0 })
        {
            return ConnectorAttachmentUploadResult.Fail(
                ConnectorAttachmentAccessError.ContentRejected,
                "Attachment is empty.");
        }

        if (data.LongLength > this.policy.MaxAttachmentBytes)
        {
            return ConnectorAttachmentUploadResult.Fail(
                ConnectorAttachmentAccessError.ContentRejected,
                "Attachment exceeds the maximum size.");
        }

        // Prefer what the bytes actually are over what the request claimed, and store the sniffed
        // type. The declaration is only a consistency check: a multipart part routinely arrives as
        // application/octet-stream, which means "unknown", not "not an image". A declaration that
        // names a DIFFERENT image type is a genuine signal of confusion or attack and is refused.
        var sniffed = ImageContentSniffer.DetectMimeType(data);
        if (sniffed is null || !this.policy.IsMimeTypeAllowed(sniffed))
        {
            return ConnectorAttachmentUploadResult.Fail(
                ConnectorAttachmentAccessError.ContentRejected,
                "Attachment is not an allowed image type.");
        }

        var declared = ConnectorMediaPolicy.NormalizeMimeType(declaredMimeType);
        if (declared is not null
            && this.policy.IsMimeTypeAllowed(declared)
            && !string.Equals(declared, sniffed, StringComparison.Ordinal))
        {
            return ConnectorAttachmentUploadResult.Fail(
                ConnectorAttachmentAccessError.ContentRejected,
                "Attachment content does not match the declared type.");
        }

        var handle = this.attachmentStore.Issue(channelId, new ConnectorAttachmentContent
        {
            MimeType = sniffed,
            Data = data,
            FileName = ConnectorText.Truncate(
                ConnectorAttachmentValidator.SanitizeFileName(fileName),
                ConnectorAttachmentValidator.MaxFileNameLength),
            Caption = ConnectorText.Truncate(caption, ConnectorAttachmentValidator.MaxCaptionLength),
        });

        if (handle is null)
        {
            return ConnectorAttachmentUploadResult.Fail(
                ConnectorAttachmentAccessError.QuotaExceeded,
                "Attachment storage quota exceeded; retry once earlier attachments expire or are consumed.");
        }

        this.LogUploadAccepted(channelId, data.LongLength);

        return ConnectorAttachmentUploadResult.Ok(handle, this.timeProvider.GetUtcNow() + this.policy.HandleTtl);
    }

    /// <summary>
    /// Resolves and consumes <paramref name="handle"/> for the connector authorised by
    /// <paramref name="authorizationHeader"/>.
    /// </summary>
    /// <param name="authorizationHeader">The raw <c>Authorization</c> header value.</param>
    /// <param name="handle">The handle to fetch.</param>
    public ConnectorAttachmentFetchResult Fetch(string? authorizationHeader, string? handle)
    {
        if (!this.policy.Enabled)
        {
            return ConnectorAttachmentFetchResult.Fail(ConnectorAttachmentAccessError.MediaDisabled);
        }

        var channelId = this.ResolveChannelId(authorizationHeader);
        if (channelId is null)
        {
            return ConnectorAttachmentFetchResult.Fail(ConnectorAttachmentAccessError.Unauthorized);
        }

        // SECURITY: a malformed handle is reported as not-found, not as a bad request. Any
        // distinction here would tell a caller something about handles it does not own.
        if (string.IsNullOrEmpty(handle)
            || handle.Length > ConnectorAttachmentValidator.MaxHandleLength
            || !ConnectorAttachmentValidator.IsWellFormedHandle(handle))
        {
            return ConnectorAttachmentFetchResult.Fail(ConnectorAttachmentAccessError.NotFound);
        }

        var content = this.attachmentStore.Consume(handle, channelId);

        return content is null
            ? ConnectorAttachmentFetchResult.Fail(ConnectorAttachmentAccessError.NotFound)
            : ConnectorAttachmentFetchResult.Ok(content);
    }

    /// <summary>
    /// Extracts the token from a <c>Bearer &lt;token&gt;</c> header value, or null when the header
    /// is absent or does not use the bearer scheme.
    /// </summary>
    /// <param name="authorizationHeader">The raw header value.</param>
    internal static string? ExtractBearerToken(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        var value = authorizationHeader.AsSpan().Trim();
        if (!value.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = value[BearerScheme.Length..];
        if (rest.Length == 0 || !char.IsWhiteSpace(rest[0]))
        {
            return null;
        }

        rest = rest.Trim();
        return rest.Length == 0 ? null : rest.ToString();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Connector {ChannelId} uploaded a {SizeBytes}-byte attachment.")]
    private partial void LogUploadAccepted(string channelId, long sizeBytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {ChannelId} exceeded its attachment upload rate limit.")]
    private partial void LogUploadRateLimited(string channelId);
}
