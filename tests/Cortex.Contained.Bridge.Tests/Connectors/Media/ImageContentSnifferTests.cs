using Cortex.Contained.Bridge.Connectors.Media;

namespace Cortex.Contained.Bridge.Tests.Connectors.Media;

/// <summary>
/// Tests <see cref="ImageContentSniffer"/>. This is the component that turns a connector's
/// <em>claim</em> about content type into a <em>verified</em> fact, so the negative cases matter
/// as much as the positive ones.
/// </summary>
public sealed class ImageContentSnifferTests
{
    public static readonly byte[] Png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];

    public static readonly byte[] Gif87 = [.. "GIF87a"u8, 0x01, 0x00];

    public static readonly byte[] Gif89 = [.. "GIF89a"u8, 0x01, 0x00];

    public static readonly byte[] Webp = [.. "RIFF"u8, 0x1A, 0x00, 0x00, 0x00, .. "WEBP"u8, 0x56, 0x50];

    [Theory]
    [MemberData(nameof(KnownFormats))]
    public void DetectMimeType_KnownSignature_ReturnsExpectedType(byte[] content, string expected)
    {
        Assert.Equal(expected, ImageContentSniffer.DetectMimeType(content));
    }

    public static TheoryData<byte[], string> KnownFormats() => new()
    {
        { Png, "image/png" },
        { Jpeg, "image/jpeg" },
        { Gif87, "image/gif" },
        { Gif89, "image/gif" },
        { Webp, "image/webp" },
    };

    [Fact]
    public void DetectMimeType_Empty_ReturnsNull()
    {
        Assert.Null(ImageContentSniffer.DetectMimeType([]));
    }

    [Fact]
    public void DetectMimeType_UnknownContent_ReturnsNull()
    {
        Assert.Null(ImageContentSniffer.DetectMimeType("<html><body>hi</body></html>"u8));
    }

    [Fact]
    public void DetectMimeType_WindowsExecutable_ReturnsNull()
    {
        Assert.Null(ImageContentSniffer.DetectMimeType([0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00]));
    }

    [Fact]
    public void DetectMimeType_TruncatedPngSignature_ReturnsNull()
    {
        Assert.Null(ImageContentSniffer.DetectMimeType([0x89, 0x50, 0x4E]));
    }

    [Fact]
    public void DetectMimeType_RiffThatIsNotWebp_ReturnsNull()
    {
        // A WAV file is also a RIFF container; only the WEBP form-type may pass.
        byte[] wav = [.. "RIFF"u8, 0x24, 0x00, 0x00, 0x00, .. "WAVE"u8];

        Assert.Null(ImageContentSniffer.DetectMimeType(wav));
    }

    [Fact]
    public void DetectMimeType_RiffTooShortForFormType_ReturnsNull()
    {
        Assert.Null(ImageContentSniffer.DetectMimeType([.. "RIFF"u8, 0x00, 0x00, 0x00]));
    }

    [Fact]
    public void MatchesDeclaredType_ContentMatchesClaim_ReturnsTrue()
    {
        Assert.True(ImageContentSniffer.MatchesDeclaredType(Png, "image/png"));
    }

    [Fact]
    public void MatchesDeclaredType_IsCaseAndParameterInsensitive()
    {
        Assert.True(ImageContentSniffer.MatchesDeclaredType(Png, "IMAGE/PNG; charset=binary"));
    }

    [Fact]
    public void MatchesDeclaredType_PngLabelledAsJpeg_ReturnsFalse()
    {
        // The whole point: a connector cannot smuggle content past the allow-list by mislabelling it.
        Assert.False(ImageContentSniffer.MatchesDeclaredType(Png, "image/jpeg"));
    }

    [Fact]
    public void MatchesDeclaredType_HtmlLabelledAsPng_ReturnsFalse()
    {
        Assert.False(ImageContentSniffer.MatchesDeclaredType("<script>"u8, "image/png"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MatchesDeclaredType_BlankDeclaredType_ReturnsFalse(string? declared)
    {
        Assert.False(ImageContentSniffer.MatchesDeclaredType(Png, declared));
    }
}
