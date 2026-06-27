using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

/// <summary>
/// Tests for the DAVE voice-decoding telemetry classifier + counters.
/// The classifier is pulled out as a pure static so we can assert message
/// parsing without a running Discord.Net / libdave.
/// </summary>
public class DaveEventStatsTests
{
    // ── Classify: decrypt failure variants ──

    [Theory]
    [InlineData("Failed to decrypt audio packet for 123: DecryptionFailure", DaveEventKind.DecryptFailure)]
    [InlineData("Failed to decrypt audio packet for 999: MissingKeyRatchet", DaveEventKind.MissingKeyRatchet)]
    [InlineData("Failed to decrypt audio packet for 1: InvalidNonce", DaveEventKind.InvalidNonce)]
    [InlineData("Failed to decrypt audio packet for 2: MissingCryptor", DaveEventKind.MissingCryptor)]
    public void Classify_DecryptCodes_ReturnsSpecificKind(string message, DaveEventKind expected)
    {
        Assert.Equal(expected, DaveEventStats.Classify("Audio", message));
    }

    [Fact]
    public void Classify_DecryptUnknownCode_FallsBackToGenericDecryptFailure()
    {
        // Future libdave could return a new code we don't know yet; still count it as a decrypt failure.
        var kind = DaveEventStats.Classify("Audio", "Failed to decrypt audio packet for 42: SomeFutureCode");
        Assert.Equal(DaveEventKind.DecryptFailure, kind);
    }

    // ── Classify: frame / ssrc errors ──

    [Fact]
    public void Classify_MalformedFrame_Recognized()
    {
        Assert.Equal(DaveEventKind.MalformedFrame, DaveEventStats.Classify("Audio", "Malformed Frame"));
    }

    [Fact]
    public void Classify_UnknownSsrc_Recognized()
    {
        Assert.Equal(DaveEventKind.UnknownSsrc, DaveEventStats.Classify("Audio", "Unknown SSRC 12345"));
    }

    [Fact]
    public void Classify_UnknownUser_Recognized()
    {
        Assert.Equal(DaveEventKind.UnknownUser, DaveEventStats.Classify("Audio", "Unknown User 67890"));
    }

    [Fact]
    public void Classify_MlsFailure_Recognized()
    {
        Assert.Equal(DaveEventKind.MlsFailure, DaveEventStats.Classify("Voice", "MLS Failure: external -> key-ratchet-missing"));
    }

    // ── Classify: non-events ──

    [Theory]
    [InlineData(null, null)]
    [InlineData("Audio", null)]
    [InlineData("Audio", "")]
    [InlineData("Audio", "Received HeartbeatAck")]
    [InlineData("Gateway", "Latency = 172 ms")]
    [InlineData("Audio", "Received 320 bytes from user 123")]
    public void Classify_NonDaveMessages_ReturnsNone(string? source, string? message)
    {
        Assert.Equal(DaveEventKind.None, DaveEventStats.Classify(source, message));
    }

    // ── Counters + snapshot ──

    [Fact]
    public void Record_IncrementsPerKindCounters()
    {
        var stats = new DaveEventStats();

        stats.Record(DaveEventKind.DecryptFailure);
        stats.Record(DaveEventKind.DecryptFailure);
        stats.Record(DaveEventKind.MalformedFrame);
        stats.Record(DaveEventKind.MlsFailure);

        var snap = stats.Take();
        Assert.Equal(2, snap.DecryptFailure);
        Assert.Equal(1, snap.MalformedFrame);
        Assert.Equal(1, snap.MlsFailure);
        Assert.Equal(0, snap.MissingKeyRatchet);
        Assert.Equal(4, snap.Total);
    }

    [Fact]
    public void Record_None_IsNoOp()
    {
        var stats = new DaveEventStats();
        stats.Record(DaveEventKind.None);
        Assert.Equal(0, stats.Take().Total);
    }

    // ── Snapshot diff ──

    [Fact]
    public void Snapshot_Delta_ComputesPerKindDifference()
    {
        var before = new DaveEventStats.Snapshot(10, 5, 0, 0, 3, 1, 0, 0);
        var after = new DaveEventStats.Snapshot(42, 5, 0, 0, 7, 1, 0, 2);

        var delta = after.Delta(before);

        Assert.Equal(32, delta.DecryptFailure);
        Assert.Equal(0, delta.MissingKeyRatchet);
        Assert.Equal(4, delta.MalformedFrame);
        Assert.Equal(0, delta.UnknownSsrc);
        Assert.Equal(2, delta.MlsFailure);
    }

    [Fact]
    public void Snapshot_Delta_NegativeDifferenceClampsToZero()
    {
        // Should never happen (counters are monotonic) but defend against
        // races where snapshots are taken in inconsistent order.
        var before = new DaveEventStats.Snapshot(10, 0, 0, 0, 0, 0, 0, 0);
        var after = new DaveEventStats.Snapshot(5, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(0, after.Delta(before).DecryptFailure);
    }

    // ── TryParseUserId ──

    [Fact]
    public void TryParseUserId_ValidDecryptFailureMessage_ReturnsId()
    {
        var id = DaveEventStats.TryParseUserId("Failed to decrypt audio packet for 806798098839765047: DecryptionFailure");
        Assert.Equal(806798098839765047UL, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some unrelated log line")]
    [InlineData("Malformed Frame")]
    public void TryParseUserId_NonDecryptMessages_ReturnsNull(string? message)
    {
        Assert.Null(DaveEventStats.TryParseUserId(message));
    }
}
