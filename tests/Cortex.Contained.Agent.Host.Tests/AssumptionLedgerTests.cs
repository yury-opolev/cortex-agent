using Cortex.Contained.Agent.Host.Agent.Autonomy;

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

        // The exhaustion rule is only meaningful if the proof of what was tried survives storage.
        Assert.Equal("tried command", entry.Tried);
    }

    [Fact]
    public void RecordAssumption_FreeTextWithoutSecrets_SurvivesVerbatim()
    {
        // The ledger IS the report of an unattended run. A blanket suppressor would render every
        // entry as the same placeholder and destroy the audit trail, so ordinary prose must live.
        var ledger = new AssumptionLedger();

        ledger.RecordAssumption(
            "migrate the billing schema",
            "chose the additive column approach",
            "a destructive rename would break rollback");
        var entry = Assert.Single(ledger.Snapshot());

        Assert.Equal("migrate the billing schema", entry.WorkItem);
        Assert.Equal("chose the additive column approach", entry.Decision);
        Assert.Equal("a destructive rename would break rollback", entry.Reason);
    }

    [Fact]
    public void RecordAssumption_RedactsOnlyTheSecretAndKeepsSurroundingText()
    {
        var ledger = new AssumptionLedger();

        ledger.RecordAssumption("deploy with secret=abc123 now", "choose default", "key was sk-abcdefghijklmnopqrstuvwxyz012345");
        var entry = Assert.Single(ledger.Snapshot());

        Assert.StartsWith("deploy with secret=", entry.WorkItem, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", entry.WorkItem, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", entry.WorkItem, StringComparison.Ordinal);

        Assert.Equal("choose default", entry.Decision);

        Assert.StartsWith("key was ", entry.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz012345", entry.Reason, StringComparison.Ordinal);
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