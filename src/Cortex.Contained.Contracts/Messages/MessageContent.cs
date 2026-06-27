namespace Cortex.Contained.Contracts.Messages;

/// <summary>
/// Content of a message. Can contain text, media, or both.
/// </summary>
public sealed record MessageContent
{
    /// <summary>Text content (may contain Markdown).</summary>
    public string? Text { get; init; }

    /// <summary>Media attachments.</summary>
    public IReadOnlyList<MediaAttachment>? Attachments { get; init; }

    /// <summary>Whether the text contains Markdown formatting.</summary>
    public bool IsMarkdown { get; init; }
}

/// <summary>A media file attached to a message.</summary>
public sealed record MediaAttachment
{
    /// <summary>MIME type (e.g., "image/jpeg", "audio/ogg").</summary>
    public required string MimeType { get; init; }

    /// <summary>File name.</summary>
    public string? FileName { get; init; }

    /// <summary>File size in bytes.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>
    /// URL to the media (may be a local file:// URI or remote URL).
    /// Mutually exclusive with Data.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Raw media bytes. Mutually exclusive with Url.
    /// For small media (&lt; 10MB). Larger files should use Url.
    /// </summary>
    public byte[]? Data { get; init; }

    /// <summary>Alt text / caption for the media.</summary>
    public string? Caption { get; init; }
}

/// <summary>Result of sending a message through a channel.</summary>
public sealed record SendResult
{
    /// <summary>Whether the message was sent successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Optional external message id assigned by the channel.</summary>
    public string? ExternalMessageId { get; init; }

    /// <summary>Optional error message if <see cref="Success"/> is false.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>A successful send, optionally carrying the channel's external message id.</summary>
    public static SendResult Ok(string? externalMessageId = null) => new() { Success = true, ExternalMessageId = externalMessageId };

    /// <summary>A failed send carrying an <paramref name="errorMessage"/>.</summary>
    public static SendResult Error(string errorMessage) => new() { Success = false, ErrorMessage = errorMessage };
}
