namespace Cortex.Contained.Speech.Tests.SpeakerId;

using Cortex.Contained.Speech.SpeakerId;
using Microsoft.Extensions.Options;

public sealed class SpeakerVerifierTests
{
    private const string TenantId = "tenant-a";

    [Fact]
    public async Task FeatureDisabled_ReturnsSkippedFeatureOff()
    {
        var store = new InMemoryVoiceprintStore();
        await store.UpsertAsync(
            RecordEnrolled(featureEnabled: false, embedding: [1, 0, 0, 0]),
            CancellationToken.None);

        var verifier = MakeVerifier(store, fakeEmbedding: [1, 0, 0, 0]);
        var result = await verifier.VerifyAsync(OneSecondOfPcm(), TenantId, CancellationToken.None);

        var skipped = Assert.IsType<VerificationResult.Skipped>(result);
        Assert.Equal(VerificationResult.SkipReason.FeatureOff, skipped.Reason);
        Assert.True(result.PassesTranscript);
    }

    [Fact]
    public async Task EnrolmentInProgress_Enrolling_ReturnsSkipped()
    {
        var store = new InMemoryVoiceprintStore();
        await store.UpsertAsync(MakeRecord(VoiceEnrollmentState.Enrolling), CancellationToken.None);

        var verifier = MakeVerifier(store);
        var result = await verifier.VerifyAsync(OneSecondOfPcm(), TenantId, CancellationToken.None);

        var skipped = Assert.IsType<VerificationResult.Skipped>(result);
        Assert.Equal(VerificationResult.SkipReason.EnrollmentInProgress, skipped.Reason);
    }

    [Fact]
    public async Task EnrolmentInProgress_Confirming_ReturnsSkipped()
    {
        var store = new InMemoryVoiceprintStore();
        await store.UpsertAsync(MakeRecord(VoiceEnrollmentState.Confirming), CancellationToken.None);

        var verifier = MakeVerifier(store);
        var result = await verifier.VerifyAsync(OneSecondOfPcm(), TenantId, CancellationToken.None);

        var skipped = Assert.IsType<VerificationResult.Skipped>(result);
        Assert.Equal(VerificationResult.SkipReason.EnrollmentInProgress, skipped.Reason);
    }

    [Fact]
    public async Task NoRecord_ReturnsNotEnrolled()
    {
        var store = new InMemoryVoiceprintStore();

        var verifier = MakeVerifier(store);
        var result = await verifier.VerifyAsync(OneSecondOfPcm(), TenantId, CancellationToken.None);

        Assert.Same(VerificationResult.NotEnrolled, result);
    }

    [Theory]
    [InlineData(VoiceEnrollmentState.Unknown)]
    [InlineData(VoiceEnrollmentState.Declined)]
    public async Task UnknownOrDeclined_ReturnsNotEnrolled(VoiceEnrollmentState state)
    {
        var store = new InMemoryVoiceprintStore();
        await store.UpsertAsync(MakeRecord(state), CancellationToken.None);

        var verifier = MakeVerifier(store);
        var result = await verifier.VerifyAsync(OneSecondOfPcm(), TenantId, CancellationToken.None);

        Assert.Same(VerificationResult.NotEnrolled, result);
    }

    [Fact]
    public async Task PendingReenroll_GateRemainsActive_AndAcceptsOwnerVoice()
    {
        var store = new InMemoryVoiceprintStore();
        // PendingReenroll keeps existing voiceprint active per spec.
        await store.UpsertAsync(
            new VoiceprintRecord(
                TenantId: TenantId,
                State: VoiceEnrollmentState.PendingReenroll,
                Embedding: [1, 0, 0, 0],
                EmbeddingDim: 4,
                ModelId: "fake",
                SampleCount: 3,
                CreatedAt: DateTimeOffset.UtcNow,
                ConfirmedAt: DateTimeOffset.UtcNow,
                DeclinedAt: null,
                ThresholdOverride: null,
                FeatureEnabled: true),
            CancellationToken.None);

        var verifier = MakeVerifier(store, fakeEmbedding: [1, 0, 0, 0]);
        var result = await verifier.VerifyAsync(OneSecondOfPcm(), TenantId, CancellationToken.None);

        Assert.IsType<VerificationResult.Accept>(result);
    }

    [Fact]
    public async Task TooShort_ReturnsSkippedTooShort()
    {
        var store = new InMemoryVoiceprintStore();
        await store.UpsertAsync(
            RecordEnrolled(featureEnabled: true, embedding: [1, 0, 0, 0]),
            CancellationToken.None);

        var verifier = MakeVerifier(store, fakeEmbedding: [1, 0, 0, 0]);
        // 200 ms — under the 800 ms default minimum.
        var pcm = new short[16000 / 5];
        var result = await verifier.VerifyAsync(pcm, TenantId, CancellationToken.None);

        var skipped = Assert.IsType<VerificationResult.Skipped>(result);
        Assert.Equal(VerificationResult.SkipReason.TooShort, skipped.Reason);
    }

    [Fact]
    public async Task EmbeddingDimMismatch_ReturnsSkippedError()
    {
        var store = new InMemoryVoiceprintStore();
        // Stored has dim 4; fake embedder returns dim 5.
        await store.UpsertAsync(
            RecordEnrolled(featureEnabled: true, embedding: [1, 0, 0, 0]),
            CancellationToken.None);

        var verifier = MakeVerifier(store, fakeEmbedding: [1, 0, 0, 0, 0]);
        var result = await verifier.VerifyAsync(OneSecondOfPcm(), TenantId, CancellationToken.None);

        var skipped = Assert.IsType<VerificationResult.Skipped>(result);
        Assert.Equal(VerificationResult.SkipReason.Error, skipped.Reason);
    }

    [Fact]
    public async Task EmbedderThrows_ReturnsSkippedError()
    {
        var store = new InMemoryVoiceprintStore();
        await store.UpsertAsync(
            RecordEnrolled(featureEnabled: true, embedding: [1, 0, 0, 0]),
            CancellationToken.None);

        var verifier = MakeVerifier(store, throwingEmbedder: true);
        var result = await verifier.VerifyAsync(OneSecondOfPcm(), TenantId, CancellationToken.None);

        var skipped = Assert.IsType<VerificationResult.Skipped>(result);
        Assert.Equal(VerificationResult.SkipReason.Error, skipped.Reason);
    }

    [Fact]
    public async Task Match_ReturnsAcceptAboveDefaultThreshold()
    {
        var store = new InMemoryVoiceprintStore();
        await store.UpsertAsync(
            RecordEnrolled(featureEnabled: true, embedding: [1, 0, 0, 0]),
            CancellationToken.None);

        var verifier = MakeVerifier(store, fakeEmbedding: [1, 0, 0, 0]);
        var result = await verifier.VerifyAsync(OneSecondOfPcm(), TenantId, CancellationToken.None);

        var accept = Assert.IsType<VerificationResult.Accept>(result);
        Assert.True(accept.Score >= 0.99f);
        Assert.True(result.PassesTranscript);
    }

    [Fact]
    public async Task NonMatch_ReturnsRejectBelowThreshold()
    {
        var store = new InMemoryVoiceprintStore();
        await store.UpsertAsync(
            RecordEnrolled(featureEnabled: true, embedding: [1, 0, 0, 0]),
            CancellationToken.None);

        var verifier = MakeVerifier(store, fakeEmbedding: [0, 1, 0, 0]);
        var result = await verifier.VerifyAsync(OneSecondOfPcm(), TenantId, CancellationToken.None);

        var reject = Assert.IsType<VerificationResult.Reject>(result);
        Assert.True(reject.Score < 0.01f);
        Assert.False(result.PassesTranscript);
    }

    [Fact]
    public async Task LongUtteranceMostlySilence_GetsTrimmedAndStillEvaluated()
    {
        var store = new InMemoryVoiceprintStore();
        await store.UpsertAsync(
            RecordEnrolled(featureEnabled: true, embedding: [1, 0, 0, 0]),
            CancellationToken.None);

        // 3 seconds total, with a voiced "burst" only in the middle 1 second.
        var pcm = new short[16000 * 3];
        for (var i = 16000; i < 32000; i++)
        {
            pcm[i] = (short)(short.MaxValue * 0.3f * MathF.Sin(2 * MathF.PI * 440 * i / 16000f));
        }

        var verifier = MakeVerifier(store, fakeEmbedding: [1, 0, 0, 0]);
        var result = await verifier.VerifyAsync(pcm, TenantId, CancellationToken.None);

        // The voiced middle is ~1s, comfortably above the 800ms minimum after trim;
        // the embedder matches; expect Accept.
        Assert.IsType<VerificationResult.Accept>(result);
    }

    [Fact]
    public async Task AllSilenceUtterance_ReturnsTooShortAfterTrim()
    {
        var store = new InMemoryVoiceprintStore();
        await store.UpsertAsync(
            RecordEnrolled(featureEnabled: true, embedding: [1, 0, 0, 0]),
            CancellationToken.None);

        // 3 seconds of pure silence — trim removes everything → TooShort.
        var pcm = new short[16000 * 3];

        var verifier = MakeVerifier(store, fakeEmbedding: [1, 0, 0, 0]);
        var result = await verifier.VerifyAsync(pcm, TenantId, CancellationToken.None);

        var skipped = Assert.IsType<VerificationResult.Skipped>(result);
        Assert.Equal(VerificationResult.SkipReason.TooShort, skipped.Reason);
    }

    [Fact]
    public async Task ThresholdOverride_AppliesPerTenant()
    {
        var store = new InMemoryVoiceprintStore();
        // Stored = [1, 0]; fake = [0.7, sqrt(0.51)] → cosine 0.7.
        await store.UpsertAsync(
            RecordEnrolled(featureEnabled: true, embedding: [1, 0], thresholdOverride: 0.9f),
            CancellationToken.None);

        var verifier = MakeVerifier(store, fakeEmbedding: [0.7f, MathF.Sqrt(0.51f)]);
        var result = await verifier.VerifyAsync(OneSecondOfPcm(), TenantId, CancellationToken.None);

        Assert.IsType<VerificationResult.Reject>(result);
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static SpeakerVerifier MakeVerifier(
        IVoiceprintStore store,
        float[]? fakeEmbedding = null,
        bool throwingEmbedder = false)
    {
        var embedder = throwingEmbedder
            ? (ISpeakerEmbedder)new ThrowingEmbedder()
            : new FakeEmbedder(fakeEmbedding ?? [1, 0, 0, 0]);
        return new SpeakerVerifier(
            embedder,
            store,
            Options.Create(new SpeakerIdOptions()));
    }

    private static VoiceprintRecord MakeRecord(VoiceEnrollmentState state) => new(
        TenantId: TenantId,
        State: state,
        Embedding: null,
        EmbeddingDim: 0,
        ModelId: null,
        SampleCount: 0,
        CreatedAt: DateTimeOffset.UtcNow,
        ConfirmedAt: null,
        DeclinedAt: null,
        ThresholdOverride: null,
        FeatureEnabled: true);

    private static VoiceprintRecord RecordEnrolled(
        bool featureEnabled,
        float[] embedding,
        float? thresholdOverride = null) => new(
        TenantId: TenantId,
        State: VoiceEnrollmentState.Enrolled,
        Embedding: embedding,
        EmbeddingDim: embedding.Length,
        ModelId: "fake-embedder-v1",
        SampleCount: 3,
        CreatedAt: DateTimeOffset.UtcNow,
        ConfirmedAt: DateTimeOffset.UtcNow,
        DeclinedAt: null,
        ThresholdOverride: thresholdOverride,
        FeatureEnabled: featureEnabled);

    private static short[] OneSecondOfPcm()
    {
        // Voiced sine so post-trim length is non-zero. Pure silence would be
        // trimmed to empty and yield Skipped(TooShort) regardless of state.
        var pcm = new short[16000];
        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)(short.MaxValue * 0.3f * MathF.Sin(2 * MathF.PI * 440 * i / 16000f));
        }
        return pcm;
    }

    private sealed class FakeEmbedder(float[] outputEmbedding) : ISpeakerEmbedder
    {
        public string ModelId => "fake-embedder-v1";

        public int EmbeddingDim => outputEmbedding.Length;

        public ValueTask<float[]> EmbedAsync(ReadOnlyMemory<short> pcm16Mono16k, CancellationToken ct)
            => ValueTask.FromResult((float[])outputEmbedding.Clone());
    }

    private sealed class ThrowingEmbedder : ISpeakerEmbedder
    {
        public string ModelId => "throwing-embedder";

        public int EmbeddingDim => 4;

        public ValueTask<float[]> EmbedAsync(ReadOnlyMemory<short> pcm16Mono16k, CancellationToken ct)
            => throw new InvalidOperationException("simulated failure");
    }
}
