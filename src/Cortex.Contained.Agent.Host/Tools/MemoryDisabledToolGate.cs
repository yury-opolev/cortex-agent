using System.Collections.Frozen;
using Cortex.Contained.Agent.Host.Memory;

namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// Hides the built-in memory tool family from every conversation while the
/// memory subsystem is disabled (<see cref="MemorySettingsStore.IsMemoryEnabled"/> is false).
/// Uses the generic <see cref="IConversationToolGate"/> extension point so the model never
/// sees memory tools it cannot service (the embeddings sidecar is stopped when memory is off).
/// </summary>
public sealed class MemoryDisabledToolGate : IConversationToolGate
{
    private static readonly FrozenSet<string> memoryToolNames = FrozenSet.ToFrozenSet(
        [
            "memory_search",
            "memory_ingest",
            "memory_get",
            "memory_update",
            "memory_delete",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly MemorySettingsStore store;

    public MemoryDisabledToolGate(MemorySettingsStore store)
    {
        this.store = store;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> GetHiddenTools(string? conversationId)
    {
        return this.store.IsMemoryEnabled ? FrozenSet<string>.Empty : memoryToolNames;
    }
}
