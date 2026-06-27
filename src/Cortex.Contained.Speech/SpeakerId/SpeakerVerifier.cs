namespace Cortex.Contained.Speech.SpeakerId;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="ISpeakerVerifier"/>. Reads the per-tenant voiceprint
/// from the store, embeds the incoming utterance, and compares.
/// </summary>
public sealed partial class SpeakerVerifier : ISpeakerVerifier
{
    private readonly ISpeakerEmbedder embedder;
    private readonly IVoiceprintStore store;
    private readonly IOptions<SpeakerIdOptions> options;
    private readonly ILogger logger;

    public SpeakerVerifier(
        ISpeakerEmbedder embedder,
        IVoiceprintStore store,
        IOptions<SpeakerIdOptions> options,
        ILogger<SpeakerVerifier>? logger = null)
    {
        this.embedder = embedder;
        this.store = store;
        this.options = options;
        this.logger = (ILogger?)logger ?? NullLogger<SpeakerVerifier>.Instance;
    }

    public async ValueTask<VerificationResult> VerifyAsync(
        ReadOnlyMemory<short> pcm16Mono16k,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var record = await this.store.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);

        if (record is not null && !record.FeatureEnabled)
        {
            return new VerificationResult.Skipped(VerificationResult.SkipReason.FeatureOff);
        }

        var state = record?.State ?? VoiceEnrollmentState.Unknown;

        if (state is VoiceEnrollmentState.Enrolling or VoiceEnrollmentState.Confirming)
        {
            return new VerificationResult.Skipped(VerificationResult.SkipReason.EnrollmentInProgress);
        }

        if (record is null
            || record.Embedding is null
            || state is VoiceEnrollmentState.Unknown or VoiceEnrollmentState.Declined)
        {
            return VerificationResult.NotEnrolled;
        }

        // State is Enrolled or PendingReenroll — gate active per spec.
        var opts = this.options.Value;
        var minSamples = (int)(opts.MinUtteranceLength.TotalSeconds * opts.EmbedderSampleRate);

        // Trim leading/trailing silence so cosine comparison sees voice content
        // only. This was a Phase 1 deviation (the verifier originally assumed
        // pre-trimmed audio); moving the trim in-verifier is required for the
        // host-side VoiceChannel whose accumulator doesn't trim. Discord's
        // accumulator only trims leading silence, so trailing silence padding
        // (up to SilenceTimeoutMs) also benefits from this pass.
        var trimmed = AudioConverter.TrimSilence(pcm16Mono16k);
        if (trimmed.Length < minSamples)
        {
            return new VerificationResult.Skipped(VerificationResult.SkipReason.TooShort);
        }
        pcm16Mono16k = trimmed;

        // Catch dim mismatches before paying for inference.
        if (this.embedder.EmbeddingDim != record.EmbeddingDim)
        {
            this.LogDimMismatch(tenantId, record.EmbeddingDim, this.embedder.EmbeddingDim);
            return new VerificationResult.Skipped(VerificationResult.SkipReason.Error);
        }

        try
        {
            var embedding = await this.embedder.EmbedAsync(pcm16Mono16k, cancellationToken).ConfigureAwait(false);
            if (embedding.Length == 0)
            {
                return new VerificationResult.Skipped(VerificationResult.SkipReason.TooShort);
            }

            // Defence-in-depth: the embedder may produce a vector that does
            // not match its advertised dim. Keep the check.
            if (embedding.Length != record.EmbeddingDim)
            {
                this.LogDimMismatch(tenantId, record.EmbeddingDim, embedding.Length);
                return new VerificationResult.Skipped(VerificationResult.SkipReason.Error);
            }

            var score = SpeakerEmbeddingMath.CosineSimilarity(embedding, record.Embedding);
            var threshold = record.ThresholdOverride ?? opts.DefaultCosineThreshold;

            return score >= threshold
                ? new VerificationResult.Accept(score)
                : new VerificationResult.Reject(score);
        }
#pragma warning disable CA1031 // Intentional broad catch: never block the user on embedder/store failures.
        catch (Exception ex)
        {
            this.LogVerifyError(ex, tenantId);
            return new VerificationResult.Skipped(VerificationResult.SkipReason.Error);
        }
#pragma warning restore CA1031
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "speaker-id: dim mismatch tenant={TenantId} stored={Stored} produced={Produced}")]
    private partial void LogDimMismatch(string tenantId, int stored, int produced);

    [LoggerMessage(Level = LogLevel.Warning, Message = "speaker-id: verify failed tenant={TenantId}")]
    private partial void LogVerifyError(Exception exception, string tenantId);
}
