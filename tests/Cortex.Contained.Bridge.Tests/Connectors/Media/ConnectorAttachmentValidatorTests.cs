using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Bridge.Connectors.Media;
using Cortex.Contained.Bridge.Connectors.Protocol;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Connectors.Media;

/// <summary>
/// Tests <see cref="ConnectorAttachmentValidator"/>. A connector is an untrusted local process,
/// so these tests are written from the attacker's side as much as the happy path: mislabelled
/// content, forged sizes, URL smuggling, and aggregate overflow all have to be refused with a
/// NON-FATAL error so one bad attachment never kills a working session.
/// </summary>
public sealed class ConnectorAttachmentValidatorTests
{
    private const int OneMebibyte = 1_048_576;

    private static ConnectorAttachmentValidator Build(Action<ConnectorMediaConfig>? configure = null)
    {
        var config = new ConnectorMediaConfig();
        configure?.Invoke(config);
        return new ConnectorAttachmentValidator(ConnectorMediaPolicy.From(config, OneMebibyte));
    }

    private static ConnectorAttachmentPayload InlinePng(
        string? mimeType = "image/png",
        string? fileName = "shot.png",
        string? caption = null) =>
        new()
        {
            MimeType = mimeType,
            FileName = fileName,
            Caption = caption,
            Data = Convert.ToBase64String(ImageContentSnifferTests.Png),
        };

    private static byte[] PngOfSize(int totalBytes)
    {
        var buffer = new byte[totalBytes];
        ImageContentSnifferTests.Png.AsSpan(0, 8).CopyTo(buffer);
        return buffer;
    }

    // ── Nothing to do ────────────────────────────────────────────────

    [Fact]
    public void Validate_NullAttachments_Succeeds()
    {
        var result = Build().Validate(null, supportsMedia: true);

        Assert.True(result.Success);
        Assert.Empty(result.Attachments);
    }

    [Fact]
    public void Validate_EmptyAttachments_Succeeds()
    {
        var result = Build().Validate([], supportsMedia: true);

        Assert.True(result.Success);
        Assert.Empty(result.Attachments);
    }

    [Fact]
    public void Validate_NoAttachments_SucceedsEvenWithoutMediaCapability()
    {
        // A connector that never sends attachments must be completely unaffected by media policy.
        var result = Build().Validate([], supportsMedia: false);

        Assert.True(result.Success);
    }

    // ── Capability and policy gates ──────────────────────────────────

    [Fact]
    public void Validate_AttachmentsWithoutMediaCapability_FailsWithMediaNotSupported()
    {
        var result = Build().Validate([InlinePng()], supportsMedia: false);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.MediaNotSupported, result.ErrorCode);
    }

    [Fact]
    public void Validate_MediaDisabledByPolicy_FailsWithMediaNotSupported()
    {
        var validator = Build(c => c.Enabled = false);

        var result = validator.Validate([InlinePng()], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.MediaNotSupported, result.ErrorCode);
    }

    // ── Happy path ───────────────────────────────────────────────────

    [Fact]
    public void Validate_ValidInlinePng_Succeeds()
    {
        var result = Build().Validate([InlinePng(caption: "the failing dialog")], supportsMedia: true);

        Assert.True(result.Success);
        var attachment = Assert.Single(result.Attachments);
        Assert.Equal("image/png", attachment.MimeType);
        Assert.Equal("shot.png", attachment.FileName);
        Assert.Equal("the failing dialog", attachment.Caption);
        Assert.Equal(ImageContentSnifferTests.Png, attachment.Data);
        Assert.Null(attachment.Handle);
    }

    [Fact]
    public void Validate_MaximumAllowedCount_Succeeds()
    {
        var attachments = Enumerable.Range(0, 4).Select(_ => InlinePng()).ToList();

        var result = Build().Validate(attachments, supportsMedia: true);

        Assert.True(result.Success);
        Assert.Equal(4, result.Attachments.Count);
    }

    [Fact]
    public void Validate_DeclaredMimeTypeIsNormalised()
    {
        var result = Build().Validate([InlinePng(mimeType: "IMAGE/PNG")], supportsMedia: true);

        Assert.True(result.Success);
        Assert.Equal("image/png", Assert.Single(result.Attachments).MimeType);
    }

    // ── Count, size, and aggregate budget ────────────────────────────

    [Fact]
    public void Validate_TooManyAttachments_FailsWithTooManyAttachments()
    {
        var attachments = Enumerable.Range(0, 5).Select(_ => InlinePng()).ToList();

        var result = Build().Validate(attachments, supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.TooManyAttachments, result.ErrorCode);
    }

    [Fact]
    public void Validate_InlineAttachmentOverPerAttachmentCap_FailsWithAttachmentTooLarge()
    {
        var validator = Build(c => c.MaxInlineBytes = 1024);
        var payload = new ConnectorAttachmentPayload
        {
            MimeType = "image/png",
            Data = Convert.ToBase64String(PngOfSize(4096)),
        };

        var result = validator.Validate([payload], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.AttachmentTooLarge, result.ErrorCode);
    }

    [Fact]
    public void Validate_IndividuallyLegalAttachmentsThatOverflowTheFrame_FailWithAttachmentTooLarge()
    {
        // Each attachment is inside MaxInlineBytes, but together they exceed what one frame can
        // carry once base64-encoded. Without the aggregate budget this is a FATAL frame_too_large.
        var policy = ConnectorMediaPolicy.From(
            new ConnectorMediaConfig { MaxInlineBytes = 4096, MaxAttachmentsPerMessage = 4 },
            maxFrameBytes: ConnectorMediaPolicy.MinFrameBytes);
        var validator = new ConnectorAttachmentValidator(policy);

        var big = new ConnectorAttachmentPayload
        {
            MimeType = "image/png",
            Data = Convert.ToBase64String(PngOfSize(policy.MaxInlineBytes)),
        };

        // Precondition: each attachment on its own is legal.
        Assert.True(validator.Validate([big], supportsMedia: true).Success);

        var result = validator.Validate([big, big, big, big], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.AttachmentTooLarge, result.ErrorCode);
    }

    [Fact]
    public void Validate_ForgedSizeBytes_IsIgnoredInFavourOfActualLength()
    {
        // sizeBytes is a hint. A connector understating a large payload must still be caught.
        var validator = Build(c => c.MaxInlineBytes = 1024);
        var payload = new ConnectorAttachmentPayload
        {
            MimeType = "image/png",
            SizeBytes = 10,
            Data = Convert.ToBase64String(PngOfSize(8192)),
        };

        var result = validator.Validate([payload], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.AttachmentTooLarge, result.ErrorCode);
    }

    [Fact]
    public void Validate_EmptyData_FailsWithInvalidPayload()
    {
        var payload = new ConnectorAttachmentPayload
        {
            MimeType = "image/png",
            Data = Convert.ToBase64String([]),
        };

        var result = Build().Validate([payload], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.InvalidPayload, result.ErrorCode);
    }

    // ── Content verification ─────────────────────────────────────────

    [Fact]
    public void Validate_ContentDoesNotMatchDeclaredType_FailsWithTypeNotAllowed()
    {
        var payload = new ConnectorAttachmentPayload
        {
            MimeType = "image/png",
            Data = Convert.ToBase64String("<html>not an image</html>"u8.ToArray()),
        };

        var result = Build().Validate([payload], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.AttachmentTypeNotAllowed, result.ErrorCode);
    }

    [Fact]
    public void Validate_PngContentDeclaredAsJpeg_FailsWithTypeNotAllowed()
    {
        var payload = new ConnectorAttachmentPayload
        {
            MimeType = "image/jpeg",
            Data = Convert.ToBase64String(ImageContentSnifferTests.Png),
        };

        var result = Build().Validate([payload], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.AttachmentTypeNotAllowed, result.ErrorCode);
    }

    [Fact]
    public void Validate_MimeTypeOutsideAllowList_FailsWithTypeNotAllowed()
    {
        var validator = Build(c => c.AllowedMimeTypes = ["image/png"]);
        var payload = new ConnectorAttachmentPayload
        {
            MimeType = "image/gif",
            Data = Convert.ToBase64String(ImageContentSnifferTests.Gif89),
        };

        var result = validator.Validate([payload], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.AttachmentTypeNotAllowed, result.ErrorCode);
    }

    [Fact]
    public void Validate_MissingMimeType_FailsWithInvalidPayload()
    {
        var result = Build().Validate([InlinePng(mimeType: null)], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.InvalidPayload, result.ErrorCode);
    }

    [Fact]
    public void Validate_MalformedBase64_FailsWithInvalidPayload()
    {
        var payload = new ConnectorAttachmentPayload { MimeType = "image/png", Data = "!!!not base64!!!" };

        var result = Build().Validate([payload], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.InvalidPayload, result.ErrorCode);
    }

    // ── SECURITY: no connector-supplied URLs, ever ───────────────────

    [Fact]
    public void Validate_UrlField_IsRejected()
    {
        var payload = new ConnectorAttachmentPayload
        {
            MimeType = "image/png",
            Url = "file:///C:/Users/victim/AppData/Local/Cortex/secrets/secrets.json",
        };

        var result = Build().Validate([payload], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.InvalidPayload, result.ErrorCode);
    }

    [Fact]
    public void Validate_UrlAlongsideValidData_IsStillRejected()
    {
        // The URL must not be silently dropped just because a legitimate carrying mode is present.
        var payload = InlinePng() with { Url = "http://169.254.169.254/latest/meta-data/" };

        var result = Build().Validate([payload], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.InvalidPayload, result.ErrorCode);
    }

    // ── Carrying-mode exclusivity ────────────────────────────────────

    [Fact]
    public void Validate_NeitherDataNorHandle_FailsWithInvalidPayload()
    {
        var result = Build().Validate([new ConnectorAttachmentPayload { MimeType = "image/png" }], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.InvalidPayload, result.ErrorCode);
    }

    [Fact]
    public void Validate_BothDataAndHandle_FailsWithInvalidPayload()
    {
        var payload = InlinePng() with { Handle = "att_9f2c14e0" };

        var result = Build().Validate([payload], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.InvalidPayload, result.ErrorCode);
    }

    // ── Handles pass through for the caller to resolve ───────────────

    [Fact]
    public void Validate_WellFormedHandle_PassesThroughUnresolved()
    {
        var payload = new ConnectorAttachmentPayload { MimeType = "image/png", Handle = "att_9f2c14e0" };

        var result = Build().Validate([payload], supportsMedia: true);

        Assert.True(result.Success);
        var attachment = Assert.Single(result.Attachments);
        Assert.Equal("att_9f2c14e0", attachment.Handle);
        Assert.Null(attachment.Data);
    }

    [Theory]
    [InlineData("att/../../etc/passwd")]
    [InlineData("att 9f2c")]
    [InlineData("att:9f2c")]
    [InlineData("att\n9f2c")]
    public void Validate_MalformedHandle_FailsWithAttachmentNotFound(string handle)
    {
        var payload = new ConnectorAttachmentPayload { MimeType = "image/png", Handle = handle };

        var result = Build().Validate([payload], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.AttachmentNotFound, result.ErrorCode);
    }

    [Fact]
    public void Validate_OverlongHandle_FailsWithAttachmentNotFound()
    {
        var payload = new ConnectorAttachmentPayload
        {
            MimeType = "image/png",
            Handle = new string('a', 129),
        };

        var result = Build().Validate([payload], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.AttachmentNotFound, result.ErrorCode);
    }

    // ── File name sanitisation ───────────────────────────────────────

    [Theory]
    [InlineData("../../../windows/system32/evil.png", "......windows system32 evil.png")]
    [InlineData("C:\\secrets\\key.png", "Csecretskey.png")]
    public void SanitizeFileName_StripsPathComponents(string input, string _)
    {
        var sanitized = ConnectorAttachmentValidator.SanitizeFileName(input);

        Assert.NotNull(sanitized);
        Assert.DoesNotContain('/', sanitized);
        Assert.DoesNotContain('\\', sanitized);
        Assert.DoesNotContain(':', sanitized);
    }

    [Fact]
    public void SanitizeFileName_StripsControlCharacters()
    {
        var sanitized = ConnectorAttachmentValidator.SanitizeFileName("evil\r\nInjected-Log-Line.png");

        Assert.NotNull(sanitized);
        Assert.DoesNotContain('\r', sanitized);
        Assert.DoesNotContain('\n', sanitized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("/")]
    public void SanitizeFileName_DegenerateInput_ReturnsNull(string? input)
    {
        Assert.Null(ConnectorAttachmentValidator.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_PreservesNonLatinNames()
    {
        Assert.Equal("скриншот.png", ConnectorAttachmentValidator.SanitizeFileName("скриншот.png"));
    }

    [Fact]
    public void SanitizeFileName_StripsUnicodeFormatCharacters()
    {
        // A right-to-left override makes "shot.png<RLO>gpj.exe" RENDER as "shot.pngexe.jpg"
        // in a log viewer, which is a spoofing aid even though the name never opens a file.
        var sanitized = ConnectorAttachmentValidator.SanitizeFileName("shot.png\u202egpj.exe");

        Assert.NotNull(sanitized);
        Assert.DoesNotContain('\u202e', sanitized);
    }

    [Fact]
    public void SanitizeFileName_LongNameDoesNotUseTheStack()
    {
        // The input length is attacker-controlled, so it must never size a stack allocation.
        var sanitized = ConnectorAttachmentValidator.SanitizeFileName(new string('n', 100_000));

        Assert.NotNull(sanitized);
        Assert.Equal(100_000, sanitized.Length);
    }

    [Fact]
    public void Validate_OverlongFileNameAndCaption_AreTruncatedNotRejected()
    {
        var payload = InlinePng(fileName: new string('n', 500), caption: new string('c', 5000));

        var result = Build().Validate([payload], supportsMedia: true);

        Assert.True(result.Success);
        var attachment = Assert.Single(result.Attachments);
        Assert.Equal(ConnectorAttachmentValidator.MaxFileNameLength, attachment.FileName!.Length);
        Assert.Equal(ConnectorAttachmentValidator.MaxCaptionLength, attachment.Caption!.Length);
    }

    // ── Decoded-length lower bound ───────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 1)]
    [InlineData(8, 4)]
    [InlineData(1000, 748)]
    public void MinDecodedLength_IsALowerBoundOnTheDecodedSize(int base64Length, long expected)
    {
        Assert.Equal(expected, ConnectorAttachmentValidator.MinDecodedLength(base64Length));
    }

    [Fact]
    public void MinDecodedLength_NeverOverstatesRealPayloads()
    {
        // Overstating would reject a legal attachment before it is ever decoded.
        for (var rawLength = 1; rawLength < 512; rawLength++)
        {
            var encoded = Convert.ToBase64String(new byte[rawLength]);
            Assert.True(
                ConnectorAttachmentValidator.MinDecodedLength(encoded.Length) <= rawLength,
                $"lower bound overstated the decoded size for {rawLength} raw bytes");
        }
    }

    [Fact]
    public void Validate_AttachmentOfExactlyMaxInlineBytes_IsAccepted()
    {
        // Regression: a naive length*3/4 upper bound overshoots by up to two bytes because of
        // base64 padding, which refused a payload sitting exactly on the limit.
        var validator = Build(c => c.MaxInlineBytes = 4096);
        var payload = new ConnectorAttachmentPayload
        {
            MimeType = "image/png",
            Data = Convert.ToBase64String(PngOfSize(4096)),
        };

        Assert.True(validator.Validate([payload], supportsMedia: true).Success);
    }

    [Fact]
    public void Validate_AttachmentOneByteOverMaxInlineBytes_IsRejected()
    {
        var validator = Build(c => c.MaxInlineBytes = 4096);
        var payload = new ConnectorAttachmentPayload
        {
            MimeType = "image/png",
            Data = Convert.ToBase64String(PngOfSize(4097)),
        };

        var result = validator.Validate([payload], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.AttachmentTooLarge, result.ErrorCode);
    }

    [Fact]
    public void Validate_GrosslyOversizeAttachment_IsRejectedBeforeDecoding()
    {
        // The cheap pre-check must fire, not the post-decode check, so a hostile connector cannot
        // force a large allocation simply to have it thrown away.
        var validator = Build(c => c.MaxInlineBytes = 1024);
        var payload = new ConnectorAttachmentPayload
        {
            MimeType = "image/png",
            Data = Convert.ToBase64String(PngOfSize(256 * 1024)),
        };

        var result = validator.Validate([payload], supportsMedia: true);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.AttachmentTooLarge, result.ErrorCode);
        Assert.Contains("at least", result.ErrorMessage, StringComparison.Ordinal);
    }
}
