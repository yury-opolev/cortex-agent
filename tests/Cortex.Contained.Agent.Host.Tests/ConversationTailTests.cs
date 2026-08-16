using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Covers selecting the bounded slice of conversation a focused composer run is given.
/// <para>
/// A timer intent is answered by a separate LLM call that does NOT see the whole chat — only
/// enough recent context to know what is going on. "Enough" is counted in TURNS, because that is
/// what a person means by "the last few exchanges"; tool traffic is part of the turn that issued
/// it, not an exchange of its own.
/// </para>
/// </summary>
public sealed class ConversationTailTests
{
    private static LlmMessage User(string text) => new() { Role = "user", Content = text };

    private static LlmMessage Assistant(string text) => new() { Role = "assistant", Content = text };

    private static LlmMessage AssistantWithToolCall(string text, string callId) => new()
    {
        Role = "assistant",
        Content = text,
        ToolCalls = [new LlmToolCall { Id = callId, Name = "file_read", Arguments = "{}" }],
    };

    private static LlmMessage ToolResult(string callId, string text) => new()
    {
        Role = "tool",
        Content = text,
        ToolCallId = callId,
    };

    [Fact]
    public void An_empty_history_yields_an_empty_tail()
    {
        Assert.Empty(ConversationTail.SelectLast([], turns: 16));
    }

    [Fact]
    public void A_history_shorter_than_the_limit_is_returned_whole()
    {
        List<LlmMessage> history = [User("hi"), Assistant("hello")];

        var tail = ConversationTail.SelectLast(history, turns: 16);

        Assert.Equal(2, tail.Count);
        Assert.Equal("hi", tail[0].Content);
        Assert.Equal("hello", tail[1].Content);
    }

    [Fact]
    public void Only_the_most_recent_turns_are_kept()
    {
        // Recency is the point: the composer needs to know what is happening NOW.
        List<LlmMessage> history = [];
        for (var i = 0; i < 10; i++)
        {
            history.Add(User($"u{i}"));
            history.Add(Assistant($"a{i}"));
        }

        var tail = ConversationTail.SelectLast(history, turns: 4);

        Assert.Equal(4, tail.Count);
        Assert.Equal("u8", tail[0].Content);
        Assert.Equal("a9", tail[3].Content);
    }

    [Fact]
    public void Both_user_and_agent_messages_count_towards_the_limit()
    {
        List<LlmMessage> history = [User("u0"), Assistant("a0"), User("u1"), Assistant("a1")];

        var tail = ConversationTail.SelectLast(history, turns: 3);

        Assert.Equal(3, tail.Count);
        Assert.Equal("a0", tail[0].Content);
    }

    [Fact]
    public void Tool_traffic_rides_along_with_its_turn_rather_than_counting_as_one()
    {
        // Counting tool results as turns would let a single tool-heavy exchange crowd out every
        // actual exchange, which is the opposite of what a turn budget is for.
        List<LlmMessage> history =
        [
            User("u0"),
            AssistantWithToolCall("looking", "c1"),
            ToolResult("c1", "file contents"),
            Assistant("a0"),
            User("u1"),
            Assistant("a1"),
        ];

        var tail = ConversationTail.SelectLast(history, turns: 4);

        Assert.Equal(5, tail.Count);
        Assert.Equal("looking", tail[0].Content);
        Assert.Equal("tool", tail[1].Role);
        Assert.Equal("a1", tail[4].Content);
    }

    [Fact]
    public void A_tool_result_is_never_left_without_the_call_that_produced_it()
    {
        // Providers reject a tool_result with no matching tool_use, so a cut that lands mid-group
        // has to take the whole group or none of it.
        List<LlmMessage> history =
        [
            User("u0"),
            AssistantWithToolCall("looking", "c1"),
            ToolResult("c1", "file contents"),
            Assistant("a0"),
        ];

        var tail = ConversationTail.SelectLast(history, turns: 2);

        Assert.DoesNotContain(tail, m => m.Role == "tool" && !tail.Any(
            c => c.ToolCalls?.Any(tc => tc.Id == m.ToolCallId) == true));
    }

    [Fact]
    public void System_messages_are_left_out_because_the_composer_builds_its_own()
    {
        List<LlmMessage> history =
        [
            new() { Role = "system", Content = "old system prompt" },
            User("u0"),
            Assistant("a0"),
        ];

        var tail = ConversationTail.SelectLast(history, turns: 16);

        Assert.DoesNotContain(tail, m => m.Role == "system");
        Assert.Equal(2, tail.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_limit_yields_nothing_rather_than_everything(int turns)
    {
        List<LlmMessage> history = [User("u0"), Assistant("a0")];

        Assert.Empty(ConversationTail.SelectLast(history, turns));
    }

    [Fact]
    public void A_short_history_opening_on_a_tool_result_does_not_carry_the_orphan()
    {
        // When the history holds fewer turns than the budget the walk never spends it, so the
        // slice starts at zero — which is exactly where a leftover tool result can sit after an
        // earlier trim.
        List<LlmMessage> history = [ToolResult("c1", "orphaned result"), Assistant("a0")];

        var tail = ConversationTail.SelectLast(history, turns: 16);

        Assert.Equal("a0", Assert.Single(tail).Content);
    }

    [Fact]
    public void A_history_of_nothing_but_tool_results_yields_nothing()
    {
        List<LlmMessage> history = [ToolResult("c1", "a"), ToolResult("c2", "b")];

        Assert.Empty(ConversationTail.SelectLast(history, turns: 16));
    }
}
