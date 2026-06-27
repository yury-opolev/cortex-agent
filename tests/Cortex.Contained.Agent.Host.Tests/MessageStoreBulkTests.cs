using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Tests for <see cref="MessageStore"/> bulk operations (GetAllMessagesAsync, BulkInsertAsync).
/// </summary>
public class MessageStoreBulkTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly MessageStore _store;

    public MessageStoreBulkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"msgstore-bulk-tests-{Guid.NewGuid():N}");
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

    // ── GetAllMessagesAsync ──────────────────────────────────────────

    [Fact]
    public async Task GetAllMessagesAsync_MultipleChannels_ReturnsAll()
    {
        await _store.SaveMessageAsync("u1", "ch-a", "user", "A1", DateTimeOffset.UtcNow);
        await _store.SaveMessageAsync("u2", "ch-b", "user", "B1", DateTimeOffset.UtcNow);
        await _store.SaveMessageAsync("u1", "ch-a", "assistant", "A2", DateTimeOffset.UtcNow);

        var all = await _store.GetAllMessagesAsync();

        Assert.Equal(3, all.Count);
        Assert.Contains(all, m => m.Content == "A1" && m.ChannelId == "ch-a");
        Assert.Contains(all, m => m.Content == "B1" && m.ChannelId == "ch-b");
        Assert.Contains(all, m => m.Content == "A2" && m.ChannelId == "ch-a");
    }

    [Fact]
    public async Task GetAllMessagesAsync_ChronologicalOrder()
    {
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-3);
        var t2 = DateTimeOffset.UtcNow.AddMinutes(-2);
        var t3 = DateTimeOffset.UtcNow.AddMinutes(-1);

        await _store.SaveMessageAsync("u", "ch", "user", "First", t1);
        await _store.SaveMessageAsync("u", "ch", "user", "Second", t2);
        await _store.SaveMessageAsync("u", "ch", "user", "Third", t3);

        var all = await _store.GetAllMessagesAsync();

        Assert.Equal(3, all.Count);
        Assert.Equal("First", all[0].Content);
        Assert.Equal("Second", all[1].Content);
        Assert.Equal("Third", all[2].Content);
    }

    [Fact]
    public async Task GetAllMessagesAsync_VisibilityFiltering()
    {
        await _store.SaveMessageAsync("u", "ch", "user", "Normal", DateTimeOffset.UtcNow, category: MessageCategory.Normal);
        await _store.SaveMessageAsync("u", "ch", "user", "Internal", DateTimeOffset.UtcNow, category: MessageCategory.Internal);
        await _store.SaveMessageAsync("u", "ch", "user", "System", DateTimeOffset.UtcNow, category: MessageCategory.System);

        var seeding = await _store.GetAllMessagesAsync(MessageVisibility.Seeding);
        var history = await _store.GetAllMessagesAsync(MessageVisibility.History);
        var all = await _store.GetAllMessagesAsync(MessageVisibility.All);

        Assert.Single(seeding);
        Assert.Equal(2, history.Count);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task GetAllMessagesAsync_Empty_ReturnsEmptyList()
    {
        var all = await _store.GetAllMessagesAsync();

        Assert.Empty(all);
    }

    // ── BulkInsertAsync ──────────────────────────────────────────────

    [Fact]
    public async Task BulkInsertAsync_InsertsAllRecords()
    {
        var records = new List<MessageRecord>
        {
            new()
            {
                UserId = "u1", ChannelId = "ch", Role = "user",
                Content = "Msg 1", Timestamp = DateTimeOffset.UtcNow,
            },
            new()
            {
                UserId = "u1", ChannelId = "ch", Role = "assistant",
                Content = "Msg 2", Timestamp = DateTimeOffset.UtcNow,
            },
            new()
            {
                UserId = "u2", ChannelId = "ch-b", Role = "user",
                Content = "Msg 3", Timestamp = DateTimeOffset.UtcNow,
            },
        };

        var inserted = await _store.BulkInsertAsync(records);

        Assert.Equal(3, inserted);
        Assert.Equal(3, await _store.GetTotalMessageCountAsync());
    }

    [Fact]
    public async Task BulkInsertAsync_EmptyList_ReturnsZero()
    {
        var inserted = await _store.BulkInsertAsync([]);

        Assert.Equal(0, inserted);
        Assert.Equal(0, await _store.GetTotalMessageCountAsync());
    }

    [Fact]
    public async Task BulkInsertAsync_PreservesAllFields()
    {
        var ts = new DateTimeOffset(2026, 3, 20, 14, 30, 0, TimeSpan.Zero);
        var records = new List<MessageRecord>
        {
            new()
            {
                UserId = "user-full",
                ChannelId = "ch-full",
                Role = "assistant",
                Content = "Full fields test",
                Timestamp = ts,
                MessageId = "msg-full-001",
                Category = MessageCategory.System,
            },
        };

        await _store.BulkInsertAsync(records);

        var all = await _store.GetAllMessagesAsync();
        Assert.Single(all);

        var msg = all[0];
        Assert.Equal("user-full", msg.UserId);
        Assert.Equal("ch-full", msg.ChannelId);
        Assert.Equal("assistant", msg.Role);
        Assert.Equal("Full fields test", msg.Content);
        Assert.Equal(ts, msg.Timestamp);
        Assert.Equal("msg-full-001", msg.MessageId);
        Assert.Equal(MessageCategory.System, msg.Category);
    }
}
