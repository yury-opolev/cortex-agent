using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class TokenEstimatorTests
{
    [Fact]
    public void EstimateTokens_SingleMessage_ReturnsReasonableEstimate()
    {
        var message = new LlmMessage { Role = "user", Content = "Hello, world!" };

        var tokens = TokenEstimator.EstimateTokens(message);

        // "user" (4 chars) + "Hello, world!" (13 chars) = 17 chars
        // 17 / 3.5 ≈ 4.86 + 4 overhead ≈ 8
        Assert.True(tokens > 0);
        Assert.True(tokens < 20);
    }

    [Fact]
    public void EstimateTokens_EmptyContent_ReturnsOverheadOnly()
    {
        var message = new LlmMessage { Role = "user", Content = null };

        var tokens = TokenEstimator.EstimateTokens(message);

        // Just role "user" (4 chars) / 3.5 + 4 overhead ≈ 5
        Assert.True(tokens >= 4); // At least the overhead
    }

    [Fact]
    public void EstimateTokens_WithToolCalls_IncludesArguments()
    {
        var message = new LlmMessage
        {
            Role = "assistant",
            Content = null,
            ToolCalls =
            [
                new LlmToolCall
                {
                    Id = "call_1",
                    Name = "file_read",
                    Arguments = "{\"path\": \"/app/data/test.txt\"}",
                },
            ],
        };

        var tokens = TokenEstimator.EstimateTokens(message);

        // Should include tool call name + arguments length
        var withoutToolCalls = TokenEstimator.EstimateTokens(new LlmMessage { Role = "assistant", Content = null });
        Assert.True(tokens > withoutToolCalls);
    }

    [Fact]
    public void EstimateTokens_MessageList_SumsCorrectly()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are a helpful assistant." },
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "Hi there!" },
        };

        var total = TokenEstimator.EstimateTokens(messages);
        var individual = messages.Sum(m => TokenEstimator.EstimateTokens(m));

        // Total should be sum of individual + 3 base overhead
        Assert.Equal(individual + 3, total);
    }

    [Fact]
    public void TrimToFit_AllFit_ReturnsAll()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "System prompt" },
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "Hi" },
        };

        var result = TokenEstimator.TrimToFit(messages, 100_000, 8_000);

        Assert.Equal(3, result.Count);
        Assert.Equal("system", result[0].Role);
    }

    [Fact]
    public void TrimToFit_RemovesOldestFirst()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "System prompt" },
            new() { Role = "user", Content = "Old message 1" },
            new() { Role = "assistant", Content = "Old response 1" },
            new() { Role = "user", Content = "Recent message" },
            new() { Role = "assistant", Content = "Recent response" },
        };

        // Use a very small budget so only system + last few messages fit
        var result = TokenEstimator.TrimToFit(messages, 50, 10);

        // Should keep system prompt and most recent messages
        Assert.Equal("system", result[0].Role);
        if (result.Count > 1)
        {
            // Last message should be the most recent one
            Assert.Equal("Recent response", result[^1].Content);
        }
    }

    [Fact]
    public void TrimToFit_AlwaysKeepsSystemPrompt()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "System prompt" },
            new() { Role = "user", Content = "Hello" },
        };

        // Budget so small only system prompt fits
        var result = TokenEstimator.TrimToFit(messages, 20, 5);

        Assert.True(result.Count >= 1);
        Assert.Equal("system", result[0].Role);
    }

    [Fact]
    public void TrimToFit_ZeroBudget_ReturnsEmpty()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "System prompt" },
            new() { Role = "user", Content = "Hello" },
        };

        var result = TokenEstimator.TrimToFit(messages, 0, 100);

        Assert.Empty(result);
    }

    [Fact]
    public void TrimToFit_NegativeBudget_ReturnsEmpty()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "System prompt" },
        };

        var result = TokenEstimator.TrimToFit(messages, 100, 200);

        Assert.Empty(result);
    }

    [Fact]
    public void TrimToFit_KeepsToolCallGroupsAtomic()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are helpful." },
            new() { Role = "user", Content = "Old question" },
            new() { Role = "assistant", Content = "Old answer" },
            new() { Role = "user", Content = "Search for X" },
            new()
            {
                Role = "assistant",
                Content = "Let me search",
                ToolCalls = [new LlmToolCall { Id = "call-1", Name = "search", Arguments = "{\"q\":\"X\"}" }],
            },
            new() { Role = "tool", Content = "Search result for X", ToolCallId = "call-1" },
            new() { Role = "assistant", Content = "Here is the answer" },
        };

        // Use a budget that can fit the system + last few messages but not all.
        // The tool group (assistant+tool = 2 messages) must stay together.
        var result = TokenEstimator.TrimToFit(messages, 200, 10);

        // Verify: no orphaned tool results
        for (var i = 0; i < result.Count; i++)
        {
            if (result[i].Role == "tool")
            {
                Assert.True(i > 0 && result[i - 1].Role == "assistant"
                                   && result[i - 1].ToolCalls is { Count: > 0 },
                    "Tool result without preceding assistant tool_use");
            }
        }
    }

    [Fact]
    public void GroupToolCalls_GroupsAssistantWithToolResults()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "Hello" },
            new()
            {
                Role = "assistant",
                ToolCalls = [new LlmToolCall { Id = "c1", Name = "fn", Arguments = "{}" },
                             new LlmToolCall { Id = "c2", Name = "fn2", Arguments = "{}" }],
            },
            new() { Role = "tool", Content = "r1", ToolCallId = "c1" },
            new() { Role = "tool", Content = "r2", ToolCallId = "c2" },
            new() { Role = "assistant", Content = "Done" },
        };

        var groups = TokenEstimator.GroupToolCalls(messages);

        Assert.Equal(3, groups.Count);
        // Group 0: standalone user message
        Assert.Single(groups[0]);
        Assert.Equal("user", groups[0][0].Role);
        // Group 1: assistant + 2 tool results
        Assert.Equal(3, groups[1].Count);
        Assert.Equal("assistant", groups[1][0].Role);
        Assert.Equal("tool", groups[1][1].Role);
        Assert.Equal("tool", groups[1][2].Role);
        // Group 2: standalone assistant
        Assert.Single(groups[2]);
        Assert.Equal("assistant", groups[2][0].Role);
    }

    [Fact]
    public void GroupToolCalls_NoToolCalls_EachMessageIsSeparateGroup()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "Hi" },
            new() { Role = "user", Content = "Bye" },
        };

        var groups = TokenEstimator.GroupToolCalls(messages);

        Assert.Equal(3, groups.Count);
        Assert.All(groups, g => Assert.Single(g));
    }
}
