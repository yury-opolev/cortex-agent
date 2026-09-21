using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Agent.Host.Agent.Autonomy;

internal enum AssumptionLedgerEntryKind
{
    Assumption,
    ParkedBlocker,
    Recovery,
    AnsweredForUser,
}

internal sealed record AssumptionLedgerEntry(
    long Sequence,
    AssumptionLedgerEntryKind Kind,
    string WorkItemKey,
    string EntryKey,
    string WorkItem,
    string? Decision,
    string? Reason,
    string? Blocker,
    string? Tried,
    string? Question,
    string? Answer);

/// <summary>
/// Records every autonomous decision that replaced asking a human, with redaction before storage.
/// <para>
/// Redaction is <see cref="SensitiveDataRedactor"/> — pattern-based, so only a secret-looking
/// substring is replaced and the surrounding prose survives. That matters more here than almost
/// anywhere else: this ledger IS the report of an unattended multi-day run, so a blanket payload
/// suppressor would leave every entry reading as the same placeholder and destroy the audit trail.
/// </para>
/// <para>
/// Identity keys are hashed from the ORIGINAL text, never the redacted display text. Redaction is
/// not identity-preserving, so a work item whose text contained a secret would otherwise never
/// match itself and the blocked-work proof would never fire.
/// </para>
/// </summary>
internal sealed class AssumptionLedger
{
    private readonly object syncLock = new();
    private readonly List<AssumptionLedgerEntry> entries = [];
    private long nextSequence;

    public AssumptionLedgerEntry RecordAssumption(string workItem, string decision, string reason)
        => this.Add(AssumptionLedgerEntryKind.Assumption, workItem, decision, reason, null, null, null, null);

    public AssumptionLedgerEntry ParkBlocker(string workItem, string blocker, string tried)
    {
        if (string.IsNullOrWhiteSpace(tried))
        {
            throw new ArgumentException("Parking a blocker requires a non-empty description of what was tried.", nameof(tried));
        }

        return this.Add(AssumptionLedgerEntryKind.ParkedBlocker, workItem, null, null, blocker, tried, null, null);
    }

    public AssumptionLedgerEntry RecordRecovery(string workItem, string decision, string reason)
        => this.Add(AssumptionLedgerEntryKind.Recovery, workItem, decision, reason, null, null, null, null);

    public AssumptionLedgerEntry RecordAnsweredForUser(string workItem, string question, string answer, string reason)
        => this.Add(AssumptionLedgerEntryKind.AnsweredForUser, workItem, null, reason, null, null, question, answer);

    public IReadOnlyList<AssumptionLedgerEntry> Snapshot()
    {
        lock (this.syncLock)
        {
            return this.entries.ToArray();
        }
    }

    public IReadOnlySet<string> ParkedBlockerItemKeys()
    {
        lock (this.syncLock)
        {
            return this.entries
                .Where(static e => e.Kind == AssumptionLedgerEntryKind.ParkedBlocker)
                .Select(static e => e.WorkItemKey)
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    public IReadOnlySet<string> DistinctEntryKeys()
    {
        lock (this.syncLock)
        {
            return this.entries
                .Select(static e => e.EntryKey)
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    private AssumptionLedgerEntry Add(
        AssumptionLedgerEntryKind kind,
        string workItem,
        string? decision,
        string? reason,
        string? blocker,
        string? tried,
        string? question,
        string? answer)
    {
        var workItemKey = Hash(kind: "work-item", workItem);
        var entryKey = Hash(kind.ToString(), workItem, decision, reason, blocker, tried, question, answer);
        var entry = new AssumptionLedgerEntry(
            0,
            kind,
            workItemKey,
            entryKey,
            Redact(workItem),
            RedactOrNull(decision),
            RedactOrNull(reason),
            RedactOrNull(blocker),
            RedactOrNull(tried),
            RedactOrNull(question),
            RedactOrNull(answer));

        lock (this.syncLock)
        {
            entry = entry with { Sequence = this.nextSequence++ };
            this.entries.Add(entry);
            return entry;
        }
    }

    private static string Redact(string text)
        => SensitiveDataRedactor.Redact(text);

    private static string? RedactOrNull(string? text)
        => text is null ? null : Redact(text);

    private static string Hash(string kind, params string?[] values)
    {
        using var sha = SHA256.Create();
        foreach (var value in values.Prepend(kind))
        {
            var text = value ?? string.Empty;
            var bytes = Encoding.UTF8.GetBytes(text);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash ?? []).ToLowerInvariant();
    }
}