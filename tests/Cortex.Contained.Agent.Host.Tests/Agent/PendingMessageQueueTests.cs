using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tests.Agent;

public sealed class PendingMessageQueueTests
{
    // ── Enqueue + DrainAll ────────────────────────────────────────────────

    [Fact]
    public void DrainAll_ReturnsMessagesInOrder_AndEmptiesQueue()
    {
        using var queue = new PendingMessageQueue();
        queue.Enqueue(Message("a"));
        queue.Enqueue(Message("b"));
        queue.Enqueue(Message("c"));

        var drained = queue.DrainAll();

        Assert.Equal(3, drained.Count);
        Assert.Equal("a", drained[0].Text);
        Assert.Equal("b", drained[1].Text);
        Assert.Equal("c", drained[2].Text);

        Assert.Equal(0, queue.Count);
        Assert.Empty(queue.DrainAll());
    }

    [Fact]
    public void DrainAll_ReturnsEmpty_WhenQueueEmpty()
    {
        using var queue = new PendingMessageQueue();
        Assert.Empty(queue.DrainAll());
    }

    // ── Count ─────────────────────────────────────────────────────────────

    [Fact]
    public void Count_ReflectsQueueSize()
    {
        using var queue = new PendingMessageQueue();
        Assert.Equal(0, queue.Count);

        queue.Enqueue(Message("x"));
        queue.Enqueue(Message("y"));
        Assert.Equal(2, queue.Count);

        queue.DrainAll();
        Assert.Equal(0, queue.Count);
    }

    // ── WaitAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WaitAsync_CompletesImmediately_WhenMessageAlreadyEnqueued()
    {
        using var queue = new PendingMessageQueue();
        queue.Enqueue(Message("ready"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await queue.WaitAsync(cts.Token); // must not throw
    }

    [Fact]
    public async Task WaitAsync_UnblocksAfterEnqueue()
    {
        using var queue = new PendingMessageQueue();
        var unblocked = false;

        var waitTask = Task.Run(async () =>
        {
            await queue.WaitAsync(CancellationToken.None);
            unblocked = true;
        });

        await Task.Delay(50);
        Assert.False(unblocked);

        queue.Enqueue(Message("go"));
        await waitTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(unblocked);
    }

    [Fact]
    public async Task WaitAsync_ThrowsOperationCancelled_WhenAlreadyCancelled()
    {
        using var queue = new PendingMessageQueue();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task WaitAsync_ThrowsOperationCancelled_WhenCancelledDuringWait()
    {
        using var queue = new PendingMessageQueue();
        using var cts = new CancellationTokenSource();

        var waitTask = queue.WaitAsync(cts.Token);
        await Task.Delay(50);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static AgentMessage Message(string text) => new()
    {
        ConversationId = "conv-1",
        ChannelId = "ch-1",
        Text = text,
        Source = AgentMessageSource.User,
    };
}
