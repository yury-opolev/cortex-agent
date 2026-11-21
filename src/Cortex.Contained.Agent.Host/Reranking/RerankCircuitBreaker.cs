namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>Circuit state of the reranker client.</summary>
public enum RerankCircuitState
{
    /// <summary>Calls go through.</summary>
    Closed,

    /// <summary>Too many consecutive failures: calls are skipped until the break ends.</summary>
    Open,

    /// <summary>The break ended: one trial call decides whether the circuit closes again.</summary>
    HalfOpen,
}

/// <summary>
/// Consecutive-failure circuit breaker: after <c>threshold</c> failures in a row reranking is skipped
/// for <c>breakDuration</c> (searches keep the fused order at no latency cost), then one trial call
/// closes or re-opens it. Thread-safe.
/// </summary>
public sealed class RerankCircuitBreaker
{
    private readonly TimeProvider time;
    private readonly object gate = new();
    private int consecutiveFailures;
    private DateTimeOffset? openUntil;
    private bool trialInFlight;

    public RerankCircuitBreaker(TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Current state.</summary>
    public RerankCircuitState State
    {
        get
        {
            lock (this.gate)
            {
                return this.openUntil is null ? RerankCircuitState.Closed
                    : this.time.GetUtcNow() < this.openUntil ? RerankCircuitState.Open
                    : RerankCircuitState.HalfOpen;
            }
        }
    }

    /// <summary>Failures since the last success.</summary>
    public int ConsecutiveFailures
    {
        get
        {
            lock (this.gate)
            {
                return this.consecutiveFailures;
            }
        }
    }

    /// <summary>End of the current break, or null when closed.</summary>
    public DateTimeOffset? OpenUntil
    {
        get
        {
            lock (this.gate)
            {
                return this.openUntil;
            }
        }
    }

    /// <summary>True when a call may go out now (closed, or the single half-open trial).</summary>
    public bool TryEnter()
    {
        lock (this.gate)
        {
            if (this.openUntil is null)
            {
                return true;
            }

            if (this.time.GetUtcNow() < this.openUntil || this.trialInFlight)
            {
                return false;
            }

            this.trialInFlight = true;
            return true;
        }
    }

    /// <summary>Records a successful call: the circuit closes.</summary>
    public void Success()
    {
        lock (this.gate)
        {
            this.consecutiveFailures = 0;
            this.openUntil = null;
            this.trialInFlight = false;
        }
    }

    /// <summary>Records a failed call; returns true when this failure opened (or re-opened) the circuit.</summary>
    public bool Failure(int threshold, TimeSpan breakDuration)
    {
        lock (this.gate)
        {
            this.consecutiveFailures++;
            var wasTrial = this.trialInFlight;
            this.trialInFlight = false;
            if (wasTrial || this.consecutiveFailures >= threshold)
            {
                this.openUntil = this.time.GetUtcNow() + breakDuration;
                return true;
            }

            return false;
        }
    }
}
