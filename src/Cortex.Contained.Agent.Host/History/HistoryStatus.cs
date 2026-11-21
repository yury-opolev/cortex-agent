namespace Cortex.Contained.Agent.Host.History;

/// <summary>Live projector state for health and coverage reporting (thread-safe).</summary>
public sealed class HistoryStatus
{
    private readonly Lock sync = new();
    private HistoryStatusSnapshot current = new(null, false, false, false, null, 0);

    /// <summary>A consistent copy of the current state.</summary>
    public HistoryStatusSnapshot Snapshot()
    {
        lock (this.sync)
        {
            return this.current;
        }
    }

    internal void Update(Func<HistoryStatusSnapshot, HistoryStatusSnapshot> change)
    {
        lock (this.sync)
        {
            this.current = change(this.current);
        }
    }
}

/// <summary>Projector state at one instant.</summary>
/// <param name="Generation">Current (served) generation, or null.</param>
/// <param name="BuildComplete">True when the current generation finished its build.</param>
/// <param name="BuildInProgress">True while a build or rebuild runs.</param>
/// <param name="Halted">True after a batch failed three times (§9.5).</param>
/// <param name="LastError">Last failure message, or null.</param>
/// <param name="Checkpoint">Last applied journal sequence of the current generation.</param>
public sealed record HistoryStatusSnapshot(
    long? Generation,
    bool BuildComplete,
    bool BuildInProgress,
    bool Halted,
    string? LastError,
    long Checkpoint);

/// <summary>Points at which a test can interrupt the cut-over (§14.1 crash at each step).</summary>
public enum CutOverStep
{
    /// <summary>Final tail replayed, <c>BuildComplete</c> not yet set.</summary>
    BeforeComplete,

    /// <summary><c>BuildComplete=1</c> written, pointer not yet rewritten.</summary>
    AfterComplete,

    /// <summary>Pointer rewritten, old generation not yet unlinked.</summary>
    AfterPointer,
}

/// <summary>Pure startup/steady-state check: may this generation keep serving (§9.5)?</summary>
public static class ProjectorStartupDecision
{
    /// <summary>Returns why <paramref name="state"/> must be rebuilt, or null when it may resume.</summary>
    public static string? RebuildReason(HistorySourceMeta meta, ProjectorState? state, string configFingerprint)
    {
        ArgumentNullException.ThrowIfNull(meta);
        if (state is null)
        {
            return "no current generation";
        }

        if (!string.Equals(state.SourceInstanceId, meta.DbInstanceId, StringComparison.Ordinal))
        {
            return "source instance changed";
        }

        if (state.SourceRestoreGeneration != meta.RestoreGeneration)
        {
            return "source restore generation changed";
        }

        if (!string.Equals(state.ConfigFingerprint, configFingerprint, StringComparison.Ordinal))
        {
            return "configuration fingerprint changed";
        }

        if (!state.BuildComplete)
        {
            return "build incomplete";
        }

        if (state.CheckpointJournalSeq < meta.JournalPrunedThrough || state.CheckpointJournalSeq > meta.JournalHead)
        {
            return "checkpoint outside the journal";
        }

        return null;
    }
}
