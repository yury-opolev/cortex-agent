using Cortex.Contained.Contracts.Config;

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
}
