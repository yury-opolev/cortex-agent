using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// Erasure step <see cref="ErasureSteps.DependentMemories"/>: deletes the not-kept curated memories inside
/// the quarantine windows of its purge in one batch (<c>DeleteManyAsync</c>: memory rows, content files,
/// vectors and keyword rows with a single FTS optimize and WAL truncate). Only scheduled when the owner
/// chose <see cref="DependentMemoryAction.Delete"/> for that purge; re-running it is idempotent, so a
/// failure stays pending (the memories remain quarantined meanwhile) and resumes on the next maintenance tick.
/// </summary>
public sealed class DependentMemoryErasureStep(MemoryQuarantine quarantine, IMemoryManagementService memories) : IErasureStep
{
    /// <inheritdoc />
    public string Name => ErasureSteps.DependentMemories;

    /// <inheritdoc />
    public async Task<bool> RunAsync(ErasureTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var ids = await quarantine.InErasureAsync(task.Id, cancellationToken).ConfigureAwait(false);
        if (ids.Count > 0)
        {
            await memories.DeleteManyAsync(ids.ToList(), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}
