using System.Collections.Concurrent;

namespace Cortex.Contained.Bridge.Connectors.Media;

/// <summary>
/// Per-channel rate limiter for attachment uploads.
/// </summary>
/// <remarks>
/// The connector's inbound frame limiter lives on the WebSocket session and does not cover the
/// REST upload endpoint at all, so without this a connector could bypass its message budget
/// entirely and spray multi-megabyte uploads. The storage quota bounds how much can be HELD;
/// this bounds how fast it can be OFFERED, which is the part that costs CPU and allocation even
/// when every upload is ultimately refused.
/// <para>
/// Buckets are keyed by channel id and created on demand. Idle buckets are swept once the map
/// grows past a threshold so a connector cycling channel ids cannot leak memory.
/// </para>
/// </remarks>
public sealed class ConnectorUploadRateLimiter
{
    /// <summary>Bucket count above which idle buckets are swept.</summary>
    internal const int SweepThreshold = 256;

    private readonly ConcurrentDictionary<string, ConnectorRateLimiter> buckets = new(StringComparer.Ordinal);
    private readonly int maxUploadsPerMinute;
    private readonly TimeProvider timeProvider;
    private readonly Lock sweepLock = new();

    /// <summary>Initialises a new <see cref="ConnectorUploadRateLimiter"/>.</summary>
    /// <param name="maxUploadsPerMinute">Uploads allowed per channel per minute; zero means unlimited.</param>
    /// <param name="timeProvider">Time source for window calculations.</param>
    public ConnectorUploadRateLimiter(int maxUploadsPerMinute, TimeProvider timeProvider)
    {
        this.maxUploadsPerMinute = maxUploadsPerMinute;
        this.timeProvider = timeProvider;
    }

    /// <summary>Number of live buckets. Intended for tests and diagnostics.</summary>
    public int BucketCount => this.buckets.Count;

    /// <summary>
    /// Attempts to acquire an upload slot for <paramref name="channelId"/>.
    /// </summary>
    /// <param name="channelId">The uploading channel.</param>
    /// <returns>True when the upload may proceed.</returns>
    public bool TryAcquire(string channelId)
    {
        if (this.maxUploadsPerMinute <= 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(channelId))
        {
            return false;
        }

        if (this.buckets.Count > SweepThreshold)
        {
            this.Sweep();
        }

        var bucket = this.buckets.GetOrAdd(
            channelId,
            _ => new ConnectorRateLimiter(this.maxUploadsPerMinute, this.timeProvider));

        return bucket.TryAcquire();
    }

    /// <summary>Drops the bucket for <paramref name="channelId"/>.</summary>
    /// <param name="channelId">The channel whose bucket should be discarded.</param>
    public void Forget(string channelId)
    {
        if (!string.IsNullOrEmpty(channelId))
        {
            this.buckets.TryRemove(channelId, out _);
        }
    }

    /// <summary>
    /// Drops buckets that currently have full headroom. A bucket at full capacity has no
    /// timestamps inside the window, so discarding it cannot grant anyone extra budget.
    /// </summary>
    private void Sweep()
    {
        // One sweeper at a time; the rest simply carry on and are limited by their own bucket.
        if (!this.sweepLock.TryEnter())
        {
            return;
        }

        try
        {
            foreach (var (channelId, bucket) in this.buckets)
            {
                if (bucket.HasFullHeadroom)
                {
                    this.buckets.TryRemove(channelId, out _);
                }
            }
        }
        finally
        {
            this.sweepLock.Exit();
        }
    }
}
