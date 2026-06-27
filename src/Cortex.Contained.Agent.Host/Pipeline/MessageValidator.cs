using Cortex.Contained.Contracts.Messages;

namespace Cortex.Contained.Agent.Host.Pipeline;

/// <summary>
/// Validates inbound messages before processing. Enforces size limits,
/// content type whitelists, and basic structural checks.
/// </summary>
public static class MessageValidator
{
    /// <summary>Maximum text message length in characters.</summary>
    private const int MaxTextLength = 100_000;

    /// <summary>Maximum attachment size in bytes (50 MB).</summary>
    private const long MaxAttachmentSizeBytes = 50 * 1024 * 1024;

    /// <summary>Maximum number of attachments per message.</summary>
    private const int MaxAttachmentCount = 10;

    /// <summary>Allowed MIME types for attachments.</summary>
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/gif",
        "image/webp",
        "audio/ogg",
        "audio/mpeg",
        "audio/wav",
        "video/mp4",
        "application/pdf",
        "text/plain",
    };

    /// <summary>
    /// Validates an inbound message. Returns a result indicating success or failure
    /// with a list of validation errors.
    /// </summary>
    public static MessageValidationResult Validate(InboundMessage message)
    {
        var errors = ValidateInternal(message);
        return new MessageValidationResult(errors.Count == 0, errors);
    }

    /// <summary>
    /// Validates a text string directly (used for hub messages that have
    /// already been extracted from the inbound envelope).
    /// </summary>
    public static MessageValidationResult ValidateText(string? text)
    {
        List<string> errors = [];

        if (text is not null && text.Length > MaxTextLength)
        {
            errors.Add($"Message text exceeds maximum length ({MaxTextLength:N0} characters).");
        }

        return new MessageValidationResult(errors.Count == 0, errors);
    }

    private static List<string> ValidateInternal(InboundMessage message)
    {
        List<string> errors = [];

        // Text length check
        if (message.Content.Text is not null && message.Content.Text.Length > MaxTextLength)
        {
            errors.Add($"Message text exceeds maximum length ({MaxTextLength:N0} characters).");
        }

        // Must have some content
        if (string.IsNullOrWhiteSpace(message.Content.Text)
            && (message.Content.Attachments is null || message.Content.Attachments.Count == 0))
        {
            errors.Add("Message must contain text or at least one attachment.");
        }

        // Attachment validation
        if (message.Content.Attachments is { Count: > 0 } attachments)
        {
            if (attachments.Count > MaxAttachmentCount)
            {
                errors.Add($"Too many attachments (max {MaxAttachmentCount}).");
            }

            for (int i = 0; i < attachments.Count; i++)
            {
                ValidateAttachment(attachments[i], i, errors);
            }
        }

        return errors;
    }

    private static void ValidateAttachment(MediaAttachment attachment, int index, List<string> errors)
    {
        // Size check
        if (attachment.SizeBytes.HasValue && attachment.SizeBytes.Value > MaxAttachmentSizeBytes)
        {
            errors.Add($"Attachment [{index}] \"{attachment.FileName ?? "unnamed"}\" exceeds {MaxAttachmentSizeBytes / (1024 * 1024)} MB limit.");
        }

        // Inline data size check
        if (attachment.Data is not null && attachment.Data.Length > MaxAttachmentSizeBytes)
        {
            errors.Add($"Attachment [{index}] \"{attachment.FileName ?? "unnamed"}\" data exceeds {MaxAttachmentSizeBytes / (1024 * 1024)} MB limit.");
        }

        // MIME type whitelist
        if (!AllowedMimeTypes.Contains(attachment.MimeType))
        {
            errors.Add($"Attachment [{index}] has unsupported media type: {attachment.MimeType}.");
        }
    }
}

/// <summary>Result of message validation.</summary>
public sealed record MessageValidationResult(bool IsValid, IReadOnlyList<string> Errors);
