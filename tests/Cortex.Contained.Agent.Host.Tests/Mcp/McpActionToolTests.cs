using System.Text.Json;
using Cortex.Contained.Agent.Host.Mcp;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Mcp;

public class McpActionToolTests
{
    private static readonly ToolExecutionContext Context = new()
    {
        ConversationId = "conv-1",
        ChannelId = "webchat-default",
        CorrelationId = "corr-1",
    };

    private static McpActionStatusTool StatusTool(IMcpGateway gateway) =>
        new(gateway, NullLogger<McpActionStatusTool>.Instance);

    private static McpActionCancelTool CancelTool(IMcpGateway gateway) =>
        new(gateway, NullLogger<McpActionCancelTool>.Instance);

    // ── mcp_action_status ─────────────────────────────────────────────────

    [Fact]
    public void StatusTool_Metadata_NamesAndRequiresActionId()
    {
        var tool = StatusTool(Substitute.For<IMcpGateway>());

        Assert.Equal("mcp_action_status", tool.Name);
        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        Assert.Equal("action_id", schema.RootElement.GetProperty("required")[0].GetString());
    }

    [Fact]
    public async Task StatusTool_MissingActionId_Fails()
    {
        var gateway = Substitute.For<IMcpGateway>();
        var tool = StatusTool(gateway);

        var result = await tool.ExecuteAsync("{}", Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("action_id", result.Error);
        await gateway.DidNotReceiveWithAnyArgs().GetActionStatusAsync(default!, default);
    }

    [Fact]
    public async Task StatusTool_FoundAction_ReturnsStatusJson()
    {
        var gateway = Substitute.For<IMcpGateway>();
        gateway.GetActionStatusAsync("act-1", Arg.Any<CancellationToken>())
            .Returns(new McpActionStatusResponse
            {
                Found = true,
                ActionId = "act-1",
                Status = "outcome_unknown",
                ArgumentsHash = "sha256:abc",
                ServerKey = "github",
                ToolName = "create_issue",
                Error = "transport lost",
            });
        var tool = StatusTool(gateway);

        var result = await tool.ExecuteAsync("""{"action_id":"act-1"}""", Context, CancellationToken.None);

        Assert.True(result.Success);
        using var content = JsonDocument.Parse(result.Content);
        Assert.Equal("act-1", content.RootElement.GetProperty("actionId").GetString());
        Assert.Equal("outcome_unknown", content.RootElement.GetProperty("status").GetString());
        Assert.Equal("sha256:abc", content.RootElement.GetProperty("argumentsHash").GetString());
    }

    [Fact]
    public async Task StatusTool_UnknownAction_Fails()
    {
        var gateway = Substitute.For<IMcpGateway>();
        gateway.GetActionStatusAsync("act-9", Arg.Any<CancellationToken>())
            .Returns(new McpActionStatusResponse { Found = false, Error = "No MCP action 'act-9'." });
        var tool = StatusTool(gateway);

        var result = await tool.ExecuteAsync("""{"action_id":"act-9"}""", Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("act-9", result.Error);
    }

    // ── mcp_action_cancel ─────────────────────────────────────────────────

    [Fact]
    public void CancelTool_Metadata_RequiresActionIdAndHash()
    {
        var tool = CancelTool(Substitute.For<IMcpGateway>());

        Assert.Equal("mcp_action_cancel", tool.Name);
        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("action_id", required);
        Assert.Contains("arguments_hash", required);
    }

    [Fact]
    public async Task CancelTool_MissingHash_Fails_WithoutCallingBridge()
    {
        var gateway = Substitute.For<IMcpGateway>();
        var tool = CancelTool(gateway);

        var result = await tool.ExecuteAsync("""{"action_id":"act-1"}""", Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("arguments_hash", result.Error);
        await gateway.DidNotReceiveWithAnyArgs().CancelActionAsync(default!, default!, default);
    }

    [Fact]
    public async Task CancelTool_Accepted_PassesExactHash_AndReportsStatus()
    {
        var gateway = Substitute.For<IMcpGateway>();
        gateway.CancelActionAsync("act-1", "sha256:abc", Arg.Any<CancellationToken>())
            .Returns(new McpActionCancelResponse { Accepted = true, Status = "cancelled" });
        var tool = CancelTool(gateway);

        var result = await tool.ExecuteAsync(
            """{"action_id":"act-1","arguments_hash":"sha256:abc"}""", Context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("cancelled", result.Content);
        await gateway.Received(1).CancelActionAsync("act-1", "sha256:abc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelTool_DispatchingAction_WarnsOutcomeMayBeUnknown_NeverClaimsCancelled()
    {
        var gateway = Substitute.For<IMcpGateway>();
        gateway.CancelActionAsync("act-1", "sha256:abc", Arg.Any<CancellationToken>())
            .Returns(new McpActionCancelResponse { Accepted = true, Status = "dispatching" });
        var tool = CancelTool(gateway);

        var result = await tool.ExecuteAsync(
            """{"action_id":"act-1","arguments_hash":"sha256:abc"}""", Context, CancellationToken.None);

        // Cancellation after dispatch began must NOT be presented as a completed cancel.
        Assert.True(result.Success);
        Assert.Contains("dispatching", result.Content);
        Assert.Contains("may still have executed", result.Content);
        Assert.DoesNotContain("Current status: cancelled", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelTool_Rejected_Fails()
    {
        var gateway = Substitute.For<IMcpGateway>();
        gateway.CancelActionAsync("act-1", "sha256:stale", Arg.Any<CancellationToken>())
            .Returns(new McpActionCancelResponse { Accepted = false, Error = "arguments_hash_mismatch" });
        var tool = CancelTool(gateway);

        var result = await tool.ExecuteAsync(
            """{"action_id":"act-1","arguments_hash":"sha256:stale"}""", Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("arguments_hash_mismatch", result.Error);
    }
}
