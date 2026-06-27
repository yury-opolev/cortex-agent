using System.Text.Json;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;

namespace Cortex.Contained.Agent.Host.Tests;

public class ContextBootstrapToolTests : IDisposable
{
    private static readonly ToolExecutionContext Context = new()
    {
        ConversationId = "conv-test",
        ChannelId = "webchat-default",
    };

    private readonly string _tempDir;
    private readonly string _filePath;

    public ContextBootstrapToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"bootstrap-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "context-bootstrap.md");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static string Args(object obj) => JsonSerializer.Serialize(obj);

    [Fact]
    public async Task Update_ValidContent_Succeeds()
    {
        var tool = new ContextBootstrapTool(_filePath);

        var result = await tool.ExecuteAsync(
            Args(new { content = "User: Alice. Engineer in Berlin." }),
            Context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("updated", result.Content);
        Assert.Equal("User: Alice. Engineer in Berlin.", await File.ReadAllTextAsync(_filePath));
    }

    [Fact]
    public async Task Update_EmptyContent_Rejected()
    {
        var tool = new ContextBootstrapTool(_filePath);

        var result = await tool.ExecuteAsync(
            Args(new { content = "" }),
            Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("cannot be empty", result.Error);
        Assert.False(File.Exists(_filePath));
    }

    [Fact]
    public async Task Update_WhitespaceContent_Rejected()
    {
        var tool = new ContextBootstrapTool(_filePath);

        var result = await tool.ExecuteAsync(
            Args(new { content = "   \n  \t  " }),
            Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("cannot be empty", result.Error);
    }

    [Fact]
    public async Task Update_TooLongContent_Rejected()
    {
        var tool = new ContextBootstrapTool(_filePath);
        var longContent = new string('x', 2001);

        var result = await tool.ExecuteAsync(
            Args(new { content = longContent }),
            Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("too long", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2001", result.Error);
        Assert.False(File.Exists(_filePath));
    }

    [Fact]
    public async Task Update_ExactlyAtLimit_Succeeds()
    {
        var tool = new ContextBootstrapTool(_filePath);
        var maxContent = new string('x', 2000);

        var result = await tool.ExecuteAsync(
            Args(new { content = maxContent }),
            Context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(maxContent, await File.ReadAllTextAsync(_filePath));
    }

    [Fact]
    public async Task Update_MissingContentParam_Rejected()
    {
        var tool = new ContextBootstrapTool(_filePath);

        var result = await tool.ExecuteAsync(
            "{}",
            Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Missing required parameter", result.Error);
    }

    [Fact]
    public async Task Update_InvalidJson_Rejected()
    {
        var tool = new ContextBootstrapTool(_filePath);

        var result = await tool.ExecuteAsync(
            "not json",
            Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Invalid JSON", result.Error);
    }

    [Fact]
    public async Task Update_TrimsWhitespace()
    {
        var tool = new ContextBootstrapTool(_filePath);

        var result = await tool.ExecuteAsync(
            Args(new { content = "  User: Alice.  \n  " }),
            Context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("User: Alice.", await File.ReadAllTextAsync(_filePath));
    }

    [Fact]
    public async Task Update_ReportsCharacterCount()
    {
        var tool = new ContextBootstrapTool(_filePath);

        var result = await tool.ExecuteAsync(
            Args(new { content = "User: Bob." }),
            Context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("10 characters", result.Content);
    }

    [Fact]
    public async Task Update_OverwritesPreviousContent()
    {
        var tool = new ContextBootstrapTool(_filePath);

        await tool.ExecuteAsync(
            Args(new { content = "User: Alice." }),
            Context, CancellationToken.None);

        await tool.ExecuteAsync(
            Args(new { content = "User: Bob." }),
            Context, CancellationToken.None);

        Assert.Equal("User: Bob.", await File.ReadAllTextAsync(_filePath));
    }

    [Fact]
    public async Task Update_CreatesDirectoryIfNeeded()
    {
        var nestedPath = Path.Combine(_tempDir, "nested", "dir", "context-bootstrap.md");
        var tool = new ContextBootstrapTool(nestedPath);

        var result = await tool.ExecuteAsync(
            Args(new { content = "User: Alice." }),
            Context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(nestedPath));
    }
}
