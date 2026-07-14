using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Mcp;

/// <summary>
/// Real-SDK integration test for <see cref="StdioMcpServerConnection"/>: spawns the in-repo Node
/// MCP server (<c>fake-mcp-server.mjs</c>) and drives a real handshake → list → call → result,
/// including env-secret injection, allow-list filtering, fatal transport closure, and in-flight
/// cancellation. Skipped (passes trivially) when Node is not on PATH so it never produces a
/// false failure on a Node-less machine.
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

    private static McpToolInvocation Invocation(string toolName, string argumentsJson = "{}") => new()
    {
        InvocationId = Guid.CreateVersion7().ToString("N"),
        ServerKey = "fake",
        ToolName = toolName,
        ArgumentsJson = argumentsJson,
    };

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
            toolAllowList: ["echo", "reveal_env"], // hidden_tool/die/hang excluded
            logger: NullLogger<StdioMcpServerConnection>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await connection.ConnectAsync(cts.Token);

        Assert.Equal(McpServerStatus.Connected, connection.Status);

        // Allow-list applied: only echo + reveal_env, namespaced.
        var names = connection.Tools.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(["mcp__fake__echo", "mcp__fake__reveal_env"], names);
        Assert.DoesNotContain(connection.Tools, t => t.ToolName == "hidden_tool");

        // Real tools/call round-trip, with the invocation ID preserved on the result.
        var echoInvocation = Invocation("echo", """{"text":"hello mcp"}""");
        var echo = await connection.CallToolAsync(echoInvocation, cts.Token);
        Assert.Equal(McpToolOutcome.Succeeded, echo.Outcome);
        Assert.Equal(echoInvocation.InvocationId, echo.InvocationId);
        Assert.Equal("hello mcp", echo.Content);

        // Env-secret reaches the spawned process.
        var secret = await connection.CallToolAsync(Invocation("reveal_env"), cts.Token);
        Assert.False(secret.IsError);
        Assert.Equal("s3cr3t-value", secret.Content);
    }

    [Fact]
    public async Task CallTool_WhenArgumentsAreMalformed_ReturnsDefinitiveValidationFailure()
    {
        var node = FindNode();
        if (node is null)
        {
            return;
        }

        await using var connection = new StdioMcpServerConnection(
            serverKey: "fake",
            command: node,
            arguments: [ScriptPath()],
            environment: new Dictionary<string, string>(),
            toolAllowList: [],
            logger: NullLogger<StdioMcpServerConnection>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await connection.ConnectAsync(cts.Token);
        Assert.Equal(McpServerStatus.Connected, connection.Status);

        // Malformed arguments JSON fails BEFORE dispatch — a definitive validation failure. The
        // agent must only ever see the generic, secret-free message — never the raw exception text.
        var result = await connection.CallToolAsync(Invocation("echo", "{ this is not json"), cts.Token);

        Assert.Equal(McpToolOutcome.Failed, result.Outcome);
        Assert.Equal(McpFailureKind.Validation, result.FailureKind);
        Assert.Equal(McpErrorSanitizer.ToolFailure("fake", "echo"), result.Error);
        Assert.DoesNotContain("json", result.Error, StringComparison.OrdinalIgnoreCase);

        // Pre-dispatch failures never poison the connection.
        Assert.Equal(McpServerStatus.Connected, connection.Status);
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

        var result = await connection.CallToolAsync(Invocation("echo"), CancellationToken.None);

        Assert.Equal(McpToolOutcome.Failed, result.Outcome);
        Assert.Equal(McpFailureKind.Unavailable, result.FailureKind);
        Assert.Contains("not connected", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransportClosure_MarksConnectionErrorAndDropsTools()
    {
        var node = FindNode();
        if (node is null)
        {
            return;
        }

        await using var connection = new StdioMcpServerConnection(
            serverKey: "fake",
            command: node,
            arguments: [ScriptPath()],
            environment: new Dictionary<string, string>(),
            toolAllowList: [],
            logger: NullLogger<StdioMcpServerConnection>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await connection.ConnectAsync(cts.Token);
        Assert.Equal(McpServerStatus.Connected, connection.Status);
        Assert.NotEmpty(connection.Tools);

        // 'die' kills the server process without replying: dispatch started, no answer will come.
        var result = await connection.CallToolAsync(Invocation("die"), cts.Token);

        // Ambiguous by definition — the call may have executed before the crash.
        Assert.Equal(McpToolOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(McpFailureKind.Transport, result.FailureKind);

        // The dead connection is unusable: client/tools cleared, status Error.
        Assert.Equal(McpServerStatus.Error, connection.Status);
        Assert.Empty(connection.Tools);

        // Follow-up calls fail definitively (nothing is dispatched to a dead transport).
        var after = await connection.CallToolAsync(Invocation("echo", """{"text":"x"}"""), cts.Token);
        Assert.Equal(McpToolOutcome.Failed, after.Outcome);
        Assert.Equal(McpFailureKind.Unavailable, after.FailureKind);
    }

    [Fact]
    public async Task CallTool_WhenServerReturnsJsonRpcError_ReturnsDefinitiveToolFailure_WithoutKillingConnection()
    {
        var node = FindNode();
        if (node is null)
        {
            return;
        }

        await using var connection = new StdioMcpServerConnection(
            serverKey: "fake",
            command: node,
            arguments: [ScriptPath()],
            environment: new Dictionary<string, string>(),
            toolAllowList: [],
            logger: NullLogger<StdioMcpServerConnection>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await connection.ConnectAsync(cts.Token);
        Assert.Equal(McpServerStatus.Connected, connection.Status);

        // 'protocol_error' makes the server reply with a JSON-RPC error RESPONSE; the SDK surfaces
        // that as an McpProtocolException. The request reached the server and was rejected at the
        // protocol layer BEFORE the tool ran, so the side effect definitively did NOT occur — a
        // DEFINITIVE Failed/Tool, NOT an ambiguous OutcomeUnknown.
        var result = await connection.CallToolAsync(Invocation("protocol_error"), cts.Token);

        Assert.Equal(McpToolOutcome.Failed, result.Outcome);
        Assert.Equal(McpFailureKind.Tool, result.FailureKind);
        Assert.Equal(McpErrorSanitizer.ToolFailure("fake", "protocol_error"), result.Error);

        // A protocol rejection is not a transport failure: the connection survives, tools intact.
        Assert.Equal(McpServerStatus.Connected, connection.Status);
        Assert.NotEmpty(connection.Tools);
        var echo = await connection.CallToolAsync(Invocation("echo", """{"text":"still alive"}"""), cts.Token);
        Assert.Equal(McpToolOutcome.Succeeded, echo.Outcome);
        Assert.Equal("still alive", echo.Content);
    }

    [Fact]
    public async Task Call_ExceedsConfiguredTimeout_ReturnsOutcomeUnknown()
    {
        var node = FindNode();
        if (node is null)
        {
            return;
        }

        // A very short configured CallTimeoutSeconds must bound the call even with NO external
        // caller cancellation at all — the Bridge's own per-server bound is what fires here.
        await using var connection = new StdioMcpServerConnection(
            serverKey: "fake",
            command: node,
            arguments: [ScriptPath()],
            environment: new Dictionary<string, string>(),
            toolAllowList: [],
            logger: NullLogger<StdioMcpServerConnection>.Instance,
            callTimeoutSeconds: 1);

        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await connection.ConnectAsync(connectCts.Token);
        Assert.Equal(McpServerStatus.Connected, connection.Status);

        // 'hang' never replies. No external cancellation is supplied — only the connection's own
        // configured 1s timeout can end this call.
        var result = await connection.CallToolAsync(Invocation("hang"), CancellationToken.None);

        Assert.Equal(McpToolOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(McpFailureKind.Timeout, result.FailureKind);

        // The server process is still alive (only this invocation was bounded away); the
        // connection survives and later calls succeed.
        Assert.Equal(McpServerStatus.Connected, connection.Status);
        var echo = await connection.CallToolAsync(Invocation("echo", """{"text":"still alive"}"""), connectCts.Token);
        Assert.Equal(McpToolOutcome.Succeeded, echo.Outcome);
        Assert.Equal("still alive", echo.Content);
    }

    [Fact]
    public async Task CallTool_CancelledMidCall_ReturnsOutcomeUnknown_WithoutKillingConnection()
    {
        var node = FindNode();
        if (node is null)
        {
            return;
        }

        await using var connection = new StdioMcpServerConnection(
            serverKey: "fake",
            command: node,
            arguments: [ScriptPath()],
            environment: new Dictionary<string, string>(),
            toolAllowList: [],
            logger: NullLogger<StdioMcpServerConnection>.Instance);

        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await connection.ConnectAsync(connectCts.Token);
        Assert.Equal(McpServerStatus.Connected, connection.Status);

        // 'hang' never replies; the caller's token fires mid-call. That is a real end-to-end
        // cancellation of an in-flight MCP call — and its outcome is unknown, never "succeeded".
        using var callCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var result = await connection.CallToolAsync(Invocation("hang"), callCts.Token);

        Assert.Equal(McpToolOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(McpFailureKind.Cancellation, result.FailureKind);

        // The server is still alive: the connection survives and later calls succeed.
        Assert.Equal(McpServerStatus.Connected, connection.Status);
        var echo = await connection.CallToolAsync(Invocation("echo", """{"text":"still alive"}"""), connectCts.Token);
        Assert.Equal(McpToolOutcome.Succeeded, echo.Outcome);
        Assert.Equal("still alive", echo.Content);
    }
}
