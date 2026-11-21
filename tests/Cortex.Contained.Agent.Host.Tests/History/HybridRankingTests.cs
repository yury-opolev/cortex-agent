using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class HybridRankingTests
{
    [Fact]
    public void Collapse_SameWorkerNearDuplicates_KeepsBestOnly()
    {
        float[] brief = [1, 0];
        float[] other = [0, 1];
        var ranked = new[]
        {
            new CollapseCandidate(1, 1.0, "delivered@ch", brief),
            new CollapseCandidate(2, 0.9, "delivered@ch", brief),
            new CollapseCandidate(3, 0.8, null, brief),
            new CollapseCandidate(4, 0.7, "delivered@ch", other),
        };

        var (order, collapsed) = NearDuplicateCollapse.Apply(ranked, lambda: 0.7, duplicateSimilarity: 0.95);

        Assert.Equal([1L, 3L, 4L], order);
        Assert.Equal(1, collapsed);
    }

    [Fact]
    public void Collapse_MmrDemotesSimilarSameAuthorSegment()
    {
        float[] a = [1, 0];
        float[] similar = VectorMath.Normalize([1, 0.6f]);
        float[] different = [0, 1];
        var ranked = new[]
        {
            new CollapseCandidate(1, 1.0, "internal@ch", a),
            new CollapseCandidate(2, 0.95, "internal@ch", similar),
            new CollapseCandidate(3, 0.9, "internal@ch", different),
        };

        var (order, collapsed) = NearDuplicateCollapse.Apply(ranked, lambda: 0.7, duplicateSimilarity: 0.95);

        Assert.Equal([1L, 3L, 2L], order);
        Assert.Equal(0, collapsed);
    }

    [Fact]
    public void Recency_OffByDefault_HalvesPerHalfLife()
    {
        var now = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(1, RecencyPrior.Multiplier(now.AddDays(-400), now, 0, 30));
        Assert.Equal(1.5, RecencyPrior.Multiplier(now, now, 0.5, 30), 12);
        Assert.Equal(1.25, RecencyPrior.Multiplier(now.AddDays(-30), now, 0.5, 30), 12);
    }
}
