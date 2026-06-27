using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>Applies an optional built-in-memory enable toggle to <see cref="MemorySettingsConfig"/>. Null = leave as-is.</summary>
public static class MemoryToggleApply
{
    public static void Apply(MemorySettingsConfig memory, bool? enabled)
    {
        if (enabled.HasValue)
        {
            memory.Enabled = enabled.Value;
        }
    }

    /// <summary>
    /// Forces the persisted master enable flag onto a memory-settings update before it is pushed
    /// to the agent. The memory-settings page omits <c>enabled</c>, so the bound DTO defaults it
    /// to true; without this, saving unrelated settings would silently re-enable a disabled
    /// memory subsystem. Only the <see cref="MemoryConfig.Enabled"/> flag is overridden.
    /// </summary>
    public static MemoryConfig WithPersistedEnabled(MemoryConfig requested, MemorySettingsConfig persisted)
        => requested with { Enabled = persisted.Enabled };
}
