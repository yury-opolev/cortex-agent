using System.Text;
using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Config.Yaml;
using Microsoft.Extensions.Configuration;

namespace Cortex.Contained.Bridge.Tests.Connectors;

/// <summary>
/// Tests <see cref="ConnectorConfigYamlWriter"/> — the <c>connectors:</c> block of the Bridge YAML.
/// The master toggle only flips <c>enabled</c>, but the writer emits every field so a save never
/// silently drops a hand-configured limit. The round-trip test also proves the section does not
/// clobber the neighbouring <c>mcp:</c> section.
/// </summary>
public sealed class ConnectorConfigYamlWriterTests
{
    private static string Write(ConnectorSettingsConfig connectors)
    {
        var sb = new StringBuilder();
        ConnectorConfigYamlWriter.AppendConnectorsSection(sb, connectors);
        return sb.ToString();
    }

    [Fact]
    public void AppendConnectorsSection_WritesEnabledTrue()
    {
        var yaml = Write(new ConnectorSettingsConfig { Enabled = true });

        Assert.Contains("connectors:", yaml, StringComparison.Ordinal);
        Assert.Contains("enabled: true", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendConnectorsSection_WritesEnabledFalse()
    {
        var yaml = Write(new ConnectorSettingsConfig { Enabled = false });

        Assert.Contains("connectors:", yaml, StringComparison.Ordinal);
        Assert.Contains("enabled: false", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendConnectorsSection_WritesPolicyAndLimitFields()
    {
        var yaml = Write(new ConnectorSettingsConfig
        {
            Enabled = true,
            RequireApproval = false,
            MaxConnectors = 4,
            Replay = new ConnectorReplayConfig { MaxMessages = 7, MaxAge = TimeSpan.FromHours(3) },
            Limits = new ConnectorLimitsConfig { MaxFrameBytes = 2048, MaxMessagesPerMinute = 30 },
        });

        Assert.Contains("requireApproval: false", yaml, StringComparison.Ordinal);
        Assert.Contains("maxConnectors: 4", yaml, StringComparison.Ordinal);
        Assert.Contains("maxMessages: 7", yaml, StringComparison.Ordinal);
        Assert.Contains("maxFrameBytes: 2048", yaml, StringComparison.Ordinal);
        Assert.Contains("maxMessagesPerMinute: 30", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendConnectorsSection_DoesNotClobberOtherSections()
    {
        var mcp = new McpSettingsConfig { Enabled = true };
        var connectors = new ConnectorSettingsConfig
        {
            Enabled = false,
            RequireApproval = false,
            MaxConnectors = 3,
            Replay = new ConnectorReplayConfig { MaxMessages = 11, MaxAge = TimeSpan.FromHours(5) },
            Limits = new ConnectorLimitsConfig { MaxFrameBytes = 4096, MaxMessagesPerMinute = 42 },
        };

        var sb = new StringBuilder();
        sb.AppendLine("agentHubUrl: http://127.0.0.1:5100/hub/agent");
        McpConfigYamlWriter.AppendMcpSection(sb, mcp);
        ConnectorConfigYamlWriter.AppendConnectorsSection(sb, connectors);

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cortex",
            "tests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"connector-cfg-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, sb.ToString());

        try
        {
            var configuration = new ConfigurationBuilder().AddYamlFile(path).Build();
            var bound = new BridgeConfig();
            configuration.Bind(bound);

            // Both sections survive the round-trip — neither overwrites the other.
            Assert.True(bound.Mcp.Enabled);
            Assert.False(bound.Connectors.Enabled);
            Assert.False(bound.Connectors.RequireApproval);
            Assert.Equal(3, bound.Connectors.MaxConnectors);
            Assert.Equal(11, bound.Connectors.Replay.MaxMessages);
            Assert.Equal(TimeSpan.FromHours(5), bound.Connectors.Replay.MaxAge);
            Assert.Equal(4096, bound.Connectors.Limits.MaxFrameBytes);
            Assert.Equal(42, bound.Connectors.Limits.MaxMessagesPerMinute);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
