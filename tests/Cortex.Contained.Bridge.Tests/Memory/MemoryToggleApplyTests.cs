using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Tests.Memory;

public sealed class MemoryToggleApplyTests
{
    [Fact]
    public void Apply_SetsEnabledFalse()
    {
        var mem = new MemorySettingsConfig(); // true by default
        MemoryToggleApply.Apply(mem, enabled: false);
        Assert.False(mem.Enabled);
    }

    [Fact]
    public void Apply_NullLeavesUnchanged()
    {
        var mem = new MemorySettingsConfig { Enabled = false };
        MemoryToggleApply.Apply(mem, enabled: null);
        Assert.False(mem.Enabled);
    }

    [Fact]
    public void WithPersistedEnabled_ForcesPersistedMasterFlag_PreservesOtherFields()
    {
        // The memory-settings page omits `enabled`, so the bound MemoryConfig defaults it to
        // true. Saving unrelated settings must NOT re-enable a disabled memory subsystem.
        var requested = new MemoryConfig { Enabled = true, DuplicateThreshold = 0.42f };
        var persisted = new MemorySettingsConfig { Enabled = false };

        var result = MemoryToggleApply.WithPersistedEnabled(requested, persisted);

        Assert.False(result.Enabled);                  // forced from persisted master flag
        Assert.Equal(0.42f, result.DuplicateThreshold); // other fields preserved
    }
}
