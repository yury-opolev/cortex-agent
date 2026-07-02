namespace Cortex.Contained.Channels.CloudMessaging.Reconnect;

/// <summary>
/// Pure reconnect/backoff decision for the <see cref="CloudMessagingChannel"/> connect loop.
/// Extracted for unit-testability independent of the live WebSocket.
/// </summary>
public static class BackoffDecision
{
    /// <summary>
    /// Returns the delay before the next reconnect attempt using exponential backoff
    /// with a jitter band and a maximum cap.
    /// </summary>
    /// <param name="attemptCount">
    /// Zero-based attempt count (0 = first retry, after first failure).
    /// </param>
    /// <param name="baseDelayMs">Base delay in milliseconds (default 1 000 ms).</param>
    /// <param name="maxDelayMs">Maximum delay in milliseconds (default 60 000 ms).</param>
    /// <param name="jitterMs">Maximum random jitter added to each delay (default 2 000 ms).</param>
    /// <param name="randomJitter">
    /// A value in [0, 1) used to compute jitter. Pass a fixed value in tests.
    /// </param>
    public static TimeSpan ComputeDelay(
        int attemptCount,
        int baseDelayMs = 1_000,
        int maxDelayMs = 60_000,
        int jitterMs = 2_000,
        double randomJitter = 0.0)
    {
        if (attemptCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptCount), "Must be >= 0.");
        }

        // Exponential: base * 2^attempt, capped at max (before jitter)
        var exponentialMs = (double)baseDelayMs * Math.Pow(2.0, attemptCount);
        var clampedMs = Math.Min(exponentialMs, maxDelayMs);

        // Add deterministic jitter to spread reconnects when many bridges reconnect together
        var actualJitter = randomJitter * jitterMs;
        var totalMs = clampedMs + actualJitter;

        return TimeSpan.FromMilliseconds(totalMs);
    }

    /// <summary>
    /// Returns true when a failed connect attempt should be retried (always — the
    /// channel retries indefinitely; callers cancel via <see cref="CancellationToken"/>).
    /// </summary>
    public static bool ShouldRetry() => true;
}
