using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public class McpServerConnectionAllowListTests
{
    private sealed class TestConnection : McpServerConnectionBase
    {
        public TestConnection(IReadOnlyCollection<string> allowList, IReadOnlyCollection<string>? mutationAllowList = null)
            : base("srv", allowList, mutationAllowList ?? [], NullLogger.Instance)
        {
        }

        protected override IClientTransport CreateTransport() => throw new NotSupportedException();
    }

    private static McpToolInvocation Invocation(string toolName) => new()
    {
        InvocationId = Guid.CreateVersion7().ToString("N"),
        ServerKey = "srv",
        ToolName = toolName,
        ArgumentsJson = "{}",
    };

    [Fact]
    public async Task CallToolAsync_ToolNotInAllowList_RejectedBeforeDispatch()
    {
        // The allow-list is a security boundary: a tool the user excluded must be refused at invoke
        // time, not merely hidden from the catalog. The check runs before any connection is touched.
        var connection = new TestConnection(["allowed_tool"]);
        var invocation = Invocation("blocked_tool");

        var result = await connection.CallToolAsync(invocation, CancellationToken.None);

        Assert.Equal(McpToolOutcome.Failed, result.Outcome);
        Assert.Equal(McpFailureKind.Policy, result.FailureKind);
        Assert.Equal(invocation.InvocationId, result.InvocationId);
        Assert.Contains("not permitted", result.Error);
    }

    [Fact]
    public async Task CallToolAsync_EmptyAllowList_NotBlocked_FallsThroughToConnectionCheck()
    {
        var connection = new TestConnection([]); // empty allow-list = all tools allowed

        var result = await connection.CallToolAsync(Invocation("any_tool"), CancellationToken.None);

        Assert.Equal(McpToolOutcome.Failed, result.Outcome);
        Assert.Equal(McpFailureKind.Unavailable, result.FailureKind);
        Assert.Contains("not connected", result.Error); // not the allow-list rejection
    }

    [Fact]
    public async Task CallToolAsync_MutationClassifiedTool_RefusedOnDirectPath()
    {
        // SECURITY: a mutation-classified tool must NEVER execute through the direct path, even
        // when it IS exposed in the tool allow-list. Executing it requires the approval flow,
        // which binds a human approval to the exact canonical arguments.
        var connection = new TestConnection(["write_tool"], ["write_tool"]);
        var invocation = Invocation("write_tool");

        var result = await connection.CallToolAsync(invocation, CancellationToken.None);

        Assert.Equal(McpToolOutcome.Failed, result.Outcome);
        Assert.Equal(McpFailureKind.Policy, result.FailureKind);
        Assert.Equal(invocation.InvocationId, result.InvocationId);
        Assert.Contains("approval", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallToolAsync_MutationClassification_RecheckedAtDispatch_EvenWithOpenAllowList()
    {
        // The mutation re-check is a dispatch-time policy boundary of its own — it must fire
        // before any connection is touched, independent of the exposure allow-list state.
        var connection = new TestConnection([], ["write_tool"]);

        var result = await connection.CallToolAsync(Invocation("write_tool"), CancellationToken.None);

        Assert.Equal(McpToolOutcome.Failed, result.Outcome);
        Assert.Equal(McpFailureKind.Policy, result.FailureKind);
        Assert.Contains("approval", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallToolAsync_MutationNeverInferredFromToolName_UnclassifiedToolFallsThrough()
    {
        // "delete_everything" SOUNDS destructive, but classification is EXPLICIT admin policy
        // only — never inferred from names/descriptions/annotations. With an empty mutation
        // list it falls through to the connection check instead of a mutation refusal.
        var connection = new TestConnection([], []);

        var result = await connection.CallToolAsync(Invocation("delete_everything"), CancellationToken.None);

        Assert.Equal(McpFailureKind.Unavailable, result.FailureKind);
        Assert.Contains("not connected", result.Error);
    }
}
