using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests;

public sealed class TurnTimeStamperTests
{
    private const string BerlinNoon = "[Current time: Sunday 22 November 2026, 16:45 (Europe/Berlin, UTC+01:00)]";

    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 22, 15, 45, 0, TimeSpan.Zero));

    private OwnerClock NewClock(bool enabled = true) =>
        new(this.time, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"), enabled);

    private static List<LlmMessage> Conversation() =>
    [
        new() { Role = "system", Content = "sys" },
        new() { Role = "user", Content = "first question" },
        new() { Role = "assistant", Content = "first answer" },
        new() { Role = "user", Content = "second question" },
    ];

    [Fact]
    public void Apply_StampsTheFinalUserMessageOnly()
    {
        var prepared = Conversation();

        new TurnTimeStamper(this.NewClock()).Apply(prepared);

        Assert.Equal("sys", prepared[0].Content);
        Assert.Equal("first question", prepared[1].Content);
        Assert.Equal("first answer", prepared[2].Content);
        Assert.Equal(BerlinNoon + "\nsecond question", prepared[3].Content);
    }

    [Fact]
    public void Apply_DoesNotMutateTheOriginalMessageInstances()
    {
        var prepared = Conversation();
        var original = prepared[3];

        new TurnTimeStamper(this.NewClock()).Apply(prepared);

        Assert.Equal("second question", original.Content);
        Assert.NotSame(original, prepared[3]);
    }

    [Fact]
    public void Apply_KeepsMessageTypeAndTimestamp()
    {
        var sent = new DateTimeOffset(2026, 11, 22, 15, 44, 0, TimeSpan.Zero);
        var prepared = new List<LlmMessage>
        {
            new() { Role = "user", Content = "go", MessageType = LlmMessageType.ScheduledTaskInstruction, Timestamp = sent },
        };

        new TurnTimeStamper(this.NewClock()).Apply(prepared);

        Assert.Equal(LlmMessageType.ScheduledTaskInstruction, prepared[0].MessageType);
        Assert.Equal(sent, prepared[0].Timestamp);
    }

    [Fact]
    public void Apply_ToolRoundsAfterTheUserMessage_StillStampsTheUserMessage()
    {
        var prepared = new List<LlmMessage>
        {
            new() { Role = "system", Content = "sys" },
            new() { Role = "user", Content = "check it" },
            new() { Role = "assistant", Content = null, ToolCalls = [new LlmToolCall { Id = "c1", Name = "t", Arguments = "{}" }] },
            new() { Role = "tool", Content = "result", ToolCallId = "c1" },
        };

        new TurnTimeStamper(this.NewClock()).Apply(prepared);

        Assert.StartsWith(BerlinNoon, prepared[1].Content, StringComparison.Ordinal);
        Assert.Equal("result", prepared[3].Content);
    }

    [Fact]
    public void Apply_MultimodalMessage_PutsTheStampInAFirstTextBlock()
    {
        var prepared = new List<LlmMessage>
        {
            new()
            {
                Role = "user",
                Content = "look",
                ContentBlocks = [LlmContentBlock.ImageBlock("AAAA", "image/png"), LlmContentBlock.TextBlock("look")],
            },
        };

        new TurnTimeStamper(this.NewClock()).Apply(prepared);

        var blocks = prepared[0].ContentBlocks!;
        Assert.Equal(3, blocks.Count);
        Assert.Equal("text", blocks[0].Type);
        Assert.Equal(BerlinNoon, blocks[0].Text);
        Assert.Equal("image", blocks[1].Type);
        Assert.Equal("look", blocks[2].Text);
    }

    [Fact]
    public void Apply_UserMessageWithoutContent_GetsTheStampAlone()
    {
        var prepared = new List<LlmMessage> { new() { Role = "user" } };

        new TurnTimeStamper(this.NewClock()).Apply(prepared);

        Assert.Equal(BerlinNoon, prepared[0].Content);
    }

    [Fact]
    public void Apply_NoUserMessage_ChangesNothing()
    {
        var prepared = new List<LlmMessage>
        {
            new() { Role = "system", Content = "sys" },
            new() { Role = "assistant", Content = "hello" },
        };

        new TurnTimeStamper(this.NewClock()).Apply(prepared);

        Assert.Equal("sys", prepared[0].Content);
        Assert.Equal("hello", prepared[1].Content);
    }

    [Fact]
    public void Apply_SameMessageAcrossToolRounds_KeepsTheStampStableWhileTheClockAdvances()
    {
        var stamper = new TurnTimeStamper(this.NewClock());
        var history = Conversation();

        var round1 = new List<LlmMessage>(history);
        stamper.Apply(round1);
        this.time.Advance(TimeSpan.FromMinutes(3));
        var round2 = new List<LlmMessage>(history);
        stamper.Apply(round2);

        Assert.Equal(round1[3].Content, round2[3].Content);
    }

    [Fact]
    public void Apply_NewUserMessage_GetsAFreshStamp()
    {
        var stamper = new TurnTimeStamper(this.NewClock());
        var history = Conversation();
        stamper.Apply(new List<LlmMessage>(history));

        this.time.Advance(TimeSpan.FromMinutes(3));
        history.Add(new LlmMessage { Role = "assistant", Content = "second answer" });
        history.Add(new LlmMessage { Role = "user", Content = "third question" });
        var next = new List<LlmMessage>(history);
        stamper.Apply(next);

        Assert.Equal("[Current time: Sunday 22 November 2026, 16:48 (Europe/Berlin, UTC+01:00)]\nthird question", next[^1].Content);
        Assert.Equal("second question", next[3].Content);
    }

    [Fact]
    public void Apply_Disabled_ChangesNothing()
    {
        var prepared = Conversation();

        new TurnTimeStamper(this.NewClock(enabled: false)).Apply(prepared);

        Assert.Equal("second question", prepared[3].Content);
    }

    [Fact]
    public void Apply_NoClock_ChangesNothing()
    {
        var prepared = Conversation();

        new TurnTimeStamper(null).Apply(prepared);

        Assert.Equal("second question", prepared[3].Content);
    }
}
