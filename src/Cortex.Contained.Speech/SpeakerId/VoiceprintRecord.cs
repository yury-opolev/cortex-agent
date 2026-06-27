namespace Cortex.Contained.Speech.SpeakerId;

/// <summary>
/// Per-tenant voiceprint and enrollment metadata. Persisted by
/// <see cref="IVoiceprintStore"/> implementations.
/// </summary>
/// <param name="TenantId">Tenant identifier (primary key).</param>
/// <param name="State">Current lifecycle state.</param>
/// <param name="Embedding">Mean-of-normalised owner voiceprint. Null when state is not <see cref="VoiceEnrollmentState.Enrolled"/>.</param>
/// <param name="EmbeddingDim">Dimensionality of <paramref name="Embedding"/>. Zero when <paramref name="Embedding"/> is null.</param>
/// <param name="ModelId">Identifier of the model that produced <paramref name="Embedding"/> (e.g. "eres2netv2-base-v1"). Null when not enrolled.</param>
/// <param name="SampleCount">Number of enrollment utterances averaged into the embedding. Zero when not enrolled.</param>
/// <param name="CreatedAt">When the record was first inserted.</param>
/// <param name="ConfirmedAt">When state last became <see cref="VoiceEnrollmentState.Enrolled"/>.</param>
/// <param name="DeclinedAt">When state last became <see cref="VoiceEnrollmentState.Declined"/>.</param>
/// <param name="ThresholdOverride">Per-tenant cosine threshold override; null means use the global default.</param>
/// <param name="FeatureEnabled">Web-UI toggle. When false, verification is skipped entirely (treated as <see cref="VerificationResult.SkipReason.FeatureOff"/>).</param>
public sealed record VoiceprintRecord(
    string TenantId,
    VoiceEnrollmentState State,
    float[]? Embedding,
    int EmbeddingDim,
    string? ModelId,
    int SampleCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? DeclinedAt,
    float? ThresholdOverride,
    bool FeatureEnabled);
