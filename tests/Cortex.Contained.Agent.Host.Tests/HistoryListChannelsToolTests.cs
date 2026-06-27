using System.Text.Json;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class HistoryListChannelsToolTests : IAsyncDisposable
{
    private static readonly ToolExecutionContext _context = new()
    {
        ConversationId = "conv-test",
        ChannelId = "webchat-default",
    };

    private readonly string _tempDir;
    private readonly MessageStore _store;

    public HistoryListChannelsToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"history-list-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new MessageStore(
            Path.Combine(_tempDir, "messages.db"),
            NullLogger<MessageStore>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task EmptyStore_ReturnsEmptyJsonArray()
    {
        var tool = new HistoryListChannelsTool(_store);

        var result = await tool.ExecuteAsync("{}", _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("[]", result.Content);
    }

    [Fact]
    public void ToolName_IsHistoryListChannels()
    {
        var tool = new HistoryListChannelsTool(_store);
        Assert.Equal("history_list_channels", tool.Name);
    }

    [Fact]
    public async Task TwoChannels_ReturnsSortedByLastActivityDescending()
    {
        var older = DateTimeOffset.UtcNow.AddHours(-2);
        var newer = DateTimeOffset.UtcNow.AddMinutes(-5);

        await _store.SaveMessageAsync("u", "older-channel", "user", "old", older);
        await _store.SaveMessageAsync("u", "newer-channel", "user", "new1", newer.AddMinutes(-1));
        await _store.SaveMessageAsync("u", "newer-channel", "assistant", "new2", newer);

        var tool = new HistoryListChannelsTool(_store);
        var result = await tool.ExecuteAsync("{}", _context, CancellationToken.None);

        Assert.True(result.Success);

        using var doc = JsonDocument.Parse(result.Content);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());

        var first = doc.RootElement[0];
        var second = doc.RootElement[1];

        Assert.Equal("newer-channel", first.GetProperty("channelId").GetString());
        Assert.Equal(2, first.GetProperty("messageCount").GetInt32());
        Assert.True(first.TryGetProperty("lastActivity", out _));

        Assert.Equal("older-channel", second.GetProperty("channelId").GetString());
        Assert.Equal(1, second.GetProperty("messageCount").GetInt32());
    }
}
