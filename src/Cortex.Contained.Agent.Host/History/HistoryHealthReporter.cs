using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>The <c>history</c> block of <c>/health</c> (§13).</summary>
public sealed record HistoryHealth(
    bool Enabled,
    bool ToolsEnabled,
    string SqliteVersion,
    long? Generation,
    bool BuildComplete,
    bool BuildInProgress,
    long CheckpointLag,
    long JournalBacklog,
    bool Halted,
    string? LastError,
    int PendingErasureTasks,
    IReadOnlyList<string> PendingErasureSteps);

/// <summary>Builds <see cref="HistoryHealth"/> from options, projector status and <c>messages.db</c> Meta.</summary>
public sealed class HistoryHealthReporter
{
    private readonly IHistorySource source;
    private readonly IHistoryErasureStore erasure;
    private readonly HistoryStatus status;
    private readonly HistoryToolsSwitch toolsSwitch;
    private readonly IOptionsMonitor<HistoryOptions> options;

    public HistoryHealthReporter(
        IHistorySource source,
        IHistoryErasureStore erasure,
        HistoryStatus status,
        HistoryToolsSwitch toolsSwitch,
        IOptionsMonitor<HistoryOptions> options)
    {
        this.source = source;
        this.erasure = erasure;
        this.status = status;
        this.toolsSwitch = toolsSwitch;
        this.options = options;
    }

    /// <summary>Current health snapshot.</summary>
    public async Task<HistoryHealth> SnapshotAsync(CancellationToken cancellationToken)
    {
        HistorySourceMeta meta;
        await using (var snapshot = await this.source.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false))
        {
            meta = await snapshot.ReadMetaAsync(cancellationToken).ConfigureAwait(false);
        }

        var pending = await this.erasure.ReadPendingErasureAsync(cancellationToken).ConfigureAwait(false);
        var state = this.status.Snapshot();
        return new HistoryHealth(
            this.options.CurrentValue.Enabled,
            this.toolsSwitch.IsEnabled,
            SqliteRuntimeInfo.Capabilities.Version,
            state.Generation,
            state.BuildComplete,
            state.BuildInProgress,
            Math.Max(0, meta.JournalHead - state.Checkpoint),
            Math.Max(0, meta.JournalHead - meta.JournalPrunedThrough),
            state.Halted,
            state.LastError,
            pending.Count,
            pending.SelectMany(t => t.Steps).Distinct(StringComparer.Ordinal).ToList());
    }
}
