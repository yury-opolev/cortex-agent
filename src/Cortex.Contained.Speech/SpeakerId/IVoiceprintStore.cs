namespace Cortex.Contained.Speech.SpeakerId;

/// <summary>
/// Persists per-tenant voiceprints and enrollment state.
/// </summary>
/// <remarks>
/// Phase 1 ships an in-memory implementation suitable for development and
/// tests. A SQLite-backed implementation lives in Agent.Host (Phase 2).
/// All implementations must be safe for concurrent reads and serialise
/// writes per tenant.
/// </remarks>
public interface IVoiceprintStore
{
    /// <summary>
    /// Returns the record for <paramref name="tenantId"/>, or null if no
    /// row exists yet (treated by callers as <see cref="VoiceEnrollmentState.Unknown"/>
    /// with the feature on).
    /// </summary>
    ValueTask<VoiceprintRecord?> GetAsync(string tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts or replaces the record for <c>record.TenantId</c>.
    /// </summary>
    ValueTask UpsertAsync(VoiceprintRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the record for <paramref name="tenantId"/>, if present.
    /// </summary>
    ValueTask DeleteAsync(string tenantId, CancellationToken cancellationToken);
}
