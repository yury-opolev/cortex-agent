namespace Cortex.Contained.Agent.Host.Tests.SpeakerId;

using Cortex.Contained.Agent.Host.SpeakerId;
using Cortex.Contained.Speech.SpeakerId;

public sealed class EnrollmentOrchestratorTests
{
    private const string TenantId = "tenant-a";

    [Fact]
    public async Task GetState_NewTenant_ReturnsUnknown()
    {
        var (orch, _, _) = MakeOrchestrator();
        var state = await orch.GetStateAsync(TenantId, CancellationToken.None);
        Assert.Equal(VoiceEnrollmentState.Unknown, state);
    }

    [Fact]
    public async Task TryStart_FromUnknown_TransitionsToEnrolling()
    {
        var (orch, _, _) = MakeOrchestrator();
        var outcome = await orch.TryStartAsync(TenantId, CancellationToken.None);

        var t = Assert.IsType<EnrollmentOutcome.Transitioned>(outcome);
        Assert.Equal(VoiceEnrollmentState.Unknown, t.From);
        Assert.Equal(VoiceEnrollmentState.Enrolling, t.To);
        Assert.Equal(VoiceEnrollmentState.Enrolling, await orch.GetStateAsync(TenantId, CancellationToken.None));
    }

    [Fact]
    public async Task TryStart_FromDeclined_TransitionsToEnrolling()
    {
        var (orch, store, _) = MakeOrchestrator();
        await store.UpsertAsync(DeclinedRecord(), CancellationToken.None);

        var outcome = await orch.TryStartAsync(TenantId, CancellationToken.None);
        Assert.IsType<EnrollmentOutcome.Transitioned>(outcome);
    }

    [Fact]
    public async Task TryStart_FromEnrolled_IsInvalidState()
    {
        var (orch, store, _) = MakeOrchestrator();
        await store.UpsertAsync(EnrolledRecord([1, 0, 0, 0]), CancellationToken.None);

        var outcome = await orch.TryStartAsync(TenantId, CancellationToken.None);
        Assert.IsType<EnrollmentOutcome.InvalidState>(outcome);
    }

    [Fact]
    public async Task TryDecline_FromUnknown_TransitionsToDeclined()
    {
        var (orch, _, _) = MakeOrchestrator();
        var outcome = await orch.TryDeclineAsync(TenantId, CancellationToken.None);
        Assert.IsType<EnrollmentOutcome.Transitioned>(outcome);
        Assert.Equal(VoiceEnrollmentState.Declined, await orch.GetStateAsync(TenantId, CancellationToken.None));
    }

    [Fact]
    public async Task TryDecline_FromOtherState_IsInvalidState()
    {
        var (orch, store, _) = MakeOrchestrator();
        await store.UpsertAsync(EnrolledRecord([1, 0, 0, 0]), CancellationToken.None);

        var outcome = await orch.TryDeclineAsync(TenantId, CancellationToken.None);
        Assert.IsType<EnrollmentOutcome.InvalidState>(outcome);
    }

    [Fact]
    public async Task TryCancel_FromEnrolling_RevertsToUnknown()
    {
        var (orch, _, _) = MakeOrchestrator();
        await orch.TryStartAsync(TenantId, CancellationToken.None);

        var outcome = await orch.TryCancelAsync(TenantId, CancellationToken.None);
        Assert.IsType<EnrollmentOutcome.Transitioned>(outcome);
        Assert.Equal(VoiceEnrollmentState.Unknown, await orch.GetStateAsync(TenantId, CancellationToken.None));
    }

    [Fact]
    public async Task TryCancel_FromPendingReenroll_RestoresEnrolledWithoutChangingVoiceprint()
    {
        var (orch, store, _) = MakeOrchestrator();
        await store.UpsertAsync(EnrolledRecord([1, 0, 0, 0]), CancellationToken.None);

        await orch.TryRequestReenrollAsync(TenantId, CancellationToken.None);
        var outcome = await orch.TryCancelAsync(TenantId, CancellationToken.None);

        Assert.IsType<EnrollmentOutcome.Transitioned>(outcome);
        var record = await store.GetAsync(TenantId, CancellationToken.None);
        Assert.Equal(VoiceEnrollmentState.Enrolled, record!.State);
        Assert.NotNull(record.Embedding);
    }

    [Fact]
    public async Task TryRequestReenroll_FromEnrolled_KeepsVoiceprint()
    {
        var (orch, store, _) = MakeOrchestrator();
        await store.UpsertAsync(EnrolledRecord([1, 0, 0, 0]), CancellationToken.None);

        var outcome = await orch.TryRequestReenrollAsync(TenantId, CancellationToken.None);
        Assert.IsType<EnrollmentOutcome.Transitioned>(outcome);

        var record = await store.GetAsync(TenantId, CancellationToken.None);
        Assert.Equal(VoiceEnrollmentState.PendingReenroll, record!.State);
        Assert.NotNull(record.Embedding);
    }

    [Fact]
    public async Task TryConfirmReenroll_FromPendingReenroll_WipesAndMovesToEnrolling()
    {
        var (orch, store, _) = MakeOrchestrator();
        await store.UpsertAsync(EnrolledRecord([1, 0, 0, 0]), CancellationToken.None);

        await orch.TryRequestReenrollAsync(TenantId, CancellationToken.None);
        var outcome = await orch.TryConfirmReenrollAsync(TenantId, CancellationToken.None);

        Assert.IsType<EnrollmentOutcome.Transitioned>(outcome);
        var record = await store.GetAsync(TenantId, CancellationToken.None);
        Assert.Equal(VoiceEnrollmentState.Enrolling, record!.State);
        Assert.Null(record.Embedding);
    }

    [Fact]
    public async Task TryForget_FromEnrolled_WipesAndMovesToDeclined()
    {
        var (orch, store, _) = MakeOrchestrator();
        await store.UpsertAsync(EnrolledRecord([1, 0, 0, 0]), CancellationToken.None);

        var outcome = await orch.TryForgetAsync(TenantId, CancellationToken.None);
        Assert.IsType<EnrollmentOutcome.Transitioned>(outcome);

        var record = await store.GetAsync(TenantId, CancellationToken.None);
        Assert.Equal(VoiceEnrollmentState.Declined, record!.State);
        Assert.Null(record.Embedding);
        Assert.NotNull(record.DeclinedAt);
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static (EnrollmentOrchestrator Orch, IVoiceprintStore Store, object Unused) MakeOrchestrator(float[]? fakeEmbedding = null)
    {
        var store = new InMemoryVoiceprintStore();
        var orch = new EnrollmentOrchestrator(store);
        return (orch, store, new object());
    }

    private static VoiceprintRecord DeclinedRecord() => new(
        TenantId: TenantId,
        State: VoiceEnrollmentState.Declined,
        Embedding: null,
        EmbeddingDim: 0,
        ModelId: null,
        SampleCount: 0,
        CreatedAt: DateTimeOffset.UtcNow,
        ConfirmedAt: null,
        DeclinedAt: DateTimeOffset.UtcNow,
        ThresholdOverride: null,
        FeatureEnabled: true);

    private static VoiceprintRecord EnrolledRecord(float[] embedding) => new(
        TenantId: TenantId,
        State: VoiceEnrollmentState.Enrolled,
        Embedding: embedding,
        EmbeddingDim: embedding.Length,
        ModelId: "fake-embedder-v1",
        SampleCount: 3,
        CreatedAt: DateTimeOffset.UtcNow,
        ConfirmedAt: DateTimeOffset.UtcNow,
        DeclinedAt: null,
        ThresholdOverride: null,
        FeatureEnabled: true);
}
