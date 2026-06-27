using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Tests for <see cref="MessageStore"/>. Verifies SQLite persistence of
/// messages including CRUD, search, seeding, visibility filtering, and time range queries.
/// </summary>
public class MessageStoreTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly MessageStore _store;

    public MessageStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"msgstore-tests-{Guid.NewGuid():N}");
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

    // ── Save and retrieve ──────────────────────────────────────────────

    [Fact]
    public async Task SaveAndRetrieve_SingleMessage()
    {
        await _store.SaveMessageAsync("user1", "webchat", "user", "Hello", DateTimeOffset.UtcNow);

        var messages = await _store.GetMessagesAsync("webchat");

        Assert.Single(messages);
        Assert.Equal("user1", messages[0].UserId);
        Assert.Equal("webchat", messages[0].ChannelId);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("Hello", messages[0].Content);
    }

    [Fact]
    public async Task Messages_ReturnedOldestFirst()
    {
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-2);
        var t2 = DateTimeOffset.UtcNow.AddMinutes(-1);
        var t3 = DateTimeOffset.UtcNow;

        await _store.SaveMessageAsync("u", "ch", "user", "First", t1);
        await _store.SaveMessageAsync("u", "ch", "assistant", "Second", t2);
        await _store.SaveMessageAsync("u", "ch", "user", "Third", t3);

        var messages = await _store.GetMessagesAsync("ch");

        Assert.Equal(3, messages.Count);
        Assert.Equal("First", messages[0].Content);
        Assert.Equal("Second", messages[1].Content);
        Assert.Equal("Third", messages[2].Content);
    }

    [Fact]
    public async Task Messages_FilteredByChannel()
    {
        await _store.SaveMessageAsync("u", "ch-a", "user", "A message", DateTimeOffset.UtcNow);
        await _store.SaveMessageAsync("u", "ch-b", "user", "B message", DateTimeOffset.UtcNow);

        var a = await _store.GetMessagesAsync("ch-a");
        var b = await _store.GetMessagesAsync("ch-b");

        Assert.Single(a);
        Assert.Equal("A message", a[0].Content);
        Assert.Single(b);
        Assert.Equal("B message", b[0].Content);
    }

    [Fact]
    public async Task Messages_LimitRespected()
    {
        for (int i = 0; i < 10; i++)
            await _store.SaveMessageAsync("u", "ch", "user", $"Message {i}", DateTimeOffset.UtcNow.AddMinutes(i));

        var messages = await _store.GetMessagesAsync("ch", limit: 3);

        Assert.Equal(3, messages.Count);
        // Should be the last 3 (newest), returned oldest-first
        Assert.Equal("Message 7", messages[0].Content);
        Assert.Equal("Message 8", messages[1].Content);
        Assert.Equal("Message 9", messages[2].Content);
    }

    // ── Visibility filtering ───────────────────────────────────────────

    [Fact]
    public async Task Visibility_Seeding_ExcludesInternalAndSystem()
    {
        await _store.SaveMessageAsync("u", "ch", "user", "Normal", DateTimeOffset.UtcNow, category: MessageCategory.Normal);
        await _store.SaveMessageAsync("u", "ch", "user", "Internal", DateTimeOffset.UtcNow, category: MessageCategory.Internal);
        await _store.SaveMessageAsync("u", "ch", "user", "System", DateTimeOffset.UtcNow, category: MessageCategory.System);

        var messages = await _store.GetMessagesAsync("ch", visibility: MessageVisibility.Seeding);

        Assert.Single(messages);
        Assert.Equal("Normal", messages[0].Content);
    }

    [Fact]
    public async Task Visibility_History_IncludesNormalAndSystem()
    {
        await _store.SaveMessageAsync("u", "ch", "user", "Normal", DateTimeOffset.UtcNow, category: MessageCategory.Normal);
        await _store.SaveMessageAsync("u", "ch", "user", "Internal", DateTimeOffset.UtcNow, category: MessageCategory.Internal);
        await _store.SaveMessageAsync("u", "ch", "user", "System", DateTimeOffset.UtcNow, category: MessageCategory.System);

        var messages = await _store.GetMessagesAsync("ch", visibility: MessageVisibility.History);

        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, m => m.Content == "Normal");
        Assert.Contains(messages, m => m.Content == "System");
    }

    [Fact]
    public async Task Visibility_All_IncludesEverything()
    {
        await _store.SaveMessageAsync("u", "ch", "user", "Normal", DateTimeOffset.UtcNow, category: MessageCategory.Normal);
        await _store.SaveMessageAsync("u", "ch", "user", "Internal", DateTimeOffset.UtcNow, category: MessageCategory.Internal);
        await _store.SaveMessageAsync("u", "ch", "user", "System", DateTimeOffset.UtcNow, category: MessageCategory.System);

        var messages = await _store.GetMessagesAsync("ch", visibility: MessageVisibility.All);

        Assert.Equal(3, messages.Count);
    }

    // ── Time range queries ─────────────────────────────────────────────

    [Fact]
    public async Task GetMessages_After_FiltersCorrectly()
    {
        var t1 = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 3, 17, 0, 0, 0, TimeSpan.Zero);

        await _store.SaveMessageAsync("u", "ch", "user", "Old", t1);
        await _store.SaveMessageAsync("u", "ch", "user", "Mid", t2);
        await _store.SaveMessageAsync("u", "ch", "user", "New", t3);

        var cutoff = new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero);
        var messages = await _store.GetMessagesAsync("ch", after: cutoff);

        Assert.Equal(2, messages.Count);
        Assert.Equal("Mid", messages[0].Content);
        Assert.Equal("New", messages[1].Content);
    }

    [Fact]
    public async Task GetMessages_Before_FiltersCorrectly()
    {
        var t1 = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 3, 17, 0, 0, 0, TimeSpan.Zero);

        await _store.SaveMessageAsync("u", "ch", "user", "Old", t1);
        await _store.SaveMessageAsync("u", "ch", "user", "Mid", t2);
        await _store.SaveMessageAsync("u", "ch", "user", "New", t3);

        var cutoff = new DateTimeOffset(2026, 3, 17, 0, 0, 0, TimeSpan.Zero);
        var messages = await _store.GetMessagesAsync("ch", before: cutoff);

        Assert.Equal(2, messages.Count);
        Assert.Equal("Old", messages[0].Content);
        Assert.Equal("Mid", messages[1].Content);
    }

    // ── Search ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_FindsMatchingMessages()
    {
        await _store.SaveMessageAsync("u", "ch", "user", "The weather is nice today", DateTimeOffset.UtcNow);
        await _store.SaveMessageAsync("u", "ch", "assistant", "I agree about the weather", DateTimeOffset.UtcNow);
        await _store.SaveMessageAsync("u", "ch", "user", "Tell me about dogs", DateTimeOffset.UtcNow);

        var results = await _store.SearchMessagesAsync("weather");

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Search_ReturnsEmpty_WhenNoMatch()
    {
        await _store.SaveMessageAsync("u", "ch", "user", "Hello world", DateTimeOffset.UtcNow);

        var results = await _store.SearchMessagesAsync("nonexistent");

        Assert.Empty(results);
    }

    // ── Conversations ──────────────────────────────────────────────────

    [Fact]
    public async Task GetConversations_GroupsByChannel()
    {
        await _store.SaveMessageAsync("u", "webchat", "user", "Hi", DateTimeOffset.UtcNow.AddMinutes(-1));
        await _store.SaveMessageAsync("u", "webchat", "assistant", "Hello", DateTimeOffset.UtcNow);
        await _store.SaveMessageAsync("u", "discord-dm", "user", "Hey", DateTimeOffset.UtcNow);

        var conversations = await _store.GetConversationsAsync();

        Assert.Equal(2, conversations.Count);
        var webchat = conversations.First(c => c.ChannelId == "webchat");
        Assert.Equal(2, webchat.MessageCount);
    }

    [Fact]
    public async Task GetConversations_FilterByChannel()
    {
        await _store.SaveMessageAsync("u", "webchat", "user", "Hi", DateTimeOffset.UtcNow);
        await _store.SaveMessageAsync("u", "discord-dm", "user", "Hey", DateTimeOffset.UtcNow);

        var conversations = await _store.GetConversationsAsync(channelId: "webchat");

        Assert.Single(conversations);
        Assert.Equal("webchat", conversations[0].ChannelId);
    }

    // ── Delete ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteChannelMessages_RemovesOnlyThatChannel()
    {
        await _store.SaveMessageAsync("u", "ch-a", "user", "Keep", DateTimeOffset.UtcNow);
        await _store.SaveMessageAsync("u", "ch-b", "user", "Delete", DateTimeOffset.UtcNow);

        await _store.DeleteChannelMessagesAsync("ch-b");

        var a = await _store.GetMessagesAsync("ch-a");
        var b = await _store.GetMessagesAsync("ch-b");
        Assert.Single(a);
        Assert.Empty(b);
    }

    [Fact]
    public async Task DeleteAllMessages_RemovesEverything()
    {
        await _store.SaveMessageAsync("u", "ch-a", "user", "A", DateTimeOffset.UtcNow);
        await _store.SaveMessageAsync("u", "ch-b", "user", "B", DateTimeOffset.UtcNow);

        var deleted = await _store.DeleteAllMessagesAsync();

        Assert.Equal(2, deleted);
        Assert.Equal(0, await _store.GetTotalMessageCountAsync());
    }

    [Fact]
    public async Task DeleteMessagesOlderThan_RemovesOnlyOldMessages()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.SaveMessageAsync("u", "ch-a", "user", "Old A", now.AddDays(-5));
        await _store.SaveMessageAsync("u", "ch-a", "user", "Recent A", now.AddHours(-1));
        await _store.SaveMessageAsync("u", "ch-b", "user", "Old B", now.AddDays(-3));
        await _store.SaveMessageAsync("u", "ch-b", "user", "Recent B", now.AddMinutes(-30));

        var deleted = await _store.DeleteMessagesOlderThanAsync(now.AddDays(-2));

        Assert.Equal(2, deleted); // Old A (-5d) and Old B (-3d)
        Assert.Equal(2, await _store.GetTotalMessageCountAsync()); // Recent A and Recent B remain
    }

    [Fact]
    public async Task DeleteChannelMessagesOlderThan_RemovesOnlyOldMessagesForChannel()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.SaveMessageAsync("u", "ch-a", "user", "Old A", now.AddDays(-5));
        await _store.SaveMessageAsync("u", "ch-a", "user", "Recent A", now.AddHours(-1));
        await _store.SaveMessageAsync("u", "ch-b", "user", "Old B", now.AddDays(-5));

        var deleted = await _store.DeleteChannelMessagesOlderThanAsync("ch-a", now.AddDays(-2));

        Assert.Equal(1, deleted); // Only Old A
        Assert.Equal(2, await _store.GetTotalMessageCountAsync()); // Recent A + Old B remain
    }

    // ── Active channels ────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveChannels_ReturnsDistinctChannels()
    {
        await _store.SaveMessageAsync("u", "webchat", "user", "Hi", DateTimeOffset.UtcNow);
        await _store.SaveMessageAsync("u", "webchat", "assistant", "Hello", DateTimeOffset.UtcNow);
        await _store.SaveMessageAsync("u", "discord-dm", "user", "Hey", DateTimeOffset.UtcNow);

        var channels = await _store.GetActiveChannelsAsync();

        Assert.Equal(2, channels.Count);
        Assert.Contains("webchat", channels);
        Assert.Contains("discord-dm", channels);
    }

    // ── Channel summaries ──────────────────────────────────────────────

    [Fact]
    public async Task GetChannelSummaries_GroupsByChannel_WithCountAndLastActivity()
    {
        var t1 = new DateTimeOffset(2026, 4, 15, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 4, 16, 11, 30, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 4, 17, 9, 45, 0, TimeSpan.Zero);
        var t4 = new DateTimeOffset(2026, 4, 17, 12, 0, 0, TimeSpan.Zero);

        await _store.SaveMessageAsync("u", "webchat-default", "user", "Hi", t1);
        await _store.SaveMessageAsync("u", "webchat-default", "assistant", "Hello", t2);
        await _store.SaveMessageAsync("u", "webchat-default", "user", "More", t4);
        await _store.SaveMessageAsync("u", "discord-dm", "user", "Hey", t3);

        var summaries = await _store.GetChannelSummariesAsync();

        Assert.Equal(2, summaries.Count);

        var webchat = summaries.Single(s => s.ChannelId == "webchat-default");
        Assert.Equal(3, webchat.MessageCount);
        Assert.Equal(t4, webchat.LastActivity);

        var discord = summaries.Single(s => s.ChannelId == "discord-dm");
        Assert.Equal(1, discord.MessageCount);
        Assert.Equal(t3, discord.LastActivity);
    }

    [Fact]
    public async Task GetChannelSummaries_EmptyStore_ReturnsEmptyList()
    {
        var summaries = await _store.GetChannelSummariesAsync();

        Assert.Empty(summaries);
    }

    [Fact]
    public async Task GetChannelSummaries_HyphenatedChannelIds_ReturnedAsIs()
    {
        await _store.SaveMessageAsync("u", "discord-voice-default", "user", "voice message", DateTimeOffset.UtcNow);

        var summaries = await _store.GetChannelSummariesAsync();

        Assert.Single(summaries);
        Assert.Equal("discord-voice-default", summaries[0].ChannelId);
        Assert.Equal(1, summaries[0].MessageCount);
    }

    // ── Message count ──────────────────────────────────────────────────

    [Fact]
    public async Task GetMessageCount_ReturnsCorrectCount()
    {
        await _store.SaveMessageAsync("u", "ch", "user", "1", DateTimeOffset.UtcNow);
        await _store.SaveMessageAsync("u", "ch", "user", "2", DateTimeOffset.UtcNow);
        await _store.SaveMessageAsync("u", "other", "user", "3", DateTimeOffset.UtcNow);

        Assert.Equal(2, await _store.GetMessageCountAsync("ch"));
        Assert.Equal(1, await _store.GetMessageCountAsync("other"));
        Assert.Equal(3, await _store.GetTotalMessageCountAsync());
    }

    // ── Persistence across instances ───────────────────────────────────

    [Fact]
    public async Task Data_SurvivesNewInstance()
    {
        await _store.SaveMessageAsync("u", "ch", "user", "Persistent", DateTimeOffset.UtcNow);
        await _store.DisposeAsync();

        await using var store2 = new MessageStore(
            Path.Combine(_tempDir, "messages.db"),
            NullLogger<MessageStore>.Instance);

        var messages = await store2.GetMessagesAsync("ch");
        Assert.Single(messages);
        Assert.Equal("Persistent", messages[0].Content);
    }

    // ── MessageId ──────────────────────────────────────────────────────

    [Fact]
    public async Task MessageId_SavedAndRetrieved()
    {
        var msgId = Guid.NewGuid().ToString("N");
        await _store.SaveMessageAsync("u", "ch", "assistant", "Response", DateTimeOffset.UtcNow, messageId: msgId);

        var messages = await _store.GetMessagesAsync("ch");
        Assert.Single(messages);
        Assert.Equal(msgId, messages[0].MessageId);
    }

    [Fact]
    public async Task MessageId_NullWhenNotProvided()
    {
        await _store.SaveMessageAsync("u", "ch", "user", "No ID", DateTimeOffset.UtcNow);

        var messages = await _store.GetMessagesAsync("ch");
        Assert.Null(messages[0].MessageId);
    }

    // ── ToolCalls column ────────────────────────────────────────────────

    [Fact]
    public async Task SaveMessage_PersistsToolCallsJson()
    {
        const string json = """[{"name":"memory_search","args":"\"x\"","ok":true,"pos":"after"}]""";

        await _store.SaveMessageAsync(
            userId: "assistant",
            channelId: "ch",
            role: "assistant",
            content: "hello",
            timestamp: DateTimeOffset.UtcNow,
            toolCalls: json);

        var messages = await _store.GetMessagesAsync("ch");

        Assert.Single(messages);
        Assert.Equal(json, messages[0].ToolCalls);
    }

    [Fact]
    public async Task SaveMessage_NoToolCalls_RoundTripsAsNull()
    {
        await _store.SaveMessageAsync("u", "ch", "user", "hi", DateTimeOffset.UtcNow);

        var messages = await _store.GetMessagesAsync("ch");

        Assert.Null(messages[0].ToolCalls);
    }

    [Fact]
    public async Task UpdateToolCalls_PatchesExistingRow()
    {
        var id = await _store.SaveMessageAsync(
            userId: "assistant",
            channelId: "ch",
            role: "assistant",
            content: "hi",
            timestamp: DateTimeOffset.UtcNow);

        const string json = """[{"name":"file_read","args":"\"a.md\"","ok":true,"pos":"after"}]""";
        await _store.UpdateToolCallsAsync(id, json);

        var messages = await _store.GetMessagesAsync("ch");

        Assert.Single(messages);
        Assert.Equal(json, messages[0].ToolCalls);
    }

    [Fact]
    public async Task UpdateToolCalls_NullClearsExistingValue()
    {
        var id = await _store.SaveMessageAsync(
            "assistant", "ch", "assistant", "hi", DateTimeOffset.UtcNow,
            toolCalls: """[{"name":"x","args":"","ok":true,"pos":"after"}]""");

        await _store.UpdateToolCallsAsync(id, null);

        var messages = await _store.GetMessagesAsync("ch");
        Assert.Null(messages[0].ToolCalls);
    }

    // ── UpdateContentAsync (barge-in truncation) ──────────────────────

    [Fact]
    public async Task UpdateContent_RoundTrips()
    {
        var id = await _store.SaveMessageAsync(
            "assistant", "discord", "assistant",
            "Full story that was generated.", DateTimeOffset.UtcNow);

        await _store.UpdateContentAsync(id, "Truncated…", CancellationToken.None);

        var messages = await _store.GetMessagesAsync("discord");
        Assert.Single(messages);
        Assert.Equal("Truncated…", messages[0].Content);
    }

    [Fact]
    public async Task UpdateContent_UnknownId_IsNoOp()
    {
        await _store.SaveMessageAsync(
            "assistant", "discord", "assistant", "Kept.", DateTimeOffset.UtcNow);

        await _store.UpdateContentAsync(999999, "should not apply", CancellationToken.None);

        var messages = await _store.GetMessagesAsync("discord");
        Assert.Single(messages);
        Assert.Equal("Kept.", messages[0].Content);
    }
}
