using Cortex.Contained.Agent.Host.Agent.Autonomy;
using Cortex.Contained.Agent.Host.Mcp;

namespace Cortex.Contained.Agent.Host.Tests;

public class AssumptionLedgerTests
{
    [Fact]
    public void ParkBlocker_WhitespaceTried_Throws()
    {
        var ledger = new AssumptionLedger();

        Assert.Throws<ArgumentException>(() => ledger.ParkBlocker("item", "blocked", "   "));
    }

    [Fact]
    public void ParkBlocker_NonEmptyTried_StoresParkedBlocker()
    {
        var ledger = new AssumptionLedger();

        ledger.ParkBlocker("item", "blocked", "tried command");
        var entry = Assert.Single(ledger.Snapshot());

        Assert.Equal(AssumptionLedgerEntryKind.ParkedBlocker, entry.Kind);
        Assert.Equal(McpTelemetrySanitizer.RedactedPayload, entry.Tried);
    }

    [Fact]
    public void RecordAssumption_RedactsFreeTextBeforeStorage()
    {
        var ledger = new AssumptionLedger();

        ledger.RecordAssumption("deploy secret=abc123", "choose default", "because secret=abc123");
        var entry = Assert.Single(ledger.Snapshot());

        Assert.Equal(McpTelemetrySanitizer.RedactedPayload, entry.WorkItem);
        Assert.Equal(McpTelemetrySanitizer.RedactedPayload, entry.Decision);
        Assert.Equal(McpTelemetrySanitizer.RedactedPayload, entry.Reason);
    }

    [Fact]
    public void RecordAssumption_SecretInDisplayText_DoesNotBreakIdentityMatching()
    {
        var ledger = new AssumptionLedger();

        ledger.RecordAssumption("rotate token secret=one", "decide", "reason");
        ledger.ParkBlocker("rotate token secret=one", "blocked", "tried reset");

        var snapshot = ledger.Snapshot();
        Assert.Equal(snapshot[0].WorkItemKey, snapshot[1].WorkItemKey);
        Assert.NotEqual(snapshot[0].EntryKey, snapshot[1].EntryKey);
    }

    [Fact]
    public void Snapshot_ReturnsCopyNotMutableInternals()
    {
        var ledger = new AssumptionLedger();
        ledger.RecordRecovery("item", "recover", "reason");

        var before = ledger.Snapshot();
        ledger.RecordAnsweredForUser("item", "question", "answer", "reason");
        var after = ledger.Snapshot();

        Assert.Single(before);
        Assert.Equal(2, after.Count);
    }

    [Fact]
    public void Snapshot_ConcurrentWriters_ReturnsCompleteEntries()
    {
        var ledger = new AssumptionLedger();

        Parallel.For(0, 20, i => ledger.RecordRecovery($"item {i}", "recover", "reason"));
        var snapshot = ledger.Snapshot();

        Assert.Equal(20, snapshot.Count);
        Assert.Equal(20, snapshot.Select(static e => e.Sequence).Distinct().Count());
    }
}