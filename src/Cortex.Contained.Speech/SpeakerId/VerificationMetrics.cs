namespace Cortex.Contained.Speech.SpeakerId;

using System.Collections.Concurrent;

/// <summary>
/// Per-tenant counters for verification gate outcomes. Lets the operator
/// track FAR / FRR over time without grepping structured logs.
/// </summary>
/// <remarks>
/// In-memory only. Counters reset on process restart — that's deliberate; if
/// historical retention is needed later, hook a periodic sampler onto these
/// values. The metrics surface is intentionally tiny.
/// </remarks>
public sealed class VerificationMetrics
{
    private readonly ConcurrentDictionary<string, Counters> byTenant = new(StringComparer.Ordinal);

    /// <summary>Record a single gate decision for a tenant.</summary>
    public void Record(string tenantId, VerificationResult result)
    {
        var counters = this.byTenant.GetOrAdd(tenantId, _ => new Counters());
        switch (result)
        {
            case VerificationResult.Accept:
                Interlocked.Increment(ref counters.Accept);
                break;
            case VerificationResult.Reject:
                Interlocked.Increment(ref counters.Reject);
                break;
            case VerificationResult.NotEnrolledResult:
                Interlocked.Increment(ref counters.NotEnrolled);
                break;
            case VerificationResult.Skipped skipped:
                switch (skipped.Reason)
                {
                    case VerificationResult.SkipReason.FeatureOff:
                        Interlocked.Increment(ref counters.SkippedFeatureOff);
                        break;
                    case VerificationResult.SkipReason.EnrollmentInProgress:
                        Interlocked.Increment(ref counters.SkippedEnrollmentInProgress);
                        break;
                    case VerificationResult.SkipReason.TooShort:
                        Interlocked.Increment(ref counters.SkippedTooShort);
                        break;
                    case VerificationResult.SkipReason.Error:
                        Interlocked.Increment(ref counters.SkippedError);
                        break;
                }
                break;
        }
    }

    /// <summary>Record a hub timeout (gate decided independent of the verifier).</summary>
    public void RecordTimeout(string tenantId)
    {
        var counters = this.byTenant.GetOrAdd(tenantId, _ => new Counters());
        Interlocked.Increment(ref counters.GateTimeout);
    }

    /// <summary>Snapshot per-tenant counters. Returned dictionary is a copy; safe to enumerate.</summary>
    public IReadOnlyDictionary<string, VerificationCountersSnapshot> Snapshot()
    {
        var result = new Dictionary<string, VerificationCountersSnapshot>(StringComparer.Ordinal);
        foreach (var (tenantId, c) in this.byTenant)
        {
            result[tenantId] = new VerificationCountersSnapshot(
                Accept: Volatile.Read(ref c.Accept),
                Reject: Volatile.Read(ref c.Reject),
                NotEnrolled: Volatile.Read(ref c.NotEnrolled),
                SkippedFeatureOff: Volatile.Read(ref c.SkippedFeatureOff),
                SkippedEnrollmentInProgress: Volatile.Read(ref c.SkippedEnrollmentInProgress),
                SkippedTooShort: Volatile.Read(ref c.SkippedTooShort),
                SkippedError: Volatile.Read(ref c.SkippedError),
                GateTimeout: Volatile.Read(ref c.GateTimeout));
        }
        return result;
    }

    private sealed class Counters
    {
        public long Accept;
        public long Reject;
        public long NotEnrolled;
        public long SkippedFeatureOff;
        public long SkippedEnrollmentInProgress;
        public long SkippedTooShort;
        public long SkippedError;
        public long GateTimeout;
    }
}

/// <summary>Read-only snapshot of <see cref="VerificationMetrics"/> for a single tenant.</summary>
public sealed record VerificationCountersSnapshot(
    long Accept,
    long Reject,
    long NotEnrolled,
    long SkippedFeatureOff,
    long SkippedEnrollmentInProgress,
    long SkippedTooShort,
    long SkippedError,
    long GateTimeout);
