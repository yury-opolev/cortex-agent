using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Verifies the per-turn attribution rule that pairs orphan tool-only LLM
/// responses with the next text-bearing record id (as <c>before</c>) and
/// the in-response tools (as <c>after</c>).
/// </summary>
public class AgentRuntimeToolHistoryTests
{
    [Fact]
    public void AttributeTools_TextWithToolsThenToolOnlyThenText_AssignsBeforeAndAfter()
    {
        // Round 1: text "Let me check" + tools [search]
        // Round 2: tool-only [get_more]
        // Round 3: text "Done" + tools [ingest]
        //
        // Expected:
        // - Record from round 1 has after=[search]
        // - Round 2 produces no record; its tools buffer as before
        // - Record from round 3 has before=[get_more], after=[ingest]

        var attributor = new ToolCallAttributor();

        // Round 1: text + tools
        attributor.RecordResponseTools(textRecordId: 100, [
            new ToolCallSummaryEntry("search", "\"x\"", true, "after"),
        ]);

        var afterRound1 = attributor.DrainPatches();
        Assert.Single(afterRound1);
        Assert.Equal(100L, afterRound1[0].MessageId);
        Assert.Single(afterRound1[0].Entries);
        Assert.Equal("search", afterRound1[0].Entries[0].Name);
        Assert.Equal("after", afterRound1[0].Entries[0].Pos);

        // Round 2: tool-only (no record id)
        attributor.RecordResponseTools(textRecordId: null, [
            new ToolCallSummaryEntry("get_more", "\"y\"", true, "after"),
        ]);

        var afterRound2 = attributor.DrainPatches();
        Assert.Empty(afterRound2);

        // Round 3: text + tools
        attributor.RecordResponseTools(textRecordId: 101, [
            new ToolCallSummaryEntry("ingest", "\"z\"", true, "after"),
        ]);

        var afterRound3 = attributor.DrainPatches();
        Assert.Single(afterRound3);
        Assert.Equal(101L, afterRound3[0].MessageId);
        Assert.Equal(2, afterRound3[0].Entries.Count);
        Assert.Equal("get_more", afterRound3[0].Entries[0].Name);
        Assert.Equal("before", afterRound3[0].Entries[0].Pos);
        Assert.Equal("ingest", afterRound3[0].Entries[1].Name);
        Assert.Equal("after", afterRound3[0].Entries[1].Pos);
    }

    [Fact]
    public void AttributeTools_TextOnly_NoEntries_ProducesNoPatch()
    {
        var attributor = new ToolCallAttributor();

        attributor.RecordResponseTools(textRecordId: 200, []);

        var patches = attributor.DrainPatches();

        Assert.Empty(patches);
    }

    [Fact]
    public void AttributeTools_ToolOnlyResponse_ProducesNoPatch()
    {
        // A tool-only LLM response (no text, no record id) produces no patch
        // immediately. The orphan tools live in the before-buffer until a
        // future text-bearing response consumes them, or until the attributor
        // goes out of scope at end of turn.
        var attributor = new ToolCallAttributor();

        attributor.RecordResponseTools(textRecordId: null, [
            new ToolCallSummaryEntry("orphan", "\"x\"", true, "after"),
        ]);

        Assert.Empty(attributor.DrainPatches());
    }

    [Fact]
    public void AttributeTools_OrphanPersistsAcrossDrains_UntilConsumedByTextRecord()
    {
        // Realistic AgentRuntime flow: each tool-bearing round drains its patches
        // immediately, but the before-buffer must survive across drains so a
        // subsequent text-bearing round can still attach the orphan.
        var attributor = new ToolCallAttributor();

        attributor.RecordResponseTools(textRecordId: null, [
            new ToolCallSummaryEntry("orphan", "\"y\"", true, "after"),
        ]);
        Assert.Empty(attributor.DrainPatches());
        Assert.Empty(attributor.DrainPatches());

        attributor.RecordResponseTools(textRecordId: 400, []);
        var patches = attributor.DrainPatches();

        Assert.Single(patches);
        Assert.Equal(400L, patches[0].MessageId);
        Assert.Single(patches[0].Entries);
        Assert.Equal("orphan", patches[0].Entries[0].Name);
        Assert.Equal("before", patches[0].Entries[0].Pos);
    }
}
