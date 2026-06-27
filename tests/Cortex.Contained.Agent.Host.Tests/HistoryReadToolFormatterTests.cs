using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Verifies <see cref="HistoryReadTool"/>'s private formatter renders persisted
/// tool-call summaries beneath the originating Consultant turn and preserves
/// Visitor/Consultant index numbering whether or not tool blocks are present.
/// </summary>
public class HistoryReadToolFormatterTests
{
    [Fact]
    public void FormatChannelText_AssistantWithToolCalls_AppendsBlockUnderConsultantTurn()
    {
        var records = new List<MessageRecord>
        {
            BuildRecord(role: "user", content: "find my notes", toolCalls: null),
            BuildRecord(
                role: "assistant",
                content: "Let me check.",
                toolCalls: """[{"name":"memory_search","args":"\"notes\"","ok":true,"pos":"after"}]"""),
            BuildRecord(role: "user", content: "thanks", toolCalls: null),
        };

        var formatted = HistoryReadTool.FormatChannelTextForTesting(records);

        Assert.Contains("[0] Visitor: find my notes", formatted);
        Assert.Contains("[1] Consultant: Let me check.", formatted);
        Assert.Contains("Tools used (1):", formatted);
        Assert.Contains("- memory_search(\"notes\") ✓", formatted);
        Assert.Contains("[2] Visitor: thanks", formatted);
    }

    [Fact]
    public void FormatChannelText_AssistantWithoutToolCalls_NoBlockEmitted()
    {
        var records = new List<MessageRecord>
        {
            BuildRecord(role: "assistant", content: "Hello there.", toolCalls: null),
        };

        var formatted = HistoryReadTool.FormatChannelTextForTesting(records);

        Assert.Contains("Consultant: Hello there.", formatted);
        Assert.DoesNotContain("Tools used", formatted);
    }

    [Fact]
    public void FormatChannelText_IndexNumbering_StableAcrossWithAndWithoutToolCalls()
    {
        var withTools = new List<MessageRecord>
        {
            BuildRecord(role: "user", content: "u1", toolCalls: null),
            BuildRecord(
                role: "assistant",
                content: "a1",
                toolCalls: """[{"name":"x","args":"","ok":true,"pos":"after"}]"""),
            BuildRecord(role: "user", content: "u2", toolCalls: null),
        };

        var withoutTools = new List<MessageRecord>
        {
            BuildRecord(role: "user", content: "u1", toolCalls: null),
            BuildRecord(role: "assistant", content: "a1", toolCalls: null),
            BuildRecord(role: "user", content: "u2", toolCalls: null),
        };

        var formattedWith = HistoryReadTool.FormatChannelTextForTesting(withTools);
        var formattedWithout = HistoryReadTool.FormatChannelTextForTesting(withoutTools);

        Assert.Contains("[0] Visitor: u1", formattedWith);
        Assert.Contains("[1] Consultant: a1", formattedWith);
        Assert.Contains("[2] Visitor: u2", formattedWith);

        Assert.Contains("[0] Visitor: u1", formattedWithout);
        Assert.Contains("[1] Consultant: a1", formattedWithout);
        Assert.Contains("[2] Visitor: u2", formattedWithout);
    }

    private static MessageRecord BuildRecord(string role, string content, string? toolCalls)
    {
        return new MessageRecord
        {
            UserId = role == "user" ? "user-1" : "assistant",
            ChannelId = "ch",
            Role = role,
            Content = content,
            Timestamp = DateTimeOffset.UtcNow,
            Category = MessageCategory.Normal,
            ToolCalls = toolCalls,
        };
    }
}
