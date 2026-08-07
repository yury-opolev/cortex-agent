using Cortex.Contained.Bridge.Connectors;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Connectors;

public sealed class ConnectorRateLimiterTests
{
    // ── Unlimited (maxMessagesPerMinute <= 0) ─────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void TryAcquire_Unlimited_AlwaysReturnsTrue(int limit)
    {
        var limiter = new ConnectorRateLimiter(limit, TimeProvider.System);

        for (var i = 0; i < 10_000; i++)
        {
            Assert.True(limiter.TryAcquire());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void MaxMessagesPerMinute_Unlimited_ReturnsConfiguredValue(int limit)
    {
        var limiter = new ConnectorRateLimiter(limit, TimeProvider.System);
        Assert.Equal(limit, limiter.MaxMessagesPerMinute);
    }

    // ── Allows exactly the limit ──────────────────────────────────────

    [Fact]
    public void TryAcquire_WithinLimit_AllowsExactlyLimit()
    {
        var fake = new FakeTimeProvider();
        var limiter = new ConnectorRateLimiter(5, fake);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire(), $"call {i + 1} should be allowed");
        }

        Assert.False(limiter.TryAcquire(), "call 6 should be rejected");
    }

    // ── Rejects the next call past the limit ──────────────────────────

    [Fact]
    public void TryAcquire_OverLimit_ReturnsFalse()
    {
        var fake = new FakeTimeProvider();
        var limiter = new ConnectorRateLimiter(3, fake);

        limiter.TryAcquire();
        limiter.TryAcquire();
        limiter.TryAcquire();

        Assert.False(limiter.TryAcquire());
    }

    // ── Window slides: tokens available again after the window ────────

    [Fact]
    public void TryAcquire_AfterWindowSlides_AllowsNewMessages()
    {
        var fake = new FakeTimeProvider();
        var limiter = new ConnectorRateLimiter(3, fake);

        // Exhaust the limit.
        limiter.TryAcquire();
        limiter.TryAcquire();
        limiter.TryAcquire();
        Assert.False(limiter.TryAcquire());

        // Advance past the 60-second window.
        fake.Advance(TimeSpan.FromSeconds(61));

        // Should allow again.
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());
    }

    // ── Partial window slide ──────────────────────────────────────────

    [Fact]
    public void TryAcquire_PartialWindowSlide_OnlyExpiredSlotsBecomeFree()
    {
        var fake = new FakeTimeProvider();
        var limiter = new ConnectorRateLimiter(3, fake);

        // Send 2 messages at t=0.
        limiter.TryAcquire();
        limiter.TryAcquire();

        // Advance 30 seconds. Send 1 more at t=30.
        fake.Advance(TimeSpan.FromSeconds(30));
        limiter.TryAcquire();

        // At t=30, limit is hit (3/3).
        Assert.False(limiter.TryAcquire());

        // Advance to t=61 — the first two slots (from t=0) expire.
        fake.Advance(TimeSpan.FromSeconds(31));
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());

        // Now the t=30 slot is still active: limit hit again.
        Assert.False(limiter.TryAcquire());
    }

    // ── Thread safety ─────────────────────────────────────────────────

    [Fact]
    public void TryAcquire_ConcurrentCalls_GrantsExactlyLimit()
    {
        const int limit = 100;
        const int concurrency = 500;
        var fake = new FakeTimeProvider();
        var limiter = new ConnectorRateLimiter(limit, fake);
        var granted = 0;

        Parallel.For(0, concurrency, _ =>
        {
            if (limiter.TryAcquire())
            {
                Interlocked.Increment(ref granted);
            }
        });

        Assert.Equal(limit, granted);
    }

    // ── MaxMessagesPerMinute property ─────────────────────────────────

    [Fact]
    public void MaxMessagesPerMinute_ReturnsConfiguredLimit()
    {
        var limiter = new ConnectorRateLimiter(42, TimeProvider.System);
        Assert.Equal(42, limiter.MaxMessagesPerMinute);
    }
}
