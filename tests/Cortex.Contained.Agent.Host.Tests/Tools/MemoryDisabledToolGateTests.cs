using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Tools;

namespace Cortex.Contained.Agent.Host.Tests.Tools;

public sealed class MemoryDisabledToolGateTests
{
    [Fact]
    public void Enabled_HidesNothing()
    {
        using var store = new MemorySettingsStore(); // default enabled
        var gate = new MemoryDisabledToolGate(store);
        Assert.Empty(gate.GetHiddenTools("any-conversation"));
    }

    [Fact]
    public void Disabled_HidesAllFiveMemoryTools()
    {
        using var store = new MemorySettingsStore();
        store.Update(null, null, null, memoryEnabled: false);
        var gate = new MemoryDisabledToolGate(store);

        var hidden = gate.GetHiddenTools("any-conversation");

        Assert.Equal(5, hidden.Count);
        Assert.Contains("memory_search", hidden);
        Assert.Contains("memory_ingest", hidden);
        Assert.Contains("memory_get", hidden);
        Assert.Contains("memory_update", hidden);
        Assert.Contains("memory_delete", hidden);
    }
}
