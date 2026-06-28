using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Bridge.Mcp.Auth;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Mcp;

/// <summary>
/// End-to-end test of the Bridge MCP host stack — real <see cref="McpStaticAuth"/> →
/// <see cref="McpServerConnectionFactory"/> → <see cref="StdioMcpServerConnection"/> (real SDK,
/// real Node MCP server) → <see cref="McpHostService"/> aggregation + invoke routing. Proves a
/// DPAPI-resolved secret reaches the spawned process. Only the SignalR hop to the agent is left to
/// the manual check (it needs a live hub). Skipped when Node is unavailable.
/// </summary>
public sealed class McpHostEndToEndIntegrationTests
{
    private static string? FindNode()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var exeNames = OperatingSystem.IsWindows() ? new[] { "node.exe", "node.cmd" } : ["node"];
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var exe in exeNames)
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string ScriptPath()
        => Path.Combine(AppContext.BaseDirectory, "Mcp", "Fixtures", "fake-mcp-server.mjs");

    [Fact]
    public async Task ReconcileInvoke_RealStack_RoundTripsWithSecretAndMasterGate()
    {
        var node = FindNode();
        if (node is null)
        {
            return; // see class summary — documents the manual check.
        }

        var secretResolver = Substitute.For<IMcpSecretResolver>();
        secretResolver.GetSecret("mcp/test/token").Returns("dpapi-secret");

        var authManager = new McpStaticAuth(secretResolver, NullLogger<McpStaticAuth>.Instance);
        var factory = new McpServerConnectionFactory(
            authManager, NullLoggerFactory.Instance, NullLogger<McpServerConnectionFactory>.Instance);
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance);

        var settings = new McpSettingsConfig
        {
            Enabled = true,
            Servers =
            [
                new McpServerConfig
                {
                    Key = "fake",
                    Enabled = true,
                    Transport = McpTransport.Stdio,
                    Command = node,
                    Args = [ScriptPath()],
                    Env = new Dictionary<string, string> { ["MCP_TEST_SECRET"] = "${secret:mcp/test/token}" },
                    ToolAllowList = ["echo", "reveal_env"],
                },
            ],
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.ReconcileAsync(settings, cts.Token);

        var names = host.CurrentCatalog.Tools.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(["mcp__fake__echo", "mcp__fake__reveal_env"], names);

        var echo = await host.InvokeAsync("fake", "echo", """{"text":"round trip"}""", cts.Token);
        Assert.False(echo.IsError);
        Assert.Equal("round trip", echo.Content);

        // The DPAPI secret flowed config → McpStaticAuth → env → spawned process.
        var secret = await host.InvokeAsync("fake", "reveal_env", "{}", cts.Token);
        Assert.False(secret.IsError);
        Assert.Equal("dpapi-secret", secret.Content);

        // Master kill-switch removes everything live.
        await host.ReconcileAsync(new McpSettingsConfig { Enabled = false, Servers = settings.Servers }, cts.Token);
        Assert.Empty(host.CurrentCatalog.Tools);
        var afterKill = await host.InvokeAsync("fake", "echo", "{}", cts.Token);
        Assert.True(afterKill.IsError);
    }
}
