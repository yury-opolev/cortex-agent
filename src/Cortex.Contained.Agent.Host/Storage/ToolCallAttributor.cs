namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Per-turn helper that buffers tool-call summary entries from tool-only LLM responses
/// (orphans) and pairs them with the next text-bearing response's record id, producing
/// a list of UPDATE patches the caller can flush to <see cref="MessageStore"/>.
/// </summary>
/// <remarks>
/// One instance per <c>GenerateResponseAsync</c> invocation. Orphans without a following
/// text record are dropped on the next <see cref="DrainPatches"/>.
/// </remarks>
internal sealed class ToolCallAttributor
{
    private readonly List<ToolCallSummaryEntry> orphanedToolEntries = [];
    private readonly List<ToolCallPatch> patches = [];
    private readonly Dictionary<long, List<ToolCallSummaryEntry>> textRows = [];
    private bool turnUsedRecallTool;

    /// <summary>
    /// Records the tool calls executed during one LLM response.
    /// Pass <paramref name="textRecordId"/> = the saved row id when the response had text;
    /// pass null when the response was tool-only (no MessageRecord was saved).
    /// </summary>
    /// <remarks>
    /// Recall is per turn: once any <c>history_*</c> tool ran, every assistant row of the turn
    /// (earlier rows are back-patched, later ones patched on save) carries a recall entry or the
    /// synthetic turn marker (itself a <c>history_*</c> name), so the final reply quoting
    /// recalled text is labelled <c>recall_echo</c>.
    /// </remarks>
    public void RecordResponseTools(long? textRecordId, IReadOnlyList<ToolCallSummaryEntry> afterEntries)
    {
        if (afterEntries.Any(e => ToolCallSummary.IsRecallTool(e.Name)))
        {
            this.turnUsedRecallTool = true;
        }

        if (textRecordId is null)
        {
            // Tool-only response — entries become "before" for the next text record.
            foreach (var entry in afterEntries)
            {
                this.orphanedToolEntries.Add(entry with { Pos = "before" });
            }

            this.MarkEarlierRows(except: null);
            return;
        }

        var combined = new List<ToolCallSummaryEntry>(this.orphanedToolEntries.Count + afterEntries.Count + 1);
        combined.AddRange(this.orphanedToolEntries);
        combined.AddRange(afterEntries);
        this.orphanedToolEntries.Clear();

        if (this.turnUsedRecallTool && !HasRecallEntry(combined))
        {
            combined.Add(ToolCallSummary.CreateRecallTurnMarker());
        }

        this.textRows[textRecordId.Value] = combined;
        if (combined.Count > 0)
        {
            this.patches.Add(new ToolCallPatch(textRecordId.Value, combined));
        }

        this.MarkEarlierRows(except: textRecordId.Value);
    }

    private static bool HasRecallEntry(List<ToolCallSummaryEntry> entries) =>
        entries.Any(e => ToolCallSummary.IsRecallTool(e.Name));

    private void MarkEarlierRows(long? except)
    {
        if (!this.turnUsedRecallTool)
        {
            return;
        }

        foreach (var (id, entries) in this.textRows)
        {
            if (id == except || HasRecallEntry(entries))
            {
                continue;
            }

            entries.Add(ToolCallSummary.CreateRecallTurnMarker());
            this.patches.Add(new ToolCallPatch(id, entries));
        }
    }

    /// <summary>
    /// Returns the accumulated patches and clears them.
    /// The before-buffer is preserved so orphans recorded since the last text-bearing
    /// response remain available to attach to a future record. Buffered orphans without
    /// any following text record are discarded only when the attributor goes out of scope.
    /// </summary>
    public IReadOnlyList<ToolCallPatch> DrainPatches()
    {
        if (this.patches.Count == 0)
        {
            return [];
        }

        var snapshot = this.patches.ToArray();
        this.patches.Clear();
        return snapshot;
    }
}

/// <summary>
/// One UPDATE to apply: replace <c>Messages.ToolCalls</c> for <see cref="MessageId"/>
/// with the JSON-serialised <see cref="Entries"/>.
/// </summary>
internal sealed record ToolCallPatch(long MessageId, IReadOnlyList<ToolCallSummaryEntry> Entries);
