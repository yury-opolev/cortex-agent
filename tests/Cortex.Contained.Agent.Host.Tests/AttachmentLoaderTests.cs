using Cortex.Contained.Agent.Host.Tools;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Pins the sandbox-safe image loader behind the send_message attachments param.
/// Validation rules: path stays in the sandbox, file exists and is non-empty,
/// type is a known image, size is within the 8 MB cap.
/// </summary>
public sealed class AttachmentLoaderTests : IDisposable
{
    private readonly string _sandbox;
    private readonly AttachmentLoader _loader;

    public AttachmentLoaderTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "cortex-attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _loader = new AttachmentLoader(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch (IOException) { }
    }

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_sandbox, name);
        File.WriteAllBytes(path, bytes);
        return name;
    }

    [Fact]
    public void Load_ValidPng_ReturnsAttachmentWithBytesAndMime()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var rel = WriteFile("chart.png", bytes);

        var result = _loader.Load(rel);

        Assert.True(result.Success);
        Assert.NotNull(result.Attachment);
        Assert.Equal("image/png", result.Attachment!.MimeType);
        Assert.Equal("chart.png", result.Attachment.FileName);
        Assert.Equal(bytes, result.Attachment.Data);
        Assert.Equal(bytes.LongLength, result.Attachment.SizeBytes);
    }

    [Theory]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.webp", "image/webp")]
    public void Load_KnownImageExtensions_MapToMime(string name, string expectedMime)
    {
        var rel = WriteFile(name, new byte[] { 1, 2, 3, 4 });

        var result = _loader.Load(rel);

        Assert.True(result.Success);
        Assert.Equal(expectedMime, result.Attachment!.MimeType);
    }

    [Fact]
    public void Load_MissingFile_Fails()
    {
        var result = _loader.Load("nope.png");

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_PathTraversal_Fails()
    {
        var result = _loader.Load("../../etc/passwd.png");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Load_NonImageType_Fails()
    {
        var rel = WriteFile("notes.txt", new byte[] { 1, 2, 3 });

        var result = _loader.Load(rel);

        Assert.False(result.Success);
        Assert.Contains("type", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_Oversize_Fails()
    {
        var rel = WriteFile("big.png", new byte[(8 * 1024 * 1024) + 1]);

        var result = _loader.Load(rel);

        Assert.False(result.Success);
        Assert.Contains("8", result.Error!); // mentions the 8 MB cap
    }

    [Fact]
    public void Load_EmptyFile_Fails()
    {
        var rel = WriteFile("empty.png", []);

        var result = _loader.Load(rel);

        Assert.False(result.Success);
        Assert.Contains("empty", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
