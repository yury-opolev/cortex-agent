using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// Erasure step <see cref="ErasureSteps.DependentMemories"/>: deletes the not-kept curated memories inside
/// the quarantine windows of its purge in one batch (<c>DeleteManyAsync</c>: memory rows, content files,
/// vectors and keyword rows with a single FTS optimize and WAL truncate). Only scheduled when the owner
/// chose <see cref="DependentMemoryAction.Delete"/> for that purge; re-running it is idempotent, so a
/// failure stays pending (the memories remain quarantined meanwhile) and resumes on the next maintenance tick.
/// The batch is erased in chunks under the quarantine decision lock, re-reading the kept set per chunk, so a
/// memory the owner keeps while the step runs is spared from every chunk that has not been deleted yet.
/// </summary>
public sealed class DependentMemoryErasureStep(MemoryQuarantine quarantine, IMemoryManagementService memories) : IErasureStep
{
    /// <summary>Memories erased per <see cref="IMemoryManagementService.DeleteManyAsync"/> call.</summary>
    public const int ChunkSize = 250;

    /// <inheritdoc />
    public string Name => ErasureSteps.DependentMemories;

    /// <inheritdoc />
    public async Task<bool> RunAsync(ErasureTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var ids = (await quarantine.InErasureAsync(task.Id, cancellationToken).ConfigureAwait(false)).Order(StringComparer.Ordinal).ToList();
        foreach (var chunk in ids.Chunk(ChunkSize))
        {
            await quarantine.WithDecisionLockAsync(
                async ct =>
                {
                    var stillQuarantined = await quarantine.WithoutKeptAsync(chunk, ct).ConfigureAwait(false);
                    if (stillQuarantined.Count > 0)
                    {
                        await memories.DeleteManyAsync(stillQuarantined, ct).ConfigureAwait(false);
                    }

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}
