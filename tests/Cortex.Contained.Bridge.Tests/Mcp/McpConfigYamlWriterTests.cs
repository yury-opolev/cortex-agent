using System.Text;
using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Config.Yaml;
using Microsoft.Extensions.Configuration;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpConfigYamlWriterTests
{
    private static string Write(McpSettingsConfig mcp)
    {
        var sb = new StringBuilder();
        McpConfigYamlWriter.AppendMcpSection(sb, mcp);
        return sb.ToString();
    }

    [Fact]
    public void AppendMcpSection_WritesMasterEnabledAndHeader()
    {
        var yaml = Write(new McpSettingsConfig { Enabled = false });

        Assert.Contains("\nmcp:", yaml, StringComparison.Ordinal);
        Assert.Contains("enabled: false", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendMcpSection_WritesSecretRefIdButNeverSecretValue()
    {
        var mcp = new McpSettingsConfig
        {
            Servers =
            [
                new McpServerConfig
                {
                    Key = "github",
                    Transport = McpTransport.Http,
                    Url = "https://api.example.com/mcp/",
                    Auth = McpAuthMode.ApiKey,
                    ApiKeyHeader = "Authorization",
                    SecretRef = "mcp/github/apikey",
                    ToolAllowList = ["create_issue"],
                },
            ],
        };

        var yaml = Write(mcp);

        Assert.Contains("secretRef: \"mcp/github/apikey\"", yaml, StringComparison.Ordinal);
        Assert.Contains("auth: apiKey", yaml, StringComparison.Ordinal);
        Assert.Contains("create_issue", yaml, StringComparison.Ordinal);
        // The DTO carries no secret value field, so no plaintext key can ever leak here.
        Assert.DoesNotContain("pat", yaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendMcpSection_RoundTripsThroughConfigBinder()
    {
        var mcp = new McpSettingsConfig
        {
            Enabled = true,
            Servers =
            [
                new McpServerConfig
                {
                    Key = "filesystem",
                    Enabled = true,
                    Transport = McpTransport.Stdio,
                    Command = "npx",
                    Args = ["-y", "@modelcontextprotocol/server-filesystem", "/app/shared"],
                    Env = new Dictionary<string, string> { ["SOME_TOKEN"] = "${secret:mcp/fs/token}" },
                    Auth = McpAuthMode.None,
                },
                new McpServerConfig
                {
                    Key = "github",
                    Transport = McpTransport.Http,
                    Url = "https://api.example.com/mcp/",
                    Auth = McpAuthMode.ApiKey,
                    SecretRef = "mcp/github/apikey",
                    ToolAllowList = ["create_issue", "list_prs"],
                },
            ],
        };

        var temp = Path.Combine(Path.GetTempPath(), $"mcp-cfg-{Guid.NewGuid():N}.yml");
        File.WriteAllText(temp, "agentHubUrl: http://127.0.0.1:5100/hub/agent\n" + Write(mcp));
        try
        {
            var configuration = new ConfigurationBuilder().AddYamlFile(temp).Build();
            var bound = new BridgeConfig();
            configuration.Bind(bound);

            Assert.True(bound.Mcp.Enabled);
            Assert.Equal(2, bound.Mcp.Servers.Count);

            var fs = bound.Mcp.Servers.Single(s => s.Key == "filesystem");
            Assert.Equal(McpTransport.Stdio, fs.Transport);
            Assert.Equal("npx", fs.Command);
            Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "/app/shared"], fs.Args);
            Assert.Equal("${secret:mcp/fs/token}", fs.Env["SOME_TOKEN"]);

            var gh = bound.Mcp.Servers.Single(s => s.Key == "github");
            Assert.Equal(McpTransport.Http, gh.Transport);
            Assert.Equal(McpAuthMode.ApiKey, gh.Auth);
            Assert.Equal("https://api.example.com/mcp/", gh.Url);
            Assert.Equal("mcp/github/apikey", gh.SecretRef);
            Assert.Equal(["create_issue", "list_prs"], gh.ToolAllowList);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
