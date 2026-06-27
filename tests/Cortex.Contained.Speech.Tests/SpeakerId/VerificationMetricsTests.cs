namespace Cortex.Contained.Speech.Tests.SpeakerId;

using Cortex.Contained.Speech.SpeakerId;

public sealed class VerificationMetricsTests
{
    [Fact]
    public void Record_TallyPerTenantAndOutcome()
    {
        var metrics = new VerificationMetrics();

        metrics.Record("tenant-a", new VerificationResult.Accept(0.9f));
        metrics.Record("tenant-a", new VerificationResult.Accept(0.85f));
        metrics.Record("tenant-a", new VerificationResult.Reject(0.2f));
        metrics.Record("tenant-a", VerificationResult.NotEnrolled);
        metrics.Record("tenant-a", new VerificationResult.Skipped(VerificationResult.SkipReason.TooShort));
        metrics.Record("tenant-a", new VerificationResult.Skipped(VerificationResult.SkipReason.Error));
        metrics.Record("tenant-a", new VerificationResult.Skipped(VerificationResult.SkipReason.FeatureOff));
        metrics.Record("tenant-a", new VerificationResult.Skipped(VerificationResult.SkipReason.EnrollmentInProgress));
        metrics.RecordTimeout("tenant-a");

        metrics.Record("tenant-b", new VerificationResult.Accept(0.7f));

        var snapshot = metrics.Snapshot();

        Assert.Equal(2, snapshot.Count);
        var a = snapshot["tenant-a"];
        Assert.Equal(2, a.Accept);
        Assert.Equal(1, a.Reject);
        Assert.Equal(1, a.NotEnrolled);
        Assert.Equal(1, a.SkippedFeatureOff);
        Assert.Equal(1, a.SkippedEnrollmentInProgress);
        Assert.Equal(1, a.SkippedTooShort);
        Assert.Equal(1, a.SkippedError);
        Assert.Equal(1, a.GateTimeout);

        var b = snapshot["tenant-b"];
        Assert.Equal(1, b.Accept);
        Assert.Equal(0, b.Reject);
    }

    [Fact]
    public void Snapshot_ReturnsCopy_NotLiveView()
    {
        var metrics = new VerificationMetrics();
        metrics.Record("tenant-a", new VerificationResult.Accept(0.9f));

        var snap1 = metrics.Snapshot();
        metrics.Record("tenant-a", new VerificationResult.Accept(0.9f));
        var snap2 = metrics.Snapshot();

        Assert.Equal(1, snap1["tenant-a"].Accept);
        Assert.Equal(2, snap2["tenant-a"].Accept);
    }

    [Fact]
    public void Record_ConcurrentTallies_AreCorrect()
    {
        var metrics = new VerificationMetrics();
        const int threads = 8;
        const int perThread = 1_000;

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
            {
                metrics.Record("tenant-a", new VerificationResult.Accept(0.9f));
            }
        });

        Assert.Equal(threads * perThread, metrics.Snapshot()["tenant-a"].Accept);
    }
}
