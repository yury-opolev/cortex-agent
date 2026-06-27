namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Wire-friendly snapshot of a per-tenant voiceprint record exchanged between
/// Agent.Host (where the SQLite store lives) and the Bridge (where the
/// verification gate runs against the channel audio path).
/// </summary>
/// <param name="TenantId">Tenant identifier.</param>
/// <param name="StateName">String form of the lifecycle state (Unknown / Declined / Enrolling / Confirming / PendingReenroll / Enrolled).</param>
/// <param name="Embedding">L2-normalised owner voiceprint vector. Null when the tenant is not in Enrolled or PendingReenroll.</param>
/// <param name="EmbeddingDim">Dimensionality of <paramref name="Embedding"/>. Zero when null.</param>
/// <param name="ModelId">Identifier of the model that produced the embedding.</param>
/// <param name="ThresholdOverride">Per-tenant cosine threshold override. Null means use the global default.</param>
/// <param name="FeatureEnabled">When false the verifier short-circuits to Skipped(FeatureOff).</param>
public sealed record VoiceprintSnapshotDto(
    string TenantId,
    string StateName,
    float[]? Embedding,
    int EmbeddingDim,
    string? ModelId,
    float? ThresholdOverride,
    bool FeatureEnabled);
