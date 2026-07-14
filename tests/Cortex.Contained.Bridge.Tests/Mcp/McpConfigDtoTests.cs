using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpConfigDtoTests
{
    [Fact]
    public void McpServerConfig_Defaults_EnabledTrueAndAutoAuthEmptyLists()
    {
        var config = new McpServerConfig();

        Assert.Equal(string.Empty, config.Key);
        Assert.True(config.Enabled);
        Assert.Equal(McpTransport.Stdio, config.Transport);
        Assert.Null(config.Url);
        Assert.Null(config.Command);
        Assert.Empty(config.Args);
        Assert.Empty(config.Env);
        Assert.Equal(McpAuthMode.Auto, config.Auth);
        Assert.Null(config.ApiKeyHeader);
        Assert.Null(config.SecretRef);
        Assert.Empty(config.ToolAllowList);
        Assert.Empty(config.MutationToolAllowList);
    }

    [Fact]
    public void McpServerConfig_MutationToolAllowList_IsIndependentOfToolAllowList()
    {
        // The mutation classification is a SEPARATE explicit admin policy, not derived from the
        // exposure allow-list.
        var config = new McpServerConfig
        {
            ToolAllowList = ["create_issue", "list_prs"],
            MutationToolAllowList = ["create_issue"],
        };

        Assert.Equal(["create_issue", "list_prs"], config.ToolAllowList);
        Assert.Equal(["create_issue"], config.MutationToolAllowList);
    }

    [Fact]
    public void McpSettingsConfig_Defaults_EnabledTrueAndNoServers()
    {
        var config = new McpSettingsConfig();

        Assert.True(config.Enabled);
        Assert.Empty(config.Servers);
    }

    [Fact]
    public void BridgeConfig_Defaults_HasEmptyEnabledMcpSettings()
    {
        var config = new BridgeConfig();

        Assert.NotNull(config.Mcp);
        Assert.True(config.Mcp.Enabled);
        Assert.Empty(config.Mcp.Servers);
    }
}
