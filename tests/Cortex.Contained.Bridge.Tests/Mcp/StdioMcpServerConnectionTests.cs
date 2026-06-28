using Cortex.Contained.Bridge.Mcp;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public class StdioMcpServerConnectionTests
{
    [Fact]
    public void BuildTransportOptions_DoesNotInheritBridgeEnvironment_AndLayersConfiguredVars()
    {
        // SECURITY: a secret in the Bridge process environment (e.g. CORTEX_HUB_TOKEN) must NOT
        // leak into a spawned third-party MCP server process.
        Environment.SetEnvironmentVariable("CORTEX_HUB_TOKEN", "leak-me");
        try
        {
            var opts = StdioMcpServerConnection.BuildTransportOptions(
                "srv",
                "node",
                ["server.mjs"],
                new Dictionary<string, string> { ["MY_TOKEN"] = "abc" },
                workingDirectory: null);

            Assert.False(opts.InheritEnvironmentVariables);
            Assert.NotNull(opts.EnvironmentVariables);
            Assert.Equal("abc", opts.EnvironmentVariables!["MY_TOKEN"]);
            Assert.False(opts.EnvironmentVariables.ContainsKey("CORTEX_HUB_TOKEN"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CORTEX_HUB_TOKEN", null);
        }
    }
}
