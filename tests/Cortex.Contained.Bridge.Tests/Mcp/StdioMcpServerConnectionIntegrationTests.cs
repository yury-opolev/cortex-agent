using Cortex.Contained.Bridge.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Mcp;

/// <summary>
/// Real-SDK integration test for <see cref="StdioMcpServerConnection"/>: spawns the in-repo Node
/// MCP server (<c>fake-mcp-server.mjs</c>) and drives a real handshake → list → call → result,
/// including env-secret injection and allow-list filtering. Skipped (passes trivially) when Node
/// is not on PATH so it never produces a false failure on a Node-less machine.
/// </summary>
public sealed class StdioMcpServerConnectionIntegrationTests
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
    public async Task ConnectListCall_AgainstRealStdioServer_RoundTripsWithSecretAndAllowList()
    {
        var node = FindNode();
        if (node is null)
        {
            // Node unavailable — see the class summary; this path documents the manual check.
            return;
        }

        Assert.True(File.Exists(ScriptPath()), $"fixture not copied to output: {ScriptPath()}");

        await using var connection = new StdioMcpServerConnection(
            serverKey: "fake",
            command: node,
            arguments: [ScriptPath()],
            environment: new Dictionary<string, string> { ["MCP_TEST_SECRET"] = "s3cr3t-value" },
            toolAllowList: ["echo", "reveal_env"], // hidden_tool excluded
            logger: NullLogger<StdioMcpServerConnection>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await connection.ConnectAsync(cts.Token);

        Assert.Equal(McpServerStatus.Connected, connection.Status);

        // Allow-list applied: only echo + reveal_env, namespaced.
        var names = connection.Tools.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(["mcp__fake__echo", "mcp__fake__reveal_env"], names);
        Assert.DoesNotContain(connection.Tools, t => t.ToolName == "hidden_tool");

        // Real tools/call round-trip.
        var echo = await connection.CallToolAsync("echo", """{"text":"hello mcp"}""", cts.Token);
        Assert.False(echo.IsError);
        Assert.Equal("hello mcp", echo.Content);

        // Env-secret reaches the spawned process.
        var secret = await connection.CallToolAsync("reveal_env", "{}", cts.Token);
        Assert.False(secret.IsError);
        Assert.Equal("s3cr3t-value", secret.Content);
    }

    [Fact]
    public async Task CallTool_WhenNotConnected_ReturnsStructuredFailure()
    {
        await using var connection = new StdioMcpServerConnection(
            serverKey: "fake",
            command: "node",
            arguments: ["does-not-matter.mjs"],
            environment: new Dictionary<string, string>(),
            toolAllowList: [],
            logger: NullLogger<StdioMcpServerConnection>.Instance);

        var result = await connection.CallToolAsync("echo", "{}", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("not connected", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
