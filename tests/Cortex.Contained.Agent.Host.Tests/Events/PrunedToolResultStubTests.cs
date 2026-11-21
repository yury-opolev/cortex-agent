using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests.Events;

/// <summary>A pruned tool result keeps a pointer to its recorded evidence.</summary>
public sealed class PrunedToolResultStubTests
{
    private static List<LlmMessage> Conversation(string? evidenceId)
    {
        var bigResult = new string('x', 200_000);
        return
        [
            new() { Role = "user", Content = "Very old question" },
            new() { Role = "assistant", ToolCalls = [new LlmToolCall { Id = "c0", Name = "fn", Arguments = "{}" }] },
            new() { Role = "tool", Content = bigResult, ToolCallId = "c0", EvidenceId = evidenceId },
            new() { Role = "assistant", Content = "Very old answer" },
            new() { Role = "user", Content = "Old question" },
            new() { Role = "assistant", ToolCalls = [new LlmToolCall { Id = "c1", Name = "fn", Arguments = "{}" }] },
            new() { Role = "tool", Content = bigResult, ToolCallId = "c1" },
            new() { Role = "assistant", Content = "Old answer" },
            new() { Role = "user", Content = "New question" },
            new() { Role = "assistant", Content = "New answer" },
        ];
    }

    [Fact]
    public void PruneToolResults_WithEvidenceId_StubNamesTheEvidence()
    {
        var result = ContextManager.PruneToolResults(Conversation("ev_0123"));

        Assert.Contains("evidence ev_0123", result[2].Content, StringComparison.Ordinal);
        Assert.Contains("event_get", result[2].Content, StringComparison.Ordinal);
        Assert.Equal("ev_0123", result[2].EvidenceId);
    }

    [Fact]
    public void PruneToolResults_WithoutEvidenceId_KeepsPlainPlaceholder()
    {
        var result = ContextManager.PruneToolResults(Conversation(null));

        Assert.Equal(ContextManager.PrunedToolResultPlaceholder, result[2].Content);
    }
}
