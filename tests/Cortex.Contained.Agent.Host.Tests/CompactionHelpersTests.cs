using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

public class CompactionHelpersTests
{
    // ── SplitAtLastToolRound ────────────────────────────────────────────────

    [Fact]
    public void SplitAtLastToolRound_EmptyInput_ReturnsEmpty()
    {
        var (toSummarize, preservedTail) = AgentRuntime.SplitAtLastToolRound([]);

        Assert.Empty(toSummarize);
        Assert.Empty(preservedTail);
    }

    [Fact]
    public void SplitAtLastToolRound_EndsWithAssistantText_PreservesNothing()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "hello" },
            new() { Role = "assistant", Content = "hi there" },
            new() { Role = "user", Content = "what's up?" },
            new() { Role = "assistant", Content = "not much" },
        };

        var (toSummarize, preservedTail) = AgentRuntime.SplitAtLastToolRound(messages);

        Assert.Equal(4, toSummarize.Count);
        Assert.Empty(preservedTail);
    }

    [Fact]
    public void SplitAtLastToolRound_SingleToolAfterAssistantWithToolCalls_PreservesPair()
    {
        var assistantWithCall = new LlmMessage
        {
            Role = "assistant",
            Content = null,
            ToolCalls =
            [
                new LlmToolCall { Id = "call_1", Name = "search", Arguments = "{}" },
            ],
        };

        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "first" },
            new() { Role = "assistant", Content = "ok" },
            new() { Role = "user", Content = "run a search" },
            assistantWithCall,
            new() { Role = "tool", ToolCallId = "call_1", Content = "result" },
        };

        var (toSummarize, preservedTail) = AgentRuntime.SplitAtLastToolRound(messages);

        Assert.Equal(3, toSummarize.Count);
        Assert.Equal("user", toSummarize[0].Role);
        Assert.Equal("assistant", toSummarize[1].Role);
        Assert.Equal("user", toSummarize[2].Role);

        Assert.Equal(2, preservedTail.Count);
        Assert.Equal("assistant", preservedTail[0].Role);
        Assert.NotNull(preservedTail[0].ToolCalls);
        Assert.Equal("tool", preservedTail[1].Role);
        Assert.Equal("call_1", preservedTail[1].ToolCallId);
    }

    [Fact]
    public void SplitAtLastToolRound_MultipleParallelTools_PreservesAll()
    {
        var assistantWithCalls = new LlmMessage
        {
            Role = "assistant",
            Content = null,
            ToolCalls =
            [
                new LlmToolCall { Id = "call_1", Name = "search", Arguments = "{}" },
                new LlmToolCall { Id = "call_2", Name = "fetch", Arguments = "{}" },
                new LlmToolCall { Id = "call_3", Name = "read", Arguments = "{}" },
            ],
        };

        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "first" },
            new() { Role = "assistant", Content = "ok" },
            new() { Role = "user", Content = "run parallel" },
            assistantWithCalls,
            new() { Role = "tool", ToolCallId = "call_1", Content = "r1" },
            new() { Role = "tool", ToolCallId = "call_2", Content = "r2" },
            new() { Role = "tool", ToolCallId = "call_3", Content = "r3" },
        };

        var (toSummarize, preservedTail) = AgentRuntime.SplitAtLastToolRound(messages);

        Assert.Equal(3, toSummarize.Count);
        Assert.Equal(4, preservedTail.Count);
        Assert.Equal("assistant", preservedTail[0].Role);
        Assert.Equal("tool", preservedTail[1].Role);
        Assert.Equal("tool", preservedTail[2].Role);
        Assert.Equal("tool", preservedTail[3].Role);
    }

    [Fact]
    public void SplitAtLastToolRound_ToolWithoutPrecedingAssistantToolCalls_PreservesNothing()
    {
        // Malformed: tool message preceded by a user (or assistant without tool_calls)
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "first" },
            new() { Role = "assistant", Content = "response" },
            new() { Role = "user", Content = "another" },
            new() { Role = "tool", ToolCallId = "call_x", Content = "orphan" },
        };

        var (toSummarize, preservedTail) = AgentRuntime.SplitAtLastToolRound(messages);

        Assert.Equal(4, toSummarize.Count);
        Assert.Empty(preservedTail);
    }

    [Fact]
    public void SplitAtLastToolRound_TooFewMessagesBeforeAssistant_PreservesNothing()
    {
        // assistantIdx < 2 guard: not enough content to bother summarizing
        var assistantWithCall = new LlmMessage
        {
            Role = "assistant",
            Content = null,
            ToolCalls =
            [
                new LlmToolCall { Id = "call_1", Name = "search", Arguments = "{}" },
            ],
        };

        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "run a search" },
            assistantWithCall,
            new() { Role = "tool", ToolCallId = "call_1", Content = "result" },
        };

        var (toSummarize, preservedTail) = AgentRuntime.SplitAtLastToolRound(messages);

        Assert.Equal(3, toSummarize.Count);
        Assert.Empty(preservedTail);
    }

    // ── WrapSummaryForContinuation ──────────────────────────────────────────

    [Fact]
    public void WrapSummaryForContinuation_WithTail_WrapsForToolContinuation()
    {
        const string summary = "## Goal\nDo the thing.\n## Completed actions\nSent email to alice.";

        var wrapped = AgentRuntime.WrapSummaryForContinuation(summary, hasTail: true);

        Assert.Contains(summary, wrapped, StringComparison.Ordinal);
        Assert.Contains("continued from a previous conversation", wrapped, StringComparison.Ordinal);
        Assert.Contains("tool call and its results follow", wrapped, StringComparison.Ordinal);
        Assert.Contains("Do not repeat", wrapped, StringComparison.Ordinal);
    }

    [Fact]
    public void WrapSummaryForContinuation_NoTail_WrapsForUserContinuation()
    {
        const string summary = "## Goal\nDo the thing.\n## Completed actions\nSent email to alice.";

        var wrapped = AgentRuntime.WrapSummaryForContinuation(summary, hasTail: false);

        Assert.Contains(summary, wrapped, StringComparison.Ordinal);
        Assert.Contains("continued from a previous conversation", wrapped, StringComparison.Ordinal);
        Assert.Contains("Resume directly", wrapped, StringComparison.Ordinal);
        Assert.Contains("do not repeat actions", wrapped, StringComparison.Ordinal);
        Assert.DoesNotContain("tool call and its results follow", wrapped, StringComparison.Ordinal);
    }

    // ── SplitPreservingRecentTurns ──────────────────────────────────────────

    private const int LargeBudget = 100_000;

    private static LlmMessage UserMsg(string text) => new() { Role = "user", Content = text };
    private static LlmMessage AsstMsg(string text) => new() { Role = "assistant", Content = text };
    private static LlmMessage ToolMsg(string callId, string content) => new() { Role = "tool", ToolCallId = callId, Content = content };

    [Fact]
    public void SplitPreservingRecentTurns_ZeroPreserve_FallsBackToToolRoundLogic()
    {
        var messages = new List<LlmMessage>
        {
            UserMsg("a"), AsstMsg("b"), UserMsg("c"), AsstMsg("d"),
        };

        var (toSummarize, tail) = AgentRuntime.SplitPreservingRecentTurns(messages, preserveTurns: 0, budgetTokens: LargeBudget);

        // preserveTurns=0 → identical to SplitAtLastToolRound (which preserves nothing here).
        Assert.Equal(4, toSummarize.Count);
        Assert.Empty(tail);
    }

    [Fact]
    public void SplitPreservingRecentTurns_TailFitsBudget_PreservesLastNUserTurns()
    {
        // 3 user turns. Preserve 2 → tail starts at the 2nd-from-last user turn.
        var messages = new List<LlmMessage>
        {
            UserMsg("turn 1"),
            AsstMsg("reply 1"),
            UserMsg("turn 2"),
            AsstMsg("reply 2"),
            UserMsg("turn 3"),
        };

        var (toSummarize, tail) = AgentRuntime.SplitPreservingRecentTurns(messages, preserveTurns: 2, budgetTokens: LargeBudget);

        // toSummarize = [user "turn 1", assistant "reply 1"] (indices 0..1)
        // tail = [user "turn 2", assistant "reply 2", user "turn 3"] (indices 2..4)
        Assert.Equal(2, toSummarize.Count);
        Assert.Equal("turn 1", toSummarize[0].Content);
        Assert.Equal(3, tail.Count);
        Assert.Equal("turn 2", tail[0].Content);
        Assert.Equal("turn 3", tail[2].Content);
    }

    [Fact]
    public void SplitPreservingRecentTurns_TailExceedsBudget_FallsBack()
    {
        // Build a tail much larger than a 10-token budget — should fall back to
        // SplitAtLastToolRound (which preserves nothing for an all-text history).
        var bigContent = new string('x', 4000);
        var messages = new List<LlmMessage>
        {
            UserMsg("turn 1"),
            AsstMsg("short reply"),
            UserMsg(bigContent),
            AsstMsg(bigContent),
            UserMsg(bigContent),
        };

        var (toSummarize, tail) = AgentRuntime.SplitPreservingRecentTurns(messages, preserveTurns: 2, budgetTokens: 10);

        Assert.Equal(5, toSummarize.Count);
        Assert.Empty(tail);
    }

    [Fact]
    public void SplitPreservingRecentTurns_FewerUserTurnsThanRequested_FallsBack()
    {
        // Only 1 user turn but caller asks to preserve 4 → can't anchor 4 from end,
        // anchor would land at index 0 (or earlier), so fall back.
        var messages = new List<LlmMessage>
        {
            UserMsg("only one"),
            AsstMsg("reply"),
        };

        var (toSummarize, tail) = AgentRuntime.SplitPreservingRecentTurns(messages, preserveTurns: 4, budgetTokens: LargeBudget);

        Assert.Equal(2, toSummarize.Count);
        Assert.Empty(tail);
    }

    [Fact]
    public void SplitPreservingRecentTurns_PreservesToolMessagesBetweenUserTurns()
    {
        // Two user turns separated by a full tool round. Preserve 2 → entire
        // history (from first user turn onward) is the tail; nothing to summarize.
        // That falls back per the "anchor must be > 0" guard, so we expect fallback.
        var assistantWithCall = new LlmMessage
        {
            Role = "assistant",
            ToolCalls = [new LlmToolCall { Id = "c1", Name = "search", Arguments = "{}" }],
        };
        var messages = new List<LlmMessage>
        {
            UserMsg("first"),
            assistantWithCall,
            ToolMsg("c1", "result"),
            AsstMsg("done"),
            UserMsg("second"),
        };

        var (toSummarize, tail) = AgentRuntime.SplitPreservingRecentTurns(messages, preserveTurns: 2, budgetTokens: LargeBudget);

        // anchor would be 0 → fall back to SplitAtLastToolRound, which preserves
        // nothing here (history ends with user message).
        Assert.Equal(5, toSummarize.Count);
        Assert.Empty(tail);
    }

    [Fact]
    public void SplitPreservingRecentTurns_OneRecentUserTurnFromManyOlder_PreservesOneTurn()
    {
        // 4 user turns. Preserve 1 → only the very last user turn (and anything
        // after it) is preserved.
        var messages = new List<LlmMessage>
        {
            UserMsg("u1"), AsstMsg("a1"),
            UserMsg("u2"), AsstMsg("a2"),
            UserMsg("u3"), AsstMsg("a3"),
            UserMsg("u4"),
        };

        var (toSummarize, tail) = AgentRuntime.SplitPreservingRecentTurns(messages, preserveTurns: 1, budgetTokens: LargeBudget);

        Assert.Equal(6, toSummarize.Count);
        Assert.Single(tail);
        Assert.Equal("u4", tail[0].Content);
    }

    // ── StripAloneSufficient ────────────────────────────────────────────────

    [Fact]
    public void StripAloneSufficient_BelowThreshold_ReturnsTrue()
    {
        // 65% threshold of 128k context = 83,200 — anything under that is "fits".
        Assert.True(AgentRuntime.StripAloneSufficient(strippedTokens: 1_000, contextWindow: 128_000));
        Assert.True(AgentRuntime.StripAloneSufficient(strippedTokens: 50_000, contextWindow: 128_000));
    }

    [Fact]
    public void StripAloneSufficient_AboveThreshold_ReturnsFalse()
    {
        // Above 65% = 83,200 → still need summarization.
        Assert.False(AgentRuntime.StripAloneSufficient(strippedTokens: 90_000, contextWindow: 128_000));
        Assert.False(AgentRuntime.StripAloneSufficient(strippedTokens: 200_000, contextWindow: 128_000));
    }

    [Fact]
    public void StripAloneSufficient_AtThreshold_ReturnsFalse()
    {
        // Strict less-than: at the threshold value we still summarize.
        var contextWindow = 100_000;
        var threshold = (int)(contextWindow * 0.65);
        Assert.False(AgentRuntime.StripAloneSufficient(strippedTokens: threshold, contextWindow: contextWindow));
    }
}
