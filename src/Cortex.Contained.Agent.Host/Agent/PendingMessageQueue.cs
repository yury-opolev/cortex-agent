using System.Collections.Concurrent;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Thread-safe queue for <see cref="AgentMessage"/> objects awaiting processing by the
/// agent loop. Uses a <see cref="SemaphoreSlim"/> as a lightweight signal so the loop can
/// await new work without polling.
/// </summary>
internal sealed class PendingMessageQueue : IDisposable
{
    private readonly ConcurrentQueue<AgentMessage> queue = new();
    private readonly SemaphoreSlim signal = new(0);

    /// <summary>Number of messages currently in the queue.</summary>
    public int Count => this.queue.Count;

    /// <summary>
    /// Enqueues <paramref name="message"/> and releases the internal semaphore so any
    /// waiter on <see cref="WaitAsync"/> is unblocked.
    /// </summary>
    public void Enqueue(AgentMessage message)
    {
        this.queue.Enqueue(message);
        this.signal.Release();
    }

    /// <summary>
    /// Drains all pending messages in order and returns them. The queue is empty after
    /// this call returns.
    /// </summary>
    public IReadOnlyList<AgentMessage> DrainAll()
    {
        var result = new List<AgentMessage>();
        while (this.queue.TryDequeue(out var message))
        {
            result.Add(message);
        }

        return result;
    }

    /// <summary>
    /// Waits until at least one message has been enqueued, or
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        return this.signal.WaitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.signal.Dispose();
    }
}
