using Cortex.Contained.Agent.Host.Reranking;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary>The consecutive-failure circuit breaker.</summary>
public sealed class RerankCircuitBreakerTests
{
    private static readonly TimeSpan Break = TimeSpan.FromSeconds(60);
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Failures_BelowThreshold_StayClosed_ThresholdOpens()
    {
        var breaker = new RerankCircuitBreaker(this.time);

        Assert.False(breaker.Failure(3, Break));
        Assert.False(breaker.Failure(3, Break));
        Assert.True(breaker.Failure(3, Break));

        Assert.Equal(RerankCircuitState.Open, breaker.State);
        Assert.False(breaker.TryEnter());
    }

    [Fact]
    public void BreakOver_AllowsOneTrial_SuccessCloses()
    {
        var breaker = new RerankCircuitBreaker(this.time);
        breaker.Failure(1, Break);
        this.time.Advance(Break);

        Assert.True(breaker.TryEnter());
        Assert.False(breaker.TryEnter());
        breaker.Success();

        Assert.Equal(RerankCircuitState.Closed, breaker.State);
        Assert.Equal(0, breaker.ConsecutiveFailures);
    }

    [Fact]
    public void AbandonedTrial_FreesTheTrialWithoutCountingAFailure()
    {
        var breaker = new RerankCircuitBreaker(this.time);
        breaker.Failure(1, Break);
        this.time.Advance(Break);
        Assert.True(breaker.TryEnter());

        breaker.Abandon();

        Assert.Equal(RerankCircuitState.HalfOpen, breaker.State);
        Assert.Equal(1, breaker.ConsecutiveFailures);
        Assert.True(breaker.TryEnter());
    }

    [Fact]
    public void FailedTrial_ReopensImmediately()
    {
        var breaker = new RerankCircuitBreaker(this.time);
        breaker.Failure(1, Break);
        this.time.Advance(Break);
        Assert.True(breaker.TryEnter());

        Assert.True(breaker.Failure(10, Break));
        Assert.Equal(RerankCircuitState.Open, breaker.State);
    }
}
