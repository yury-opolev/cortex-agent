using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class HistoryConfigFingerprintTests
{
    [Fact]
    public void Compute_SegmentationSettingChanges_FingerprintChanges()
    {
        var baseline = HistoryConfigFingerprint.Compute(new HistoryOptions());

        Assert.NotEqual(baseline, HistoryConfigFingerprint.Compute(new HistoryOptions { IdleGapMinutes = 31 }));
        Assert.NotEqual(baseline, HistoryConfigFingerprint.Compute(new HistoryOptions { MaxSegmentWords = 1499 }));
        Assert.NotEqual(baseline, HistoryConfigFingerprint.Compute(new HistoryOptions { TimeZone = "UTC" }));
    }

    [Fact]
    public void Compute_NonSegmentationSettingChanges_FingerprintUnchanged()
    {
        var baseline = HistoryConfigFingerprint.Compute(new HistoryOptions());

        Assert.Equal(baseline, HistoryConfigFingerprint.Compute(new HistoryOptions { SearchTokenCap = 900, DfCapRatio = 0.5 }));
    }
}
