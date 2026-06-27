using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tests.Agent;

public class AgentSessionMessageQueueTests
{
    [Fact]
    public void DrainMessages_ReturnsAllEnqueued_InOrder()
    {
        var session = new AgentSession("test-conv");
        var msg1 = CreateMessage("hello");
        var msg2 = CreateMessage("world");

        session.EnqueuePending(msg1);
        session.EnqueuePending(msg2);

        var drained = session.DrainPendingMessages();
        Assert.Equal(2, drained.Count);
        Assert.Equal("hello", drained[0].Text);
        Assert.Equal("world", drained[1].Text);
    }

    [Fact]
    public void DrainMessages_ReturnsEmpty_WhenNothingQueued()
    {
        var session = new AgentSession("test-conv");
        var drained = session.DrainPendingMessages();
        Assert.Empty(drained);
    }

    [Fact]
    public void DrainMessages_ClearsQueue_AfterDrain()
    {
        var session = new AgentSession("test-conv");
        session.EnqueuePending(CreateMessage("hello"));

        session.DrainPendingMessages();
        var second = session.DrainPendingMessages();
        Assert.Empty(second);
    }

    [Fact]
    public async Task WaitForMessagesAsync_ReturnsImmediately_WhenMessageQueued()
    {
        var session = new AgentSession("test-conv");
        session.EnqueuePending(CreateMessage("hello"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await session.WaitForPendingAsync(cts.Token); // should not throw
    }

    [Fact]
    public async Task WaitForMessagesAsync_Blocks_UntilMessageEnqueued()
    {
        var session = new AgentSession("test-conv");
        var waited = false;

        var waitTask = Task.Run(async () =>
        {
            await session.WaitForPendingAsync(CancellationToken.None);
            waited = true;
        });

        await Task.Delay(50);
        Assert.False(waited);

        session.EnqueuePending(CreateMessage("hello"));
        await waitTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(waited);
    }

    [Fact]
    public void PendingMessageCount_ReflectsQueueSize()
    {
        var session = new AgentSession("test-conv");
        Assert.Equal(0, session.PendingMessageCount);

        session.EnqueuePending(CreateMessage("a"));
        session.EnqueuePending(CreateMessage("b"));
        Assert.Equal(2, session.PendingMessageCount);

        session.DrainPendingMessages();
        Assert.Equal(0, session.PendingMessageCount);
    }

    [Fact]
    public async Task WaitForPendingAsync_Cancellation_ThrowsOperationCancelled()
    {
        var session = new AgentSession("test-conv");
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // already cancelled

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.WaitForPendingAsync(cts.Token));
    }

    [Fact]
    public async Task WaitForPendingAsync_CancelledDuringWait_ThrowsOperationCancelled()
    {
        var session = new AgentSession("test-conv");
        using var cts = new CancellationTokenSource();

        var waitTask = session.WaitForPendingAsync(cts.Token);
        await Task.Delay(50);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);
    }

    [Fact]
    public async Task ConcurrentEnqueue_AllMessagesPreserved()
    {
        var session = new AgentSession("test-conv");
        var messageCount = 100;
        var tasks = new Task[messageCount];

        for (var i = 0; i < messageCount; i++)
        {
            var index = i;
            tasks[i] = Task.Run(() => session.EnqueuePending(CreateMessage($"msg-{index}")));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(messageCount, session.PendingMessageCount);
        var drained = session.DrainPendingMessages();
        Assert.Equal(messageCount, drained.Count);

        // All messages present (order may vary due to concurrency)
        var texts = drained.Select(m => m.Text).OrderBy(t => t).ToList();
        for (var i = 0; i < messageCount; i++)
        {
            Assert.Contains($"msg-{i}", texts);
        }
    }

    [Fact]
    public async Task DrainPendingMessages_WhileEnqueuing_NoLoss()
    {
        var session = new AgentSession("test-conv");
        var enqueued = new System.Collections.Concurrent.ConcurrentBag<string>();
        var drained = new System.Collections.Concurrent.ConcurrentBag<string>();
        var done = false;

        // Producer: enqueue messages rapidly
        var producer = Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                var text = $"msg-{i}";
                session.EnqueuePending(CreateMessage(text));
                enqueued.Add(text);
            }
            done = true;
        });

        // Consumer: drain repeatedly
        var consumer = Task.Run(() =>
        {
            while (!done || session.PendingMessageCount > 0)
            {
                var batch = session.DrainPendingMessages();
                foreach (var msg in batch)
                {
                    drained.Add(msg.Text);
                }
                Thread.SpinWait(100);
            }
        });

        await Task.WhenAll(producer, consumer);

        // Every enqueued message was drained
        Assert.Equal(enqueued.Count, drained.Count);
    }

    private static AgentMessage CreateMessage(string text) => new()
    {
        ConversationId = "test-conv",
        ChannelId = "test-channel",
        Text = text,
        Source = AgentMessageSource.User,
    };
}
