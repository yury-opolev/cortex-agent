using Cortex.Contained.Bridge.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public class McpServerConnectionAllowListTests
{
    private sealed class TestConnection : McpServerConnectionBase
    {
        public TestConnection(IReadOnlyCollection<string> allowList)
            : base("srv", allowList, NullLogger.Instance)
        {
        }

        protected override IClientTransport CreateTransport() => throw new NotSupportedException();
    }

    [Fact]
    public async Task CallToolAsync_ToolNotInAllowList_RejectedBeforeDispatch()
    {
        // The allow-list is a security boundary: a tool the user excluded must be refused at invoke
        // time, not merely hidden from the catalog. The check runs before any connection is touched.
        var connection = new TestConnection(["allowed_tool"]);

        var result = await connection.CallToolAsync("blocked_tool", "{}", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("not permitted", result.Error);
    }

    [Fact]
    public async Task CallToolAsync_EmptyAllowList_NotBlocked_FallsThroughToConnectionCheck()
    {
        var connection = new TestConnection([]); // empty allow-list = all tools allowed

        var result = await connection.CallToolAsync("any_tool", "{}", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("not connected", result.Error); // not the allow-list rejection
    }
}
