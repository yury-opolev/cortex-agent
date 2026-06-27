using Cortex.Contained.Agent.Host.Tools;

namespace Cortex.Contained.Agent.Host.Tests;

public class SandboxPathResolverTests : IDisposable
{
    private readonly string _sandbox;

    public SandboxPathResolverTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "sandbox_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        if (Directory.Exists(_sandbox))
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Resolve_SimplePath_ReturnsFullPath()
    {
        var result = SandboxPathResolver.Resolve(_sandbox, "hello.txt");

        Assert.Equal(Path.Combine(Path.GetFullPath(_sandbox), "hello.txt"), result);
    }

    [Fact]
    public void Resolve_NestedPath_ReturnsFullPath()
    {
        var result = SandboxPathResolver.Resolve(_sandbox, "sub/dir/file.txt");

        var expected = Path.Combine(Path.GetFullPath(_sandbox), "sub", "dir", "file.txt");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_Dot_ReturnsSandboxRoot()
    {
        var result = SandboxPathResolver.Resolve(_sandbox, ".");

        Assert.Equal(Path.GetFullPath(_sandbox), result);
    }

    [Fact]
    public void Resolve_LeadingSlash_TreatedAsRelative()
    {
        var result = SandboxPathResolver.Resolve(_sandbox, "/hello.txt");

        Assert.Equal(Path.Combine(Path.GetFullPath(_sandbox), "hello.txt"), result);
    }

    [Fact]
    public void Resolve_BackslashPath_NormalizedToForwardSlash()
    {
        var result = SandboxPathResolver.Resolve(_sandbox, "sub\\dir\\file.txt");

        var expected = Path.Combine(Path.GetFullPath(_sandbox), "sub", "dir", "file.txt");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_ParentTraversal_ThrowsArgumentException()
    {
        Assert.Throws<SandboxEscapeException>(() =>
            SandboxPathResolver.Resolve(_sandbox, "../escape.txt"));
    }

    [Fact]
    public void Resolve_UncPath_ThrowsSandboxEscapeException()
    {
        var ex = Assert.Throws<SandboxEscapeException>(() =>
            SandboxPathResolver.Resolve(_sandbox, @"\\server\share\x"));
        Assert.Contains("UNC", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_DevicePath_ThrowsSandboxEscapeException()
    {
        Assert.Throws<SandboxEscapeException>(() =>
            SandboxPathResolver.Resolve(_sandbox, @"\\?\C:\Windows"));
    }

    [Fact]
    public void Resolve_ForwardSlashUncPath_ThrowsSandboxEscapeException()
    {
        Assert.Throws<SandboxEscapeException>(() =>
            SandboxPathResolver.Resolve(_sandbox, "//server/share/x"));
    }

    [Fact]
    public void Resolve_NestedParentTraversal_ThrowsArgumentException()
    {
        Assert.Throws<SandboxEscapeException>(() =>
            SandboxPathResolver.Resolve(_sandbox, "sub/../../escape.txt"));
    }

    [Fact]
    public void Resolve_EmptyPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            SandboxPathResolver.Resolve(_sandbox, ""));
    }

    [Fact]
    public void Resolve_WhitespacePath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            SandboxPathResolver.Resolve(_sandbox, "   "));
    }

    [Fact]
    public void ResolveAndVerify_ExistingFile_ReturnsPath()
    {
        var filePath = Path.Combine(_sandbox, "existing.txt");
        File.WriteAllText(filePath, "content");

        var result = SandboxPathResolver.ResolveAndVerify(_sandbox, "existing.txt");

        Assert.Equal(filePath, result);
    }

    [Fact]
    public void ResolveAndVerify_NonExistingFile_ReturnsPathWithoutError()
    {
        // ResolveAndVerify should not throw for non-existing files
        // (only symlink targets are checked)
        var result = SandboxPathResolver.ResolveAndVerify(_sandbox, "nonexistent.txt");

        Assert.Contains("nonexistent.txt", result);
    }

    [Fact]
    public void Resolve_DeepTraversal_ThrowsArgumentException()
    {
        Assert.Throws<SandboxEscapeException>(() =>
            SandboxPathResolver.Resolve(_sandbox, "a/b/c/../../../../escape.txt"));
    }
}
