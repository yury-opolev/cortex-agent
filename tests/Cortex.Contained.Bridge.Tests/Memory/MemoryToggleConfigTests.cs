using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Tests.Memory;

public sealed class MemoryToggleConfigTests
{
    [Fact]
    public void MemorySettingsConfig_Enabled_DefaultsTrue()
    {
        Assert.True(new MemorySettingsConfig().Enabled);
    }

    [Fact]
    public void MemoryConfigDto_Enabled_DefaultsTrue()
    {
        Assert.True(new MemoryConfig().Enabled);
    }
}
