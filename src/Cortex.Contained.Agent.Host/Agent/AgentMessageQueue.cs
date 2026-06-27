using System.Threading.Channels;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Bounded, thread-safe message channel backed by <see cref="System.Threading.Channels.Channel{T}"/>.
/// Both the SignalR hub and the scheduler write to this channel; a single consumer
/// loop in <see cref="AgentRuntime"/> reads from it and processes messages
/// sequentially per conversation.
/// </summary>
public sealed class AgentMessageChannel
{
    /// <summary>
    /// Default capacity. If the channel is full, writers block until space
    /// is available (backpressure). This prevents unbounded memory growth
    /// if messages arrive faster than the agent can process them.
    /// </summary>
    private const int DefaultCapacity = 100;

    private readonly Channel<AgentMessage> channel;
    private readonly AgentMetrics? metrics;

    public AgentMessageChannel()
        : this(DefaultCapacity, metrics: null)
    {
    }

    public AgentMessageChannel(AgentMetrics? metrics)
        : this(DefaultCapacity, metrics)
    {
    }

    public AgentMessageChannel(int capacity, AgentMetrics? metrics = null)
    {
        this.metrics = metrics;
        this.channel = Channel.CreateBounded<AgentMessage>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false, // Consumer loop is single but we don't restrict
            SingleWriter = false, // Multiple writers (hub + scheduler)
        });
    }

    /// <summary>
    /// Enqueue a message for processing. Waits asynchronously if the channel
    /// is full (backpressure).
    /// </summary>
    public async ValueTask EnqueueAsync(AgentMessage message, CancellationToken cancellationToken = default)
    {
        await this.channel.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        this.metrics?.ObserveInboundQueueDepth(this.channel.Reader.Count);
    }

    /// <summary>
    /// Try to enqueue a message without waiting. Returns false if the channel is full.
    /// Useful for fire-and-forget scenarios where dropping is acceptable.
    /// </summary>
    public bool TryEnqueue(AgentMessage message)
    {
        var written = this.channel.Writer.TryWrite(message);
        if (written)
        {
            this.metrics?.ObserveInboundQueueDepth(this.channel.Reader.Count);
        }

        return written;
    }

    /// <summary>
    /// Returns an <see cref="IAsyncEnumerable{T}"/> that yields messages as they arrive.
    /// Used by the consumer loop. Reports the post-dequeue queue depth to
    /// <see cref="AgentMetrics"/> after each item so the gauge tracks drains as well
    /// as enqueues.
    /// </summary>
    public async IAsyncEnumerable<AgentMessage> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in this.channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            this.metrics?.ObserveInboundQueueDepth(this.channel.Reader.Count);
            yield return message;
        }
    }

    /// <summary>
    /// Try to read a single message without waiting. Returns false if the channel is empty.
    /// Useful for testing and diagnostics.
    /// </summary>
    public bool TryRead(out AgentMessage? message)
    {
        var result = this.channel.Reader.TryRead(out var msg);
        message = msg;
        return result;
    }

    /// <summary>
    /// Signal that no more messages will be written. The consumer loop will drain
    /// remaining messages and then complete. This method is idempotent — calling
    /// it multiple times (e.g. during host shutdown) is safe.
    /// </summary>
    public void Complete()
    {
        this.channel.Writer.TryComplete();
    }
}
