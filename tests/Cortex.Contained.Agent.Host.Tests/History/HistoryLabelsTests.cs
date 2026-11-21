using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Contracts.Hub;
using static Cortex.Contained.Agent.Host.Tests.History.HistoryRows;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class HistoryLabelsTests
{
    [Fact]
    public void For_EachCategoryAndRole_ReturnsHonestLabel()
    {
        Assert.Equal(HistoryLineLabel.User, HistoryLabels.For(Row(1, Day, "x")));
        Assert.Equal(HistoryLineLabel.Assistant, HistoryLabels.For(Row(2, Day, "x", role: "assistant")));
        Assert.Equal(HistoryLineLabel.Delivered, HistoryLabels.For(Row(3, Day, "x", role: "assistant", category: MessageCategory.Proactive)));
        Assert.Equal(HistoryLineLabel.System, HistoryLabels.For(Row(4, Day, "x", role: "assistant", category: MessageCategory.System)));
        Assert.Equal(HistoryLineLabel.Internal, HistoryLabels.For(Row(5, Day, "x", category: MessageCategory.Internal)));
    }

    [Fact]
    public void For_AssistantTurnThatUsedHistoryTool_IsRecallEcho()
    {
        var row = Row(1, Day, "You said X", role: "assistant", toolCalls: ToolCalls("memory_search", "history_search"));

        Assert.Equal(HistoryLineLabel.RecallEcho, HistoryLabels.For(row));
        Assert.Equal("recall_echo", HistoryLabels.Text(HistoryLineLabel.RecallEcho));
        Assert.Equal("internal (not necessarily delivered)", HistoryLabels.Text(HistoryLineLabel.Internal));
    }

    [Fact]
    public void IndexedBody_HasFoldedContentAndToolNamesOnly()
    {
        var row = Row(1, Day, "Søren sent a photo\n[image attached: image/png]", role: "assistant", toolCalls: ToolCalls("memory_search"));

        Assert.Equal("soren sent a photo\n[attachment:image/png]\nmemory_search", HistoryRowText.IndexedBody(row));
    }

    [Fact]
    public void Fingerprint_SameLengthEdit_Changes()
    {
        var before = HistoryFingerprint.Compute([Row(1, Day, "meet at 10")]);
        var after = HistoryFingerprint.Compute([Row(1, Day, "meet at 11")]);

        Assert.NotEqual(before, after);
        Assert.Equal(64, before.Length);
    }
}
