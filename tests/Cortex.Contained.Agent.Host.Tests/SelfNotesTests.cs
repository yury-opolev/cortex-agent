using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;

namespace Cortex.Contained.Agent.Host.Tests;

public class SelfNotesTests : IDisposable
{
    private readonly string _tempDir;

    public SelfNotesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cortex-selfnotes-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ── SelfNotesStore ──────────────────────────────────────────────

    [Fact]
    public void Read_NoFile_ReturnsDefaultContent()
    {
        var store = new SelfNotesStore(Path.Combine(_tempDir, "notes.md"));

        var content = store.Read();

        Assert.Contains("Getting started", content);
        Assert.Contains("self_notes_write", content);
    }

    [Fact]
    public void Write_ThenRead_ReturnsWrittenContent()
    {
        var store = new SelfNotesStore(Path.Combine(_tempDir, "notes.md"));

        store.Write("My custom notes");
        var content = store.Read();

        Assert.Equal("My custom notes", content);
    }

    [Fact]
    public void Write_ExceedsBudget_ReturnsFalse()
    {
        var store = new SelfNotesStore(Path.Combine(_tempDir, "notes.md"));
        var tooLong = new string('x', SelfNotesStore.MaxCharacters + 1);

        var result = store.Write(tooLong);

        Assert.False(result);
    }

    [Fact]
    public void Write_ExactlyAtBudget_Succeeds()
    {
        var store = new SelfNotesStore(Path.Combine(_tempDir, "notes.md"));
        var exact = new string('x', SelfNotesStore.MaxCharacters);

        var result = store.Write(exact);

        Assert.True(result);
        Assert.Equal(exact, store.Read());
    }

    [Fact]
    public void Read_EmptyFile_ReturnsDefault()
    {
        var path = Path.Combine(_tempDir, "notes.md");
        File.WriteAllText(path, "");
        var store = new SelfNotesStore(path);

        var content = store.Read();

        Assert.Contains("Getting started", content);
    }

    // ── SelfNotesReadTool ───────────────────────────────────────────

    [Fact]
    public async Task ReadTool_ReturnsStoreContent()
    {
        var store = new SelfNotesStore(Path.Combine(_tempDir, "notes.md"));
        store.Write("Test content");
        var tool = new SelfNotesReadTool(store);

        var result = await tool.ExecuteAsync("{}", new ToolExecutionContext { ConversationId = "test", ChannelId = "test" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Test content", result.Content);
    }

    // ── SelfNotesWriteTool ──────────────────────────────────────────

    [Fact]
    public async Task WriteTool_ValidContent_Succeeds()
    {
        var store = new SelfNotesStore(Path.Combine(_tempDir, "notes.md"));
        var tool = new SelfNotesWriteTool(store);

        var result = await tool.ExecuteAsync("""{"content": "New notes"}""", new ToolExecutionContext { ConversationId = "test", ChannelId = "test" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("New notes", store.Read());
    }

    [Fact]
    public async Task WriteTool_TooLong_Fails()
    {
        var store = new SelfNotesStore(Path.Combine(_tempDir, "notes.md"));
        var tool = new SelfNotesWriteTool(store);
        var tooLong = new string('x', SelfNotesStore.MaxCharacters + 1);

        var result = await tool.ExecuteAsync($$$"""{"content": "{{{tooLong}}}"}""", new ToolExecutionContext { ConversationId = "test", ChannelId = "test" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("too long", result.Content);
    }

    [Fact]
    public async Task WriteTool_MissingContent_Fails()
    {
        var store = new SelfNotesStore(Path.Combine(_tempDir, "notes.md"));
        var tool = new SelfNotesWriteTool(store);

        var result = await tool.ExecuteAsync("{}", new ToolExecutionContext { ConversationId = "test", ChannelId = "test" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Missing", result.Content);
    }
}
