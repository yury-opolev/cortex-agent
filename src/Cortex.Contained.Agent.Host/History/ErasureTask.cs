namespace Cortex.Contained.Agent.Host.History;

/// <summary>Names of the synchronous cleanup steps of the erasure workflow (§11).</summary>
public static class ErasureSteps
{
    /// <summary>Apply the purge to the current index generation, optimize FTS, truncate its WAL.</summary>
    public const string Index = "index";

    /// <summary><c>wal_checkpoint(TRUNCATE)</c> on <c>messages.db</c>.</summary>
    public const string MessagesWal = "messages-wal";

    /// <summary>Delete <c>/app/data/tool-output</c> spill files (all of them for all-channel purges, the referenced ones otherwise).</summary>
    public const string ToolOutput = "tool-output";

    /// <summary>Delete overlapping <c>subagents.db</c> entries.</summary>
    public const string Subagents = "subagents";

    /// <summary>Destroy overlapping smoke-evaluation artifacts.</summary>
    public const string SmokeArtifacts = "smoke-artifacts";

    /// <summary>Erase the curated memories inside this purge's quarantine windows (owner opt-in per purge).</summary>
    public const string DependentMemories = "dependent-memories";

    /// <summary><c>VACUUM</c> of <c>messages.db</c> and the current index generation.</summary>
    public const string Vacuum = "vacuum";

    /// <summary>Prefix of the step that unlinks a retired index generation.</summary>
    public const string RetiredGenerationPrefix = "retired-generation:";

    /// <summary>Step name for retiring generation <paramref name="generation"/>.</summary>
    public static string RetiredGeneration(long generation) =>
        RetiredGenerationPrefix + generation.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The steps of a user purge of <paramref name="scope"/>; <see cref="DependentMemories"/> only when the owner chose deletion.</summary>
    public static IReadOnlyList<string> ForPurge(HistoryPurgeScope scope, DependentMemoryAction dependentMemories = DependentMemoryAction.Review)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return dependentMemories == DependentMemoryAction.Delete
            ? [Index, MessagesWal, ToolOutput, Subagents, SmokeArtifacts, DependentMemories, Vacuum]
            : [Index, MessagesWal, ToolOutput, Subagents, SmokeArtifacts, Vacuum];
    }
}

/// <summary>One durable erasure work item stored in <c>Meta.PendingErasure</c>.</summary>
public sealed record ErasureTask
{
    /// <summary>Workflow id.</summary>
    public required string Id { get; init; }

    /// <summary>Purged channel, or null for all channels.</summary>
    public string? ChannelId { get; init; }

    /// <summary>Oldest deleted timestamp, or null.</summary>
    public DateTimeOffset? RangeFrom { get; init; }

    /// <summary>Newest deleted timestamp, or null.</summary>
    public DateTimeOffset? RangeTo { get; init; }

    /// <summary>Rows deleted.</summary>
    public int Deleted { get; init; }

    /// <summary>When the task was recorded.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Steps still to run.</summary>
    public required IReadOnlyList<string> Steps { get; init; }

    /// <summary>Tool-output spill files referenced by the purged tool results (channel-scoped purges delete exactly these).</summary>
    public IReadOnlyList<string> SpillFiles { get; init; } = [];
}

/// <summary>Outcome of <c>PRAGMA wal_checkpoint(TRUNCATE)</c>.</summary>
public sealed record WalCheckpointResult(int Busy, int Log, int Checkpointed)
{
    /// <summary>True when the checkpoint ran to completion and the WAL was truncated.</summary>
    public bool Complete => this.Busy == 0 && this.Log <= 0;
}
