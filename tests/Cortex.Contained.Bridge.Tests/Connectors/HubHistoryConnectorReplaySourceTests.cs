using Cortex.Contained.Bridge.Connectors.Replay;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Tests.Connectors;

public sealed class HubHistoryConnectorReplaySourceTests
{
    // HubClient is sealed and cannot be substituted with NSubstitute.
    // Route taken: the paging + filtering logic is extracted into the internal static method
    // HubHistoryConnectorReplaySource.SelectReplayMessages(...) and tested exhaustively here.

    private static MessageEntryDto MakeEntry(
        string messageId,
        string role,
        string content,
        DateTimeOffset timestamp,
        string? channelId = "chan") =>
        new()
        {
            MessageId = messageId,
            Role = role,
            Content = content,
            Timestamp = timestamp,
            ChannelId = channelId,
            Category = default,
        };

    private static readonly DateTimeOffset Floor = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // ── Role filter ───────────────────────────────────────────────────

    [Fact]
    public void SelectReplayMessages_UserRoleEntries_AreExcluded()
    {
        var entries = new List<MessageEntryDto>
        {
            MakeEntry("u1", "user", "user message", Floor.AddMinutes(1)),
            MakeEntry("a1", "assistant", "assistant reply", Floor.AddMinutes(2)),
        };

        var result = HubHistoryConnectorReplaySource.SelectReplayMessages(entries, Floor, 100);

        Assert.Single(result);
        Assert.Equal("a1", result[0].MessageId);
    }

    [Fact]
    public void SelectReplayMessages_RoleComparisonIsCaseInsensitive()
    {
        var entries = new List<MessageEntryDto>
        {
            MakeEntry("a1", "ASSISTANT", "caps role", Floor.AddMinutes(1)),
        };

        var result = HubHistoryConnectorReplaySource.SelectReplayMessages(entries, Floor, 100);

        Assert.Single(result);
    }

    // ── Whitespace content filter ─────────────────────────────────────

    [Fact]
    public void SelectReplayMessages_NullOrWhitespaceContent_Excluded()
    {
        var entries = new List<MessageEntryDto>
        {
            MakeEntry("a1", "assistant", "   ", Floor.AddMinutes(1)),
            MakeEntry("a2", "assistant", string.Empty, Floor.AddMinutes(2)),
            MakeEntry("a3", "assistant", "real content", Floor.AddMinutes(3)),
        };

        var result = HubHistoryConnectorReplaySource.SelectReplayMessages(entries, Floor, 100);

        Assert.Single(result);
        Assert.Equal("a3", result[0].MessageId);
    }

    // ── Floor strictness ─────────────────────────────────────────────

    [Fact]
    public void SelectReplayMessages_TimestampAtOrBeforeFloor_Excluded()
    {
        var entries = new List<MessageEntryDto>
        {
            MakeEntry("a1", "assistant", "at floor", Floor),         // NOT strictly newer
            MakeEntry("a2", "assistant", "before floor", Floor.AddSeconds(-1)),
            MakeEntry("a3", "assistant", "after floor", Floor.AddSeconds(1)),
        };

        var result = HubHistoryConnectorReplaySource.SelectReplayMessages(entries, Floor, 100);

        Assert.Single(result);
        Assert.Equal("a3", result[0].MessageId);
    }

    // ── Ordering: oldest first ────────────────────────────────────────

    [Fact]
    public void SelectReplayMessages_ResultIsOldestFirst()
    {
        var entries = new List<MessageEntryDto>
        {
            MakeEntry("a3", "assistant", "third", Floor.AddMinutes(3)),
            MakeEntry("a1", "assistant", "first", Floor.AddMinutes(1)),
            MakeEntry("a2", "assistant", "second", Floor.AddMinutes(2)),
        };

        var result = HubHistoryConnectorReplaySource.SelectReplayMessages(entries, Floor, 100);

        Assert.Equal(3, result.Count);
        Assert.Equal("a1", result[0].MessageId);
        Assert.Equal("a2", result[1].MessageId);
        Assert.Equal("a3", result[2].MessageId);
    }

    // ── MaxMessages: keeps newest N ───────────────────────────────────

    [Fact]
    public void SelectReplayMessages_MoreThanMaxMessages_KeepsNewestN()
    {
        var entries = Enumerable.Range(1, 10)
            .Select(i => MakeEntry($"a{i}", "assistant", $"msg {i}", Floor.AddMinutes(i)))
            .ToList();

        var result = HubHistoryConnectorReplaySource.SelectReplayMessages(entries, Floor, 3);

        Assert.Equal(3, result.Count);
        // Newest 3 are a8, a9, a10 — returned oldest-first within that window.
        Assert.Equal("a8", result[0].MessageId);
        Assert.Equal("a9", result[1].MessageId);
        Assert.Equal("a10", result[2].MessageId);
    }

    [Fact]
    public void SelectReplayMessages_ExactlyMaxMessages_ReturnsAll()
    {
        var entries = Enumerable.Range(1, 5)
            .Select(i => MakeEntry($"a{i}", "assistant", $"msg {i}", Floor.AddMinutes(i)))
            .ToList();

        var result = HubHistoryConnectorReplaySource.SelectReplayMessages(entries, Floor, 5);

        Assert.Equal(5, result.Count);
    }

    // ── Combined scenario ─────────────────────────────────────────────

    [Fact]
    public void SelectReplayMessages_MixedEntries_OnlyAssistantAfterFloor()
    {
        var entries = new List<MessageEntryDto>
        {
            MakeEntry("u1", "user", "user before floor", Floor.AddSeconds(-5)),
            MakeEntry("a1", "assistant", "assistant at floor", Floor),
            MakeEntry("u2", "user", "user after floor", Floor.AddSeconds(10)),
            MakeEntry("a2", "assistant", "   ", Floor.AddSeconds(20)),  // whitespace
            MakeEntry("a3", "assistant", "valid", Floor.AddSeconds(30)),
        };

        var result = HubHistoryConnectorReplaySource.SelectReplayMessages(entries, Floor, 100);

        Assert.Single(result);
        Assert.Equal("a3", result[0].MessageId);
    }

    [Fact]
    public void SelectReplayMessages_FloorInFuture_ReturnsEmpty()
    {
        // A connector that supplies a future-dated sinceCursor must be replayed nothing,
        // not everything, and must not cause an exception.
        var futureFloor = DateTimeOffset.UtcNow.AddYears(10);
        var entries = new List<MessageEntryDto>
        {
            MakeEntry("a1", "assistant", "recent", DateTimeOffset.UtcNow),
            MakeEntry("a2", "assistant", "older", DateTimeOffset.UtcNow.AddHours(-1)),
        };

        var result = HubHistoryConnectorReplaySource.SelectReplayMessages(entries, futureFloor, 100);

        Assert.Empty(result);
    }
}
