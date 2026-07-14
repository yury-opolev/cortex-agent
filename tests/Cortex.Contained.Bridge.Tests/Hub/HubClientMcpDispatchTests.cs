using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Hub;

/// <summary>
/// Unit tests for <see cref="HubClient"/>'s MCP dispatch wiring: every agent-initiated invocation is
/// registered with the <see cref="Cortex.Contained.Bridge.Mcp.McpInvocationTracker"/> BEFORE it is
/// dispatched and completed in a <c>finally</c>; a duplicate active id is rejected before a second
/// execution; and the connection-generation guard keeps a late Closed/Reconnected event from an
/// already-replaced connection from cancelling the fresh generation's in-flight invocations.
/// </summary>
public sealed class HubClientMcpDispatchTests
{
    private static HubClient CreateClient() => new(NullLogger<HubClient>.Instance);

    private static McpToolInvocation Invocation(string invocationId) => new()
    {
        InvocationId = invocationId,
        ServerKey = "github",
        ToolName = "create_issue",
        ArgumentsJson = "{}",
    };

    [Fact]
    public async Task Dispatch_Success_RegistersBeforeHandlerAndCompletesInFinally()
    {
        await using var client = CreateClient();

        var activeCountInsideHandler = -1;
        client.OnInvokeMcpTool += (invocation, _) =>
        {
            // The invocation must already be tracked by the time the handler runs — registration
            // happens BEFORE dispatch, not after.
            activeCountInsideHandler = client.McpInvocationTracker.ActiveCount;
            return Task.FromResult(McpToolResult.Ok(invocation.InvocationId, "done"));
        };

        var result = await client.DispatchMcpInvocationAsync(Invocation("inv-1"));

        Assert.Equal(McpToolOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, activeCountInsideHandler);

        // Completed in the finally: nothing is left in flight.
        Assert.Equal(0, client.McpInvocationTracker.ActiveCount);
    }

    [Fact]
    public async Task Dispatch_HandlerThrows_StillCompletesInFinally()
    {
        await using var client = CreateClient();

        client.OnInvokeMcpTool += (_, _) => throw new InvalidOperationException("handler blew up");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DispatchMcpInvocationAsync(Invocation("inv-1")));

        // The finally runs even on the throw path — the id must never leak.
        Assert.Equal(0, client.McpInvocationTracker.ActiveCount);
    }

    [Fact]
    public async Task Dispatch_NoHandlerRegistered_ReturnsUnavailable()
    {
        await using var client = CreateClient();

        var result = await client.DispatchMcpInvocationAsync(Invocation("inv-1"));

        Assert.Equal(McpToolOutcome.Failed, result.Outcome);
        Assert.Equal(McpFailureKind.Unavailable, result.FailureKind);
        Assert.Equal(0, client.McpInvocationTracker.ActiveCount);
    }

    [Fact]
    public async Task Dispatch_DuplicateActiveId_RejectedAsValidationFailure_WithoutSecondExecution()
    {
        await using var client = CreateClient();

        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<McpToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCalls = 0;

        client.OnInvokeMcpTool += (_, _) =>
        {
            Interlocked.Increment(ref handlerCalls);
            handlerEntered.TrySetResult();
            return release.Task;
        };

        // First dispatch stays in flight (handler awaits release), so the id remains registered.
        var first = client.DispatchMcpInvocationAsync(Invocation("dup"));
        await handlerEntered.Task;
        Assert.Equal(1, client.McpInvocationTracker.ActiveCount);

        // A duplicate of the ACTIVE id is rejected at the HubClient level — definitive
        // Failed/Validation — and the handler is NOT executed a second time.
        var duplicate = await client.DispatchMcpInvocationAsync(Invocation("dup"));
        Assert.Equal(McpToolOutcome.Failed, duplicate.Outcome);
        Assert.Equal(McpFailureKind.Validation, duplicate.FailureKind);
        Assert.Equal(1, handlerCalls);

        // Let the first finish and confirm it completed normally.
        release.TrySetResult(McpToolResult.Ok("dup", "done"));
        var firstResult = await first;
        Assert.Equal(McpToolOutcome.Succeeded, firstResult.Outcome);
        Assert.Equal(0, client.McpInvocationTracker.ActiveCount);
    }

    [Fact]
    public async Task GenerationGuard_LateCloseFromOldGeneration_DoesNotCancelNewGenerationInvocation()
    {
        await using var client = CreateClient();

        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<McpToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;

        client.OnInvokeMcpTool += (_, token) =>
        {
            observedToken = token;
            handlerEntered.TrySetResult();
            return release.Task;
        };

        // Two connection generations open in sequence (old is superseded by new).
        var oldGeneration = client.BeginMcpGeneration();
        var newGeneration = client.BeginMcpGeneration();
        Assert.NotEqual(oldGeneration, newGeneration);

        // An invocation is now in flight under the CURRENT (new) generation.
        var dispatch = client.DispatchMcpInvocationAsync(Invocation("inv-new"));
        await handlerEntered.Task;
        Assert.Equal(1, client.McpInvocationTracker.ActiveCount);

        // A late Closed event from the OLD, already-replaced generation must NOT cancel the new
        // generation's invocation. THIS is the assertion that fails if the guard comparison
        // (capturedGeneration == current) is inverted to '!='.
        client.CancelMcpInvocationsForGeneration(oldGeneration, "hub connection closed");
        Assert.False(observedToken.IsCancellationRequested);
        Assert.Equal(1, client.McpInvocationTracker.ActiveCount);

        // The CURRENT generation's Closed event does cancel it — proving the guard still fires when
        // the generation matches (so the test can't pass by never cancelling at all).
        client.CancelMcpInvocationsForGeneration(newGeneration, "hub connection closed");
        Assert.True(observedToken.IsCancellationRequested);

        release.TrySetResult(McpToolResult.Ok("inv-new", "done"));
        await dispatch;
        Assert.Equal(0, client.McpInvocationTracker.ActiveCount);
    }

    [Fact]
    public async Task ActionStatusDispatch_RoutesToHandler()
    {
        await using var client = CreateClient();
        client.OnGetMcpActionStatus += (request, _) => Task.FromResult(new McpActionStatusResponse
        {
            Found = true,
            ActionId = request.ActionId,
            Status = "approved",
        });

        var response = await client.DispatchMcpActionStatusAsync(new McpActionStatusRequest { ActionId = "act-1" });

        Assert.True(response.Found);
        Assert.Equal("act-1", response.ActionId);
        Assert.Equal("approved", response.Status);
    }

    [Fact]
    public async Task ActionStatusDispatch_NoHandler_ReturnsNotFound()
    {
        await using var client = CreateClient();

        var response = await client.DispatchMcpActionStatusAsync(new McpActionStatusRequest { ActionId = "act-1" });

        Assert.False(response.Found);
        Assert.NotNull(response.Error);
    }

    [Fact]
    public async Task ActionCancelDispatch_RoutesToHandler_WithExactHash()
    {
        await using var client = CreateClient();
        McpActionCancelRequest? received = null;
        client.OnCancelMcpAction += (request, _) =>
        {
            received = request;
            return Task.FromResult(new McpActionCancelResponse { Accepted = true, Status = "cancelled" });
        };

        var response = await client.DispatchMcpActionCancelAsync(
            new McpActionCancelRequest { ActionId = "act-1", ArgumentsHash = "sha256:abc" });

        Assert.True(response.Accepted);
        Assert.NotNull(received);
        Assert.Equal("sha256:abc", received.ArgumentsHash);
    }

    [Fact]
    public async Task ActionCancelDispatch_NoHandler_ReturnsNotAccepted()
    {
        await using var client = CreateClient();

        var response = await client.DispatchMcpActionCancelAsync(
            new McpActionCancelRequest { ActionId = "act-1", ArgumentsHash = "sha256:abc" });

        Assert.False(response.Accepted);
        Assert.NotNull(response.Error);
    }
}
