using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Mcp;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Mcp;

public class SignalRMcpGatewayTests
{
    private sealed class FakeBridgeClientProvider : IBridgeClientProvider
    {
        public IAgentHubClient? Client { get; set; }
    }

    private static SignalRMcpGateway BuildGateway(IAgentHubClient? client, int timeoutSeconds = 60)
    {
        var provider = new FakeBridgeClientProvider { Client = client };
        var options = new McpGatewayOptions { BridgeInvokeTimeoutSeconds = timeoutSeconds };
        return new SignalRMcpGateway(provider, options, NullLogger<SignalRMcpGateway>.Instance);
    }

    [Fact]
    public async Task InvokeAsync_BridgeConnected_ReturnsBridgeResult()
    {
        var client = Substitute.For<IAgentHubClient>();
        client.InvokeMcpTool(Arg.Any<McpToolInvocation>())
            .Returns(callInfo => McpToolResult.Ok(callInfo.Arg<McpToolInvocation>().InvocationId, "x"));
        var gateway = BuildGateway(client);

        var result = await gateway.InvokeAsync("github", "create_issue", "{}", "conv-1", "webchat-default", "corr-1", CancellationToken.None);

        Assert.Equal(McpToolOutcome.Succeeded, result.Outcome);
        Assert.False(result.IsError);
        Assert.Equal("x", result.Content);
        await client.Received(1).InvokeMcpTool(Arg.Is<McpToolInvocation>(i =>
            i.ServerKey == "github" && i.ToolName == "create_issue" && i.ArgumentsJson == "{}"
            && i.ConversationId == "conv-1" && i.ChannelId == "webchat-default" && i.CorrelationId == "corr-1"));
    }

    [Fact]
    public async Task InvokeAsync_AssignsStableInvocationId()
    {
        var sentInvocations = new List<McpToolInvocation>();
        var client = Substitute.For<IAgentHubClient>();
        client.InvokeMcpTool(Arg.Do<McpToolInvocation>(sentInvocations.Add))
            .Returns(callInfo => McpToolResult.Ok(callInfo.Arg<McpToolInvocation>().InvocationId, "ok"));
        var gateway = BuildGateway(client);

        var first = await gateway.InvokeAsync("github", "create_issue", "{}", null, null, null, CancellationToken.None);
        var second = await gateway.InvokeAsync("github", "create_issue", "{}", null, null, null, CancellationToken.None);

        // One ID generated per dispatch, threaded end to end: the invocation the Bridge
        // received carries the same ID the result reports back to the caller.
        Assert.Equal(2, sentInvocations.Count);
        Assert.Equal(sentInvocations[0].InvocationId, first.InvocationId);
        Assert.Equal(sentInvocations[1].InvocationId, second.InvocationId);
        Assert.NotEqual(first.InvocationId, second.InvocationId);
        Assert.All(sentInvocations, invocation =>
        {
            Assert.Equal(32, invocation.InvocationId.Length);
            Assert.True(Guid.TryParseExact(invocation.InvocationId, "N", out _));
        });
    }

    [Fact]
    public async Task InvokeAsync_CallerCancellation_SendsCancelWithSameId()
    {
        McpToolInvocation? sent = null;
        var client = Substitute.For<IAgentHubClient>();
        client.InvokeMcpTool(Arg.Do<McpToolInvocation>(invocation => sent = invocation))
            .Returns(new TaskCompletionSource<McpToolResult>().Task); // never completes
        client.CancelMcpTool(Arg.Any<McpToolCancellation>()).Returns(Task.CompletedTask);
        var gateway = BuildGateway(client);
        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await gateway.InvokeAsync("github", "create_issue", "{}", "conv-1", null, null, callerCts.Token);

        Assert.NotNull(sent);
        Assert.Equal(McpToolOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(McpFailureKind.Cancellation, result.FailureKind);
        Assert.Equal(sent!.InvocationId, result.InvocationId);
        await client.Received(1).CancelMcpTool(Arg.Is<McpToolCancellation>(c => c.InvocationId == sent.InvocationId));
    }

    [Fact]
    public async Task InvokeAsync_Timeout_ReturnsOutcomeUnknown()
    {
        McpToolInvocation? sent = null;
        var client = Substitute.For<IAgentHubClient>();
        client.InvokeMcpTool(Arg.Do<McpToolInvocation>(invocation => sent = invocation))
            .Returns(new TaskCompletionSource<McpToolResult>().Task); // Bridge never answers
        client.CancelMcpTool(Arg.Any<McpToolCancellation>()).Returns(Task.CompletedTask);
        var gateway = BuildGateway(client, timeoutSeconds: 1);

        var result = await gateway.InvokeAsync("github", "create_issue", "{}", null, null, null, CancellationToken.None);

        // A timeout after dispatch is ambiguous: the call may have executed. It must be
        // OutcomeUnknown (never a definitive Failed) and best-effort cancellation must be sent.
        Assert.NotNull(sent);
        Assert.Equal(McpToolOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(McpFailureKind.Timeout, result.FailureKind);
        Assert.Equal(sent!.InvocationId, result.InvocationId);
        await client.Received(1).CancelMcpTool(Arg.Is<McpToolCancellation>(c => c.InvocationId == sent.InvocationId));
    }

    [Fact]
    public async Task InvokeAsync_BridgeUnavailable_ReturnsDefinitiveFailure()
    {
        var gateway = BuildGateway(client: null);

        var result = await gateway.InvokeAsync("github", "create_issue", "{}", null, null, null, CancellationToken.None);

        // Nothing was dispatched, so this failure is definitive — safe to report as Failed.
        Assert.Equal(McpToolOutcome.Failed, result.Outcome);
        Assert.Equal(McpFailureKind.Unavailable, result.FailureKind);
        Assert.True(result.IsError);
        Assert.NotEmpty(result.InvocationId);
        Assert.Contains("unreachable", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_TransportFaultAfterDispatch_ReturnsOutcomeUnknown_NoThrow()
    {
        var client = Substitute.For<IAgentHubClient>();
        client.InvokeMcpTool(Arg.Any<McpToolInvocation>())
            .Returns<Task<McpToolResult>>(_ => throw new InvalidOperationException("connection dropped"));
        var gateway = BuildGateway(client);

        var result = await gateway.InvokeAsync("github", "create_issue", "{}", null, null, null, CancellationToken.None);

        // The request may have reached the Bridge before the fault: ambiguous, never definitive.
        Assert.Equal(McpToolOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(McpFailureKind.Transport, result.FailureKind);
    }

    [Fact]
    public async Task GetActionStatusAsync_BridgeConnected_ReturnsBridgeResponse()
    {
        var client = Substitute.For<IAgentHubClient>();
        client.GetMcpActionStatus(Arg.Any<McpActionStatusRequest>())
            .Returns(new McpActionStatusResponse { Found = true, ActionId = "act-1", Status = "approved" });
        var gateway = BuildGateway(client);

        var response = await gateway.GetActionStatusAsync("act-1", CancellationToken.None);

        Assert.True(response.Found);
        Assert.Equal("approved", response.Status);
        await client.Received(1).GetMcpActionStatus(Arg.Is<McpActionStatusRequest>(r => r.ActionId == "act-1"));
    }

    [Fact]
    public async Task GetActionStatusAsync_BridgeUnavailable_ReturnsNotFound_NoThrow()
    {
        var gateway = BuildGateway(client: null);

        var response = await gateway.GetActionStatusAsync("act-1", CancellationToken.None);

        Assert.False(response.Found);
        Assert.Contains("unreachable", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetActionStatusAsync_TransportFault_ReturnsNotFound_NoThrow()
    {
        var client = Substitute.For<IAgentHubClient>();
        client.GetMcpActionStatus(Arg.Any<McpActionStatusRequest>())
            .Returns<Task<McpActionStatusResponse>>(_ => throw new InvalidOperationException("connection dropped"));
        var gateway = BuildGateway(client);

        var response = await gateway.GetActionStatusAsync("act-1", CancellationToken.None);

        Assert.False(response.Found);
        Assert.NotNull(response.Error);
    }

    [Fact]
    public async Task CancelActionAsync_BridgeConnected_PassesExactHash()
    {
        var client = Substitute.For<IAgentHubClient>();
        client.CancelMcpAction(Arg.Any<McpActionCancelRequest>())
            .Returns(new McpActionCancelResponse { Accepted = true, Status = "cancelled" });
        var gateway = BuildGateway(client);

        var response = await gateway.CancelActionAsync("act-1", "sha256:abc", CancellationToken.None);

        Assert.True(response.Accepted);
        await client.Received(1).CancelMcpAction(Arg.Is<McpActionCancelRequest>(r =>
            r.ActionId == "act-1" && r.ArgumentsHash == "sha256:abc"));
    }

    [Fact]
    public async Task CancelActionAsync_BridgeUnavailable_ReturnsNotAccepted_NoThrow()
    {
        var gateway = BuildGateway(client: null);

        var response = await gateway.CancelActionAsync("act-1", "sha256:abc", CancellationToken.None);

        Assert.False(response.Accepted);
        Assert.Contains("unreachable", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_CancelledBeforeDispatch_ReturnsDefinitiveCancelled()
    {
        var client = Substitute.For<IAgentHubClient>();
        var gateway = BuildGateway(client);
        using var callerCts = new CancellationTokenSource();
        await callerCts.CancelAsync();

        var result = await gateway.InvokeAsync("github", "create_issue", "{}", null, null, null, callerCts.Token);

        // Nothing left the agent, so this is a definitive cancellation, not an unknown outcome.
        Assert.Equal(McpToolOutcome.Cancelled, result.Outcome);
        Assert.Equal(McpFailureKind.Cancellation, result.FailureKind);
        await client.DidNotReceive().InvokeMcpTool(Arg.Any<McpToolInvocation>());
    }
}
