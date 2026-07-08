using Cortex.Contained.Bridge.Setup;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests;

public sealed class SubagentSettingsYamlTests
{
    [Fact]
    public void PersistSettingsToYaml_WritesMaxConcurrentSubagents()
    {
        var config = new BridgeConfig { MaxConcurrentSubagents = 7 };
        var path = Path.Combine(Path.GetTempPath(), "cortex-" + Guid.NewGuid().ToString("N") + ".yml");

        try
        {
            BridgeSettingsWriter.PersistSettingsToYaml(config, path);
            var yaml = File.ReadAllText(path);
            Assert.Contains("maxConcurrentSubagents: 7", yaml, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }
}
