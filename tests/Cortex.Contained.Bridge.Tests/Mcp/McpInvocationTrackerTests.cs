using Cortex.Contained.Bridge.Mcp;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpInvocationTrackerTests
{
    [Fact]
    public void Register_DuplicateActiveId_IsRejected()
    {
        using var tracker = new McpInvocationTracker();

        Assert.True(tracker.TryRegister("inv-1", CancellationToken.None, out _));
        Assert.False(tracker.TryRegister("inv-1", CancellationToken.None, out _));

        // Once the first registration completes, the ID may be registered again
        // (relevant for at-most-once bookkeeping, not for replays — which never happen).
        tracker.Complete("inv-1");
        Assert.True(tracker.TryRegister("inv-1", CancellationToken.None, out _));
    }

    [Fact]
    public void Cancel_CancelsOnlyMatchingInvocation()
    {
        using var tracker = new McpInvocationTracker();
        Assert.True(tracker.TryRegister("inv-a", CancellationToken.None, out var tokenA));
        Assert.True(tracker.TryRegister("inv-b", CancellationToken.None, out var tokenB));

        Assert.True(tracker.Cancel("inv-a", "test"));

        Assert.True(tokenA.IsCancellationRequested);
        Assert.False(tokenB.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_UnknownId_ReturnsFalse()
    {
        using var tracker = new McpInvocationTracker();

        Assert.False(tracker.Cancel("missing", null));
    }

    [Fact]
    public void ConnectionClose_CancelsAllInvocations()
    {
        // On connection close/reconnect/replacement the HubClient calls CancelAll:
        // every outstanding invocation's token must fire.
        using var tracker = new McpInvocationTracker();
        Assert.True(tracker.TryRegister("inv-a", CancellationToken.None, out var tokenA));
        Assert.True(tracker.TryRegister("inv-b", CancellationToken.None, out var tokenB));

        var cancelled = tracker.CancelAll("connection closed");

        Assert.Equal(2, cancelled);
        Assert.True(tokenA.IsCancellationRequested);
        Assert.True(tokenB.IsCancellationRequested);
    }

    [Fact]
    public void TryRegister_LinksExternalToken()
    {
        using var tracker = new McpInvocationTracker();
        using var external = new CancellationTokenSource();
        Assert.True(tracker.TryRegister("inv-a", external.Token, out var token));

        external.Cancel();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Complete_RemovesInvocation_SoLateCancelIsIgnored()
    {
        using var tracker = new McpInvocationTracker();
        Assert.True(tracker.TryRegister("inv-a", CancellationToken.None, out _));
        tracker.Complete("inv-a");

        Assert.False(tracker.Cancel("inv-a", "too late"));
        Assert.Equal(0, tracker.ActiveCount);
    }

    [Fact]
    public void Dispose_CancelsOutstandingInvocations_AndRejectsNewRegistrations()
    {
        var tracker = new McpInvocationTracker();
        Assert.True(tracker.TryRegister("inv-a", CancellationToken.None, out var tokenA));

        tracker.Dispose();

        Assert.True(tokenA.IsCancellationRequested);
        Assert.False(tracker.TryRegister("inv-b", CancellationToken.None, out _));
    }
}
