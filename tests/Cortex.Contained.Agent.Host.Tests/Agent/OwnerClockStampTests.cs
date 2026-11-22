using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests;

public sealed class OwnerClockStampTests
{
    private const string At1645 = "[Sunday 22 November 2026, 16:45 (Europe/Berlin, UTC+01:00)]";
    private const string At1650 = "[Sunday 22 November 2026, 16:50 (Europe/Berlin, UTC+01:00)]";

    private static readonly DateTimeOffset T1645 = new(2026, 11, 22, 15, 45, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1650 = new(2026, 11, 22, 15, 50, 0, TimeSpan.Zero);

    private static OwnerClock NewClock(bool enabled = true) =>
        new(new FakeTimeProvider(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"), enabled);

    private static List<LlmMessage> Conversation() =>
    [
        new() { Role = "system", Content = "sys" },
        new() { Role = "user", Content = "first question", Timestamp = T1645 },
        new() { Role = "assistant", Content = "first answer", Timestamp = T1645 },
        new() { Role = "user", Content = "second question", Timestamp = T1650 },
    ];

    [Fact]
    public void StampUserMessages_StampsEveryUserMessageFromItsOwnTimestamp()
    {
        var prepared = Conversation();

        NewClock().StampUserMessages(prepared);

        Assert.Equal("sys", prepared[0].Content);
        Assert.Equal(At1645 + "\nfirst question", prepared[1].Content);
        Assert.Equal("first answer", prepared[2].Content);
        Assert.Equal(At1650 + "\nsecond question", prepared[3].Content);
    }

    [Fact]
    public void StampUserMessages_IsDeterministic_SoAMessageRendersIdenticallyOnEveryRequest()
    {
        var first = Conversation();
        var later = Conversation();
        var clock = NewClock();

        clock.StampUserMessages(first);
        clock.StampUserMessages(later);

        Assert.Equal(first.Select(m => m.Content), later.Select(m => m.Content));
    }

    [Fact]
    public void StampUserMessages_DoesNotMutateTheOriginalMessageInstances()
    {
        var prepared = Conversation();
        var original = prepared[3];

        NewClock().StampUserMessages(prepared);

        Assert.Equal("second question", original.Content);
        Assert.NotSame(original, prepared[3]);
    }

    [Fact]
    public void StampUserMessages_KeepsMessageTypeAndTimestamp()
    {
        var prepared = new List<LlmMessage>
        {
            new() { Role = "user", Content = "go", MessageType = LlmMessageType.ScheduledTaskInstruction, Timestamp = T1645 },
        };

        NewClock().StampUserMessages(prepared);

        Assert.StartsWith(At1645, prepared[0].Content, StringComparison.Ordinal);
        Assert.Equal(LlmMessageType.ScheduledTaskInstruction, prepared[0].MessageType);
        Assert.Equal(T1645, prepared[0].Timestamp);
    }

    [Fact]
    public void StampUserMessages_ToolRoundsAfterTheUserMessage_LeaveToolMessagesAlone()
    {
        var prepared = new List<LlmMessage>
        {
            new() { Role = "system", Content = "sys" },
            new() { Role = "user", Content = "check it", Timestamp = T1645 },
            new() { Role = "assistant", Content = null, ToolCalls = [new LlmToolCall { Id = "c1", Name = "t", Arguments = "{}" }] },
            new() { Role = "tool", Content = "result", ToolCallId = "c1" },
        };

        NewClock().StampUserMessages(prepared);

        Assert.StartsWith(At1645, prepared[1].Content, StringComparison.Ordinal);
        Assert.Null(prepared[2].Content);
        Assert.Equal("result", prepared[3].Content);
    }

    [Fact]
    public void StampUserMessages_FinalMessageThatIsNotAUserMessage_GetsNoExtraStamp()
    {
        var prepared = Conversation();
        prepared.Add(new LlmMessage { Role = "assistant", Content = "second answer", Timestamp = T1650 });

        NewClock().StampUserMessages(prepared);

        Assert.Equal("second answer", prepared[^1].Content);
    }

    [Fact]
    public void StampUserMessages_Multimodal_PutsTheStampInAFirstTextBlock()
    {
        var prepared = new List<LlmMessage>
        {
            new()
            {
                Role = "user",
                Content = "look",
                Timestamp = T1645,
                ContentBlocks = [LlmContentBlock.ImageBlock("AAAA", "image/png"), LlmContentBlock.TextBlock("look")],
            },
        };

        NewClock().StampUserMessages(prepared);

        var blocks = prepared[0].ContentBlocks!;
        Assert.Equal(3, blocks.Count);
        Assert.Equal("text", blocks[0].Type);
        Assert.Equal(At1645, blocks[0].Text);
        Assert.Equal("image", blocks[1].Type);
        Assert.Equal("look", blocks[2].Text);
    }

    [Fact]
    public void StampUserMessages_UserMessageWithoutContent_GetsTheStampAlone()
    {
        var prepared = new List<LlmMessage> { new() { Role = "user", Timestamp = T1645 } };

        NewClock().StampUserMessages(prepared);

        Assert.Equal(At1645, prepared[0].Content);
    }

    [Fact]
    public void StampUserMessages_MessageWithoutATimestamp_GetsNoStamp()
    {
        var prepared = new List<LlmMessage>
        {
            new() { Role = "user", Content = "seeded", Timestamp = default },
            new() { Role = "user", Content = "stamped", Timestamp = T1645 },
        };

        NewClock().StampUserMessages(prepared);

        Assert.Equal("seeded", prepared[0].Content);
        Assert.StartsWith(At1645, prepared[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void StampUserMessages_CompactionSummary_GetsNoStamp()
    {
        var prepared = new List<LlmMessage>
        {
            new() { Role = "user", Content = "summary of earlier chat", MessageType = LlmMessageType.CompactionSummary, Timestamp = T1645 },
        };

        NewClock().StampUserMessages(prepared);

        Assert.Equal("summary of earlier chat", prepared[0].Content);
    }

    [Fact]
    public void StampUserMessages_DaylightSaving_EachMessageUsesTheOffsetOfItsOwnInstant()
    {
        var prepared = new List<LlmMessage>
        {
            new() { Role = "user", Content = "before", Timestamp = new DateTimeOffset(2026, 3, 29, 0, 59, 0, TimeSpan.Zero) },
            new() { Role = "user", Content = "after", Timestamp = new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero) },
        };

        NewClock().StampUserMessages(prepared);

        Assert.Contains("UTC+01:00", prepared[0].Content, StringComparison.Ordinal);
        Assert.Contains("UTC+02:00", prepared[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void StampUserMessages_Disabled_ChangesNothing()
    {
        var prepared = Conversation();

        NewClock(enabled: false).StampUserMessages(prepared);

        Assert.Equal("first question", prepared[1].Content);
        Assert.Equal("second question", prepared[3].Content);
    }
}
