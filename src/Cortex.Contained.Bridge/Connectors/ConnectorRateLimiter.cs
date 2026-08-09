namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Sliding-window per-connector message rate limiter.
/// </summary>
/// <remarks>
/// Uses a fixed-size ring buffer of timestamps (one slot per allowed message per window)
/// so <see cref="TryAcquire"/> performs zero heap allocations in the steady state.
/// When <see cref="MaxMessagesPerMinute"/> is zero or negative the limiter is unlimited
/// and <see cref="TryAcquire"/> always returns <see langword="true"/>.
/// </remarks>
public sealed class ConnectorRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly int maxMessagesPerMinute;
    private readonly TimeProvider timeProvider;

    // Ring buffer storing UtcTicks of each accepted message.
    // Null when the limiter is unlimited (maxMessagesPerMinute <= 0).
    private readonly long[]? timestamps;
    private readonly Lock syncLock = new();

    // When the buffer is full, writePos points to the oldest entry (the next slot to overwrite).
    // When the buffer is not full, writePos points to the next empty slot.
    private int writePos;
    private int count;

    /// <summary>
    /// Initialises a new <see cref="ConnectorRateLimiter"/>.
    /// </summary>
    /// <param name="maxMessagesPerMinute">
    /// Maximum messages allowed in a trailing 60-second window.
    /// A value of zero or less means unlimited — <see cref="TryAcquire"/> will always return
    /// <see langword="true"/> without allocating or acquiring any lock.
    /// </param>
    /// <param name="timeProvider">Time source used for window calculations.</param>
    public ConnectorRateLimiter(int maxMessagesPerMinute, TimeProvider timeProvider)
    {
        this.maxMessagesPerMinute = maxMessagesPerMinute;
        this.timeProvider = timeProvider;
        this.timestamps = maxMessagesPerMinute > 0 ? new long[maxMessagesPerMinute] : null;
    }

    /// <summary>Maximum messages per minute; zero or negative means unlimited.</summary>
    public int MaxMessagesPerMinute => this.maxMessagesPerMinute;

    /// <summary>
    /// True when no acquisition inside the current window is still being counted, so the limiter
    /// is indistinguishable from a freshly created one. Used to decide when a per-key limiter can
    /// be discarded without granting its owner extra budget.
    /// </summary>
    public bool HasFullHeadroom
    {
        get
        {
            if (this.timestamps is null)
            {
                return true;
            }

            var nowTicks = this.timeProvider.GetUtcNow().UtcTicks;
            var windowTicks = Window.Ticks;

            lock (this.syncLock)
            {
                if (this.count == 0)
                {
                    return true;
                }

                // The newest entry sits immediately behind writePos.
                var newest = this.timestamps[(this.writePos - 1 + this.maxMessagesPerMinute) % this.maxMessagesPerMinute];
                return nowTicks - newest >= windowTicks;
            }
        }
    }

    /// <summary>
    /// Attempts to acquire a slot for one inbound message.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the message is within the limit and may proceed;
    /// <see langword="false"/> when the connector has already sent
    /// <see cref="MaxMessagesPerMinute"/> messages in the trailing 60 seconds.
    /// </returns>
    public bool TryAcquire()
    {
        if (this.timestamps is null)
        {
            return true; // unlimited
        }

        var nowTicks = this.timeProvider.GetUtcNow().UtcTicks;
        var windowTicks = Window.Ticks;

        lock (this.syncLock)
        {
            if (this.count == this.maxMessagesPerMinute)
            {
                // Buffer is full; writePos points to the oldest entry.
                // If the oldest entry is still within the window, the limit is active.
                if (nowTicks - this.timestamps[this.writePos] < windowTicks)
                {
                    return false;
                }

                // Oldest entry has expired — overwrite it (count stays at max).
            }
            else
            {
                this.count++;
            }

            this.timestamps[this.writePos] = nowTicks;
            this.writePos = (this.writePos + 1) % this.maxMessagesPerMinute;
            return true;
        }
    }
}
