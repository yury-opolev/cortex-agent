using System.Text;
using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Bridge.Connectors.Media;
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
    public void AppendConnectorsSection_WritesMediaFields()
    {
        var yaml = Write(new ConnectorSettingsConfig
        {
            Enabled = true,
            Media = new ConnectorMediaConfig
            {
                Enabled = false,
                MaxAttachmentsPerMessage = 2,
                MaxAttachmentBytes = 1234,
                MaxInlineBytes = 567,
                HandleTtl = TimeSpan.FromMinutes(3),
                MaxStoredBytesPerConnector = 999,
                MaxUploadsPerMinute = 7,
                AllowedMimeTypes = ["image/png", "image/webp"],
            },
        });

        Assert.Contains("  media:", yaml, StringComparison.Ordinal);
        Assert.Contains("    enabled: false", yaml, StringComparison.Ordinal);
        Assert.Contains("    maxAttachmentsPerMessage: 2", yaml, StringComparison.Ordinal);
        Assert.Contains("    maxAttachmentBytes: 1234", yaml, StringComparison.Ordinal);
        Assert.Contains("    maxInlineBytes: 567", yaml, StringComparison.Ordinal);
        Assert.Contains("    maxStoredBytesPerConnector: 999", yaml, StringComparison.Ordinal);
        Assert.Contains("    maxUploadsPerMinute: 7", yaml, StringComparison.Ordinal);
        Assert.Contains("      - image/png", yaml, StringComparison.Ordinal);
        Assert.Contains("      - image/webp", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectorMediaConfig_DefaultsMatchAgentAllowances()
    {
        var media = new ConnectorMediaConfig();

        Assert.True(media.Enabled);
        Assert.Equal(4, media.MaxAttachmentsPerMessage);
        Assert.Equal(8L * 1024 * 1024, media.MaxAttachmentBytes);
        Assert.Equal(256 * 1024, media.MaxInlineBytes);
        Assert.Equal(TimeSpan.FromMinutes(10), media.HandleTtl);

        // Empty by design: IConfiguration.Bind APPENDS to a seeded collection, so seeding the
        // defaults here would make narrowing the allow-list from YAML impossible.
        Assert.Empty(media.AllowedMimeTypes);
        Assert.Equal(
            ["image/png", "image/jpeg", "image/gif", "image/webp"],
            ConnectorMediaConfig.DefaultAllowedMimeTypes);
    }

    [Fact]
    public void AppendConnectorsSection_EmptyAllowedMimeTypes_RoundTripsAsDefaultsNotAsBlockEverything()
    {
        // Locks in the design decision: an unset allow-list must survive write -> read -> resolve
        // as the four built-in image types. If the writer ever emits a bare `allowedMimeTypes:`
        // key that binds to a non-null empty list, the policy would take the "operator configured
        // something" branch and silently allow nothing.
        var connectors = new ConnectorSettingsConfig
        {
            Enabled = true,
            Media = new ConnectorMediaConfig { AllowedMimeTypes = [] },
        };

        var sb = new StringBuilder();
        sb.AppendLine("agentHubUrl: http://127.0.0.1:5100/hub/agent");
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

            var policy = ConnectorMediaPolicy.From(bound.Connectors.Media, bound.Connectors.Limits.MaxFrameBytes);

            Assert.Equal(4, policy.AllowedMimeTypes.Count);
            Assert.True(policy.IsMimeTypeAllowed("image/png"));
            Assert.True(policy.IsMimeTypeAllowed("image/webp"));
        }
        finally
        {
            File.Delete(path);
        }
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
            Media = new ConnectorMediaConfig
            {
                Enabled = true,
                MaxAttachmentsPerMessage = 2,
                MaxAttachmentBytes = 4321,
                MaxInlineBytes = 765,
                HandleTtl = TimeSpan.FromMinutes(2),
                MaxStoredBytesPerConnector = 888,
                MaxUploadsPerMinute = 9,
                AllowedMimeTypes = ["image/png", "image/gif"],
            },
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

            Assert.True(bound.Connectors.Media.Enabled);
            Assert.Equal(2, bound.Connectors.Media.MaxAttachmentsPerMessage);
            Assert.Equal(4321, bound.Connectors.Media.MaxAttachmentBytes);
            Assert.Equal(765, bound.Connectors.Media.MaxInlineBytes);
            Assert.Equal(TimeSpan.FromMinutes(2), bound.Connectors.Media.HandleTtl);
            Assert.Equal(888, bound.Connectors.Media.MaxStoredBytesPerConnector);
            Assert.Equal(9, bound.Connectors.Media.MaxUploadsPerMinute);
            Assert.Equal(["image/png", "image/gif"], bound.Connectors.Media.AllowedMimeTypes);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
