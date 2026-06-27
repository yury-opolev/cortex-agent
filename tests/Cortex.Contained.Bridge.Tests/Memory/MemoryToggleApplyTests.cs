using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Contracts.Config;

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
}
