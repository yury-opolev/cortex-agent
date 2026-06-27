using Cortex.Contained.Agent.Host.Memory;

namespace Cortex.Contained.Agent.Host.Tests.Memory;

public sealed class MemorySettingsStoreTests
{
    [Fact]
    public void IsMemoryEnabled_DefaultsTrue_WhenNeverSet()
    {
        using var store = new MemorySettingsStore();
        Assert.True(store.IsMemoryEnabled);
    }

    [Fact]
    public void Update_SetsMemoryEnabledFalse_IsMemoryEnabledFalse()
    {
        using var store = new MemorySettingsStore();
        store.Update(null, null, null, memoryEnabled: false);
        Assert.False(store.IsMemoryEnabled);
        Assert.False(store.MemoryEnabled);
    }

    [Fact]
    public void Update_MemoryEnabledNull_IsMemoryEnabledTrue()
    {
        using var store = new MemorySettingsStore();
        store.Update(null, null, null, memoryEnabled: null);
        Assert.True(store.IsMemoryEnabled);
    }
}
