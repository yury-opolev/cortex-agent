using Cortex.Contained.Bridge.Setup;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests;

public sealed class TenantShowFullLlmErrorsYamlTests
{
    [Theory]
    [InlineData(true, "showFullLlmErrors: true")]
    [InlineData(false, "showFullLlmErrors: false")]
    public void PersistSettingsToYaml_ExplicitFlag_IsWritten(bool value, string expectedLine)
    {
        var yaml = WriteYaml(new TenantConfig { Endpoint = "http://127.0.0.1:5101/hub/agent", ShowFullLlmErrors = value });

        Assert.Contains(expectedLine, yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistSettingsToYaml_UnsetFlag_IsOmitted()
    {
        var yaml = WriteYaml(new TenantConfig { Endpoint = "http://127.0.0.1:5101/hub/agent" });

        Assert.DoesNotContain("showFullLlmErrors", yaml, StringComparison.Ordinal);
    }

    private static string WriteYaml(TenantConfig tenant)
    {
        var config = new BridgeConfig();
        config.Tenants["guest"] = tenant;
        var path = Path.Combine(Path.GetTempPath(), "cortex-" + Guid.NewGuid().ToString("N") + ".yml");
        try
        {
            BridgeSettingsWriter.PersistSettingsToYaml(config, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
