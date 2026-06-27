namespace Cortex.Contained.Speech.Tests.SpeakerId;

using Cortex.Contained.Speech.SpeakerId;

public sealed class InMemoryVoiceprintStoreTests
{
    [Fact]
    public async Task GetAsync_MissingTenant_ReturnsNull()
    {
        var store = new InMemoryVoiceprintStore();
        var result = await store.GetAsync("tenant-a", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task UpsertAsync_ThenGetAsync_ReturnsStoredRecord()
    {
        var store = new InMemoryVoiceprintStore();
        var record = MakeRecord("tenant-a", VoiceEnrollmentState.Enrolled);

        await store.UpsertAsync(record, CancellationToken.None);
        var fetched = await store.GetAsync("tenant-a", CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal(record, fetched);
    }

    [Fact]
    public async Task UpsertAsync_OverwritesPreviousRecord()
    {
        var store = new InMemoryVoiceprintStore();
        var first = MakeRecord("tenant-a", VoiceEnrollmentState.Unknown);
        var second = MakeRecord("tenant-a", VoiceEnrollmentState.Enrolled);

        await store.UpsertAsync(first, CancellationToken.None);
        await store.UpsertAsync(second, CancellationToken.None);
        var fetched = await store.GetAsync("tenant-a", CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal(VoiceEnrollmentState.Enrolled, fetched!.State);
    }

    [Fact]
    public async Task DeleteAsync_RemovesRecord()
    {
        var store = new InMemoryVoiceprintStore();
        var record = MakeRecord("tenant-a", VoiceEnrollmentState.Enrolled);
        await store.UpsertAsync(record, CancellationToken.None);

        await store.DeleteAsync("tenant-a", CancellationToken.None);
        var fetched = await store.GetAsync("tenant-a", CancellationToken.None);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task DeleteAsync_MissingTenant_DoesNotThrow()
    {
        var store = new InMemoryVoiceprintStore();
        await store.DeleteAsync("tenant-a", CancellationToken.None);
    }

    [Fact]
    public async Task TenantsAreIsolated()
    {
        var store = new InMemoryVoiceprintStore();
        await store.UpsertAsync(MakeRecord("tenant-a", VoiceEnrollmentState.Enrolled), CancellationToken.None);
        await store.UpsertAsync(MakeRecord("tenant-b", VoiceEnrollmentState.Declined), CancellationToken.None);

        var a = await store.GetAsync("tenant-a", CancellationToken.None);
        var b = await store.GetAsync("tenant-b", CancellationToken.None);

        Assert.Equal(VoiceEnrollmentState.Enrolled, a!.State);
        Assert.Equal(VoiceEnrollmentState.Declined, b!.State);
    }

    private static VoiceprintRecord MakeRecord(string tenantId, VoiceEnrollmentState state) =>
        new(
            TenantId: tenantId,
            State: state,
            Embedding: state == VoiceEnrollmentState.Enrolled ? [1.0f, 0.0f, 0.0f] : null,
            EmbeddingDim: state == VoiceEnrollmentState.Enrolled ? 3 : 0,
            ModelId: state == VoiceEnrollmentState.Enrolled ? "test-model-v1" : null,
            SampleCount: state == VoiceEnrollmentState.Enrolled ? 3 : 0,
            CreatedAt: DateTimeOffset.UtcNow,
            ConfirmedAt: state == VoiceEnrollmentState.Enrolled ? DateTimeOffset.UtcNow : null,
            DeclinedAt: state == VoiceEnrollmentState.Declined ? DateTimeOffset.UtcNow : null,
            ThresholdOverride: null,
            FeatureEnabled: true);
}
