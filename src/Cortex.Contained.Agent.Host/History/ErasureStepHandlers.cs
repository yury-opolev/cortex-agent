using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>One synchronous cleanup step of the erasure workflow (§11 step 3).</summary>
public interface IErasureStep
{
    /// <summary>The step name (see <see cref="ErasureSteps"/>).</summary>
    string Name { get; }

    /// <summary>Runs the step for <paramref name="task"/>; true when it is complete. The caller holds the write lease.</summary>
    Task<bool> RunAsync(ErasureTask task, CancellationToken cancellationToken);
}

/// <summary>Applies the purge to the index, optimizes FTS and truncates the index WAL.</summary>
public sealed class IndexErasureStep(HistoryProjector projector) : IErasureStep
{
    /// <inheritdoc />
    public string Name => ErasureSteps.Index;

    /// <inheritdoc />
    public Task<bool> RunAsync(ErasureTask task, CancellationToken cancellationToken) => projector.EraseAsync(cancellationToken);
}

/// <summary>
/// <c>wal_checkpoint(TRUNCATE)</c> on <c>messages.db</c>, checking the result and retrying while
/// the write lease keeps recall readers out.
/// </summary>
public sealed class MessagesWalErasureStep(IHistoryErasureStore store) : IErasureStep
{
    /// <inheritdoc />
    public string Name => ErasureSteps.MessagesWal;

    /// <inheritdoc />
    public Task<bool> RunAsync(ErasureTask task, CancellationToken cancellationToken) =>
        WalCheckpointRetry.TruncateAsync(store, cancellationToken);
}

/// <summary>
/// <c>wal_checkpoint(TRUNCATE)</c> reports "busy" as a result column rather than failing, so every
/// caller checks <see cref="WalCheckpointResult.Complete"/> and retries while readers drain.
/// </summary>
internal static class WalCheckpointRetry
{
    private const int Attempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    public static async Task<bool> TruncateAsync(IHistoryErasureStore store, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if ((await store.CheckpointWalAsync(cancellationToken).ConfigureAwait(false)).Complete)
            {
                return true;
            }

            if (attempt < Attempts)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }
}

/// <summary>
/// Deletes <c>/app/data/tool-output</c> spill files. Spill files can hold copies of any earlier
/// history, so all-channel purges remove all of them; channel-scoped purges remove the files the
/// purged <c>tool_result</c> events referenced (<see cref="ErasureTask.SpillFiles"/>).
/// </summary>
public sealed class ToolOutputErasureStep(string directory) : IErasureStep
{
    /// <inheritdoc />
    public string Name => ErasureSteps.ToolOutput;

    /// <inheritdoc />
    public Task<bool> RunAsync(ErasureTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!Directory.Exists(directory))
        {
            return Task.FromResult(true);
        }

        var files = task.ChannelId is null
            ? Directory.EnumerateFiles(directory, "*.txt").ToList()
            : task.SpillFiles.Select(name => Path.Combine(directory, Path.GetFileName(name))).ToList();
        var allGone = true;
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                allGone = false;
            }
            catch (UnauthorizedAccessException)
            {
                allGone = false;
            }
        }

        return Task.FromResult(allGone);
    }
}

/// <summary>Deletes <c>subagents.db</c> tasks whose run window overlaps the purged range.</summary>
public sealed class SubagentErasureStep(SubagentSessionStore store) : IErasureStep
{
    /// <inheritdoc />
    public string Name => ErasureSteps.Subagents;

    /// <inheritdoc />
    public Task<bool> RunAsync(ErasureTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.RangeTo is not { } to)
        {
            return Task.FromResult(true);
        }

        var (_, activeSkipped) = store.PurgeOverlapping(task.RangeFrom, to, task.ChannelId);
        return Task.FromResult(activeSkipped == 0);
    }
}

/// <summary>
/// Destroys smoke-evaluation run directories whose corpus overlaps the purged range (D3). A run
/// directory without a readable <c>manifest.json</c> is destroyed too. Owner-authored files at the
/// root (queries, analysis plan) are kept.
/// </summary>
public sealed class SmokeArtifactErasureStep(IOptionsMonitor<HistoryOptions> options) : IErasureStep
{
    private static readonly JsonSerializerOptions manifestJson = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public string Name => ErasureSteps.SmokeArtifacts;

    /// <inheritdoc />
    public Task<bool> RunAsync(ErasureTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var root = options.CurrentValue.SmokeArtifactsDirectory;
        if (task.RangeTo is not { } to || !Directory.Exists(root))
        {
            return Task.FromResult(true);
        }

        var allGone = true;
        foreach (var run in Directory.EnumerateDirectories(root))
        {
            if (!Overlaps(Path.Combine(run, "manifest.json"), task.RangeFrom, to))
            {
                continue;
            }

            try
            {
                Directory.Delete(run, recursive: true);
            }
            catch (IOException)
            {
                allGone = false;
            }
            catch (UnauthorizedAccessException)
            {
                allGone = false;
            }
        }

        return Task.FromResult(allGone);
    }

    private static bool Overlaps(string manifestPath, DateTimeOffset? from, DateTimeOffset to)
    {
        try
        {
            using var stream = File.OpenRead(manifestPath);
            var manifest = JsonSerializer.Deserialize<SmokeRunManifest>(stream, manifestJson);
            return manifest is null || (manifest.CorpusFrom <= to && (from is null || manifest.CorpusTo >= from));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}

/// <summary>Corpus range recorded by the smoke-evaluation harness in each run directory.</summary>
public sealed record SmokeRunManifest(DateTimeOffset CorpusFrom, DateTimeOffset CorpusTo);

/// <summary>
/// <c>VACUUM</c> of <c>messages.db</c> and the current index generation after large purges and on
/// the weekly maintenance task (§11 step 6).
/// </summary>
public sealed class VacuumErasureStep(IHistoryErasureStore store, HistoryProjector projector, IOptionsMonitor<HistoryOptions> options, TimeProvider time) : IErasureStep
{
    /// <summary>Id prefix of maintenance tasks that always vacuum.</summary>
    public const string MaintenanceTaskPrefix = "maintenance-vacuum-";

    /// <inheritdoc />
    public string Name => ErasureSteps.Vacuum;

    // Tasks whose VACUUMs finished but whose checkpoint was still busy: a retry only re-runs the checkpoint.
    private readonly HashSet<string> vacuumed = [];

    /// <inheritdoc />
    public async Task<bool> RunAsync(ErasureTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var required = task.Id.StartsWith(MaintenanceTaskPrefix, StringComparison.Ordinal)
            || task.Deleted > options.CurrentValue.VacuumAfterPurgeRows;
        if (!required)
        {
            return true;
        }

        bool alreadyVacuumed;
        lock (this.vacuumed)
        {
            alreadyVacuumed = this.vacuumed.Contains(task.Id);
        }

        if (!alreadyVacuumed)
        {
            await store.VacuumAsync(time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            await projector.VacuumAsync(cancellationToken).ConfigureAwait(false);
            lock (this.vacuumed)
            {
                this.vacuumed.Add(task.Id);
            }
        }

        if (!await WalCheckpointRetry.TruncateAsync(store, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        lock (this.vacuumed)
        {
            this.vacuumed.Remove(task.Id);
        }

        return true;
    }
}
