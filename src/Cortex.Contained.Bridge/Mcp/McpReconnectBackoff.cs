namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Pure exponential-backoff schedule for reconnecting a failed MCP server connection. Keeps a
/// permanently-broken server from hot-looping across reconciles: each failed attempt waits longer
/// (1s, 2s, 4s, …) up to a one-minute cap. Stateless and deterministic so it can be unit-tested.
/// </summary>
public static class McpReconnectBackoff
{
    /// <summary>Delay before the first retry.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound on the backoff delay.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Delay before the <paramref name="attempt"/>-th retry (1-based):
    /// <c>BaseDelay * 2^(attempt-1)</c>, capped at <see cref="MaxDelay"/>. A non-positive attempt
    /// yields <see cref="TimeSpan.Zero"/> (retry immediately).
    /// </summary>
    public static TimeSpan DelayFor(int attempt)
    {
        if (attempt <= 0)
        {
            return TimeSpan.Zero;
        }

        // Cap the exponent well below the bit width so the shift never overflows; the cap below
        // clamps the actual value to MaxDelay regardless.
        var exponent = Math.Min(attempt - 1, 30);
        var ticks = BaseDelay.Ticks * (1L << exponent);
        if (ticks <= 0 || ticks > MaxDelay.Ticks)
        {
            return MaxDelay;
        }

        return TimeSpan.FromTicks(ticks);
    }
}
