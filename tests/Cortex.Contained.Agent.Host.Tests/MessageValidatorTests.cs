using Cortex.Contained.Agent.Host.Pipeline;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Messages;

namespace Cortex.Contained.Agent.Host.Tests;

public class MessageValidatorTests
{
    // ── Validate (full InboundMessage) ─────────────────────────────

    [Fact]
    public void Validate_ValidTextMessage_IsValid()
    {
        var message = CreateMessage("Hello world");

        var result = MessageValidator.Validate(message);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_TextExceedsMaxLength_ReturnsError()
    {
        var longText = new string('x', 100_001);
        var message = CreateMessage(longText);

        var result = MessageValidator.Validate(message);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("maximum length", result.Errors[0]);
    }

    [Fact]
    public void Validate_TextAtExactMaxLength_IsValid()
    {
        var exactText = new string('x', 100_000);
        var message = CreateMessage(exactText);

        var result = MessageValidator.Validate(message);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_EmptyTextNoAttachments_ReturnsError()
    {
        var message = CreateMessage(null);

        var result = MessageValidator.Validate(message);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("text or at least one attachment"));
    }

    [Fact]
    public void Validate_WhitespaceOnlyTextNoAttachments_ReturnsError()
    {
        var message = CreateMessage("   ");

        var result = MessageValidator.Validate(message);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("text or at least one attachment"));
    }

    [Fact]
    public void Validate_NoTextWithValidAttachment_IsValid()
    {
        var message = CreateMessageWithAttachments(null, [
            new MediaAttachment
            {
                MimeType = "image/jpeg",
                FileName = "photo.jpg",
                SizeBytes = 1024,
            }
        ]);

        var result = MessageValidator.Validate(message);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_TooManyAttachments_ReturnsError()
    {
        var attachments = Enumerable.Range(0, 11)
            .Select(i => new MediaAttachment
            {
                MimeType = "image/jpeg",
                FileName = $"photo{i}.jpg",
                SizeBytes = 1024,
            })
            .ToList();

        var message = CreateMessageWithAttachments("text", attachments);

        var result = MessageValidator.Validate(message);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Too many attachments"));
    }

    [Fact]
    public void Validate_ExactlyMaxAttachments_IsValid()
    {
        var attachments = Enumerable.Range(0, 10)
            .Select(i => new MediaAttachment
            {
                MimeType = "image/jpeg",
                FileName = $"photo{i}.jpg",
                SizeBytes = 1024,
            })
            .ToList();

        var message = CreateMessageWithAttachments("text", attachments);

        var result = MessageValidator.Validate(message);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_AttachmentExceedsMaxSize_ReturnsError()
    {
        var message = CreateMessageWithAttachments("text", [
            new MediaAttachment
            {
                MimeType = "image/jpeg",
                FileName = "huge.jpg",
                SizeBytes = 51 * 1024 * 1024, // 51 MB
            }
        ]);

        var result = MessageValidator.Validate(message);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("exceeds") && e.Contains("MB limit"));
    }

    [Fact]
    public void Validate_AttachmentDataExceedsMaxSize_ReturnsError()
    {
        var message = CreateMessageWithAttachments("text", [
            new MediaAttachment
            {
                MimeType = "image/jpeg",
                FileName = "huge.jpg",
                Data = new byte[51 * 1024 * 1024], // 51 MB
            }
        ]);

        var result = MessageValidator.Validate(message);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("data exceeds"));
    }

    [Fact]
    public void Validate_UnsupportedMimeType_ReturnsError()
    {
        var message = CreateMessageWithAttachments("text", [
            new MediaAttachment
            {
                MimeType = "application/x-executable",
                FileName = "malware.exe",
                SizeBytes = 1024,
            }
        ]);

        var result = MessageValidator.Validate(message);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("unsupported media type"));
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    [InlineData("audio/ogg")]
    [InlineData("audio/mpeg")]
    [InlineData("audio/wav")]
    [InlineData("video/mp4")]
    [InlineData("application/pdf")]
    [InlineData("text/plain")]
    public void Validate_AllowedMimeTypes_AreValid(string mimeType)
    {
        var message = CreateMessageWithAttachments("text", [
            new MediaAttachment
            {
                MimeType = mimeType,
                FileName = "file",
                SizeBytes = 1024,
            }
        ]);

        var result = MessageValidator.Validate(message);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_MimeTypeCaseInsensitive()
    {
        var message = CreateMessageWithAttachments("text", [
            new MediaAttachment
            {
                MimeType = "IMAGE/JPEG",
                FileName = "photo.jpg",
                SizeBytes = 1024,
            }
        ]);

        var result = MessageValidator.Validate(message);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_MultipleErrors_ReturnsAllErrors()
    {
        var longText = new string('x', 100_001);
        var message = CreateMessageWithAttachments(longText, [
            new MediaAttachment
            {
                MimeType = "application/x-executable",
                FileName = "bad.exe",
                SizeBytes = 51 * 1024 * 1024,
            }
        ]);

        var result = MessageValidator.Validate(message);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 2); // text length + attachment issues
    }

    [Fact]
    public void Validate_AttachmentWithNoSizeBytes_NoSizeError()
    {
        var message = CreateMessageWithAttachments("text", [
            new MediaAttachment
            {
                MimeType = "image/jpeg",
                FileName = "photo.jpg",
                // SizeBytes is null
            }
        ]);

        var result = MessageValidator.Validate(message);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_UnnamedAttachment_ErrorIncludesUnnamed()
    {
        var message = CreateMessageWithAttachments("text", [
            new MediaAttachment
            {
                MimeType = "application/x-executable",
                SizeBytes = 1024,
                // FileName is null
            }
        ]);

        var result = MessageValidator.Validate(message);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("unsupported media type"));
    }

    // ── ValidateText ───────────────────────────────────────────────

    [Fact]
    public void ValidateText_NullText_IsValid()
    {
        var result = MessageValidator.ValidateText(null);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateText_NormalText_IsValid()
    {
        var result = MessageValidator.ValidateText("Hello world");
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateText_ExceedsMaxLength_ReturnsError()
    {
        var longText = new string('x', 100_001);
        var result = MessageValidator.ValidateText(longText);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("maximum length", result.Errors[0]);
    }

    [Fact]
    public void ValidateText_ExactlyMaxLength_IsValid()
    {
        var exactText = new string('x', 100_000);
        var result = MessageValidator.ValidateText(exactText);

        Assert.True(result.IsValid);
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static InboundMessage CreateMessage(string? text)
    {
        return new InboundMessage
        {
            MessageId = "msg-1",
            ConversationId = "conv-1",
            ChannelId = "webchat",
            ChannelType = ChannelType.WebChat,
            Sender = new SenderInfo { Id = "user-1" },
            Content = new MessageContent { Text = text },
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private static InboundMessage CreateMessageWithAttachments(
        string? text, List<MediaAttachment> attachments)
    {
        return new InboundMessage
        {
            MessageId = "msg-1",
            ConversationId = "conv-1",
            ChannelId = "webchat",
            ChannelType = ChannelType.WebChat,
            Sender = new SenderInfo { Id = "user-1" },
            Content = new MessageContent { Text = text, Attachments = attachments },
            Timestamp = DateTimeOffset.UtcNow,
        };
    }
}
