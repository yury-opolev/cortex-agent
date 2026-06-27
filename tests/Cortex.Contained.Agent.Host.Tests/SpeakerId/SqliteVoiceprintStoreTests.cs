namespace Cortex.Contained.Agent.Host.Tests.SpeakerId;

using Cortex.Contained.Agent.Host.SpeakerId;
using Cortex.Contained.Speech.SpeakerId;

public sealed class SqliteVoiceprintStoreTests : IDisposable
{
    private readonly string tempDir;
    private readonly SqliteVoiceprintStore store;

    public SqliteVoiceprintStoreTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), "cortex-voiceprint-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.tempDir);
        this.store = new SqliteVoiceprintStore(this.tempDir);
    }

    public void Dispose()
    {
        this.store.Dispose();
        try
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task GetAsync_MissingTenant_ReturnsNull()
    {
        var result = await this.store.GetAsync("tenant-a", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task UpsertAsync_ThenGetAsync_RoundTripsScalars()
    {
        var record = MakeEnrolled("tenant-a");
        await this.store.UpsertAsync(record, CancellationToken.None);

        var fetched = await this.store.GetAsync("tenant-a", CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal("tenant-a", fetched!.TenantId);
        Assert.Equal(VoiceEnrollmentState.Enrolled, fetched.State);
        Assert.Equal(3, fetched.SampleCount);
        Assert.Equal("test-model-v1", fetched.ModelId);
        Assert.Equal(record.CreatedAt, fetched.CreatedAt);
        Assert.Equal(record.ConfirmedAt, fetched.ConfirmedAt);
        Assert.Null(fetched.DeclinedAt);
        Assert.Null(fetched.ThresholdOverride);
        Assert.True(fetched.FeatureEnabled);
    }

    [Fact]
    public async Task UpsertAsync_RoundTripsEmbeddingBytes()
    {
        var embedding = new float[] { 0.1f, -0.2f, 0.3f, -0.4f };
        var record = MakeEnrolled("tenant-a") with { Embedding = embedding, EmbeddingDim = embedding.Length };

        await this.store.UpsertAsync(record, CancellationToken.None);
        var fetched = await this.store.GetAsync("tenant-a", CancellationToken.None);

        Assert.NotNull(fetched!.Embedding);
        Assert.Equal(embedding.Length, fetched.EmbeddingDim);
        Assert.Equal(embedding.Length, fetched.Embedding!.Length);
        for (var i = 0; i < embedding.Length; i++)
        {
            Assert.Equal(embedding[i], fetched.Embedding[i], precision: 6);
        }
    }

    [Fact]
    public async Task UpsertAsync_NonEnrolledStateHasNullEmbedding()
    {
        var record = new VoiceprintRecord(
            TenantId: "tenant-a",
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

        await this.store.UpsertAsync(record, CancellationToken.None);
        var fetched = await this.store.GetAsync("tenant-a", CancellationToken.None);

        Assert.Null(fetched!.Embedding);
        Assert.Equal(0, fetched.EmbeddingDim);
        Assert.Null(fetched.ModelId);
        Assert.Equal(VoiceEnrollmentState.Declined, fetched.State);
    }

    [Fact]
    public async Task UpsertAsync_OverwritesExistingRow()
    {
        var first = MakeEnrolled("tenant-a") with { State = VoiceEnrollmentState.Unknown };
        var second = MakeEnrolled("tenant-a");

        await this.store.UpsertAsync(first, CancellationToken.None);
        await this.store.UpsertAsync(second, CancellationToken.None);

        var fetched = await this.store.GetAsync("tenant-a", CancellationToken.None);
        Assert.Equal(VoiceEnrollmentState.Enrolled, fetched!.State);
    }

    [Fact]
    public async Task DeleteAsync_RemovesRecord()
    {
        await this.store.UpsertAsync(MakeEnrolled("tenant-a"), CancellationToken.None);

        await this.store.DeleteAsync("tenant-a", CancellationToken.None);
        var fetched = await this.store.GetAsync("tenant-a", CancellationToken.None);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task DeleteAsync_MissingTenant_DoesNotThrow()
    {
        await this.store.DeleteAsync("never-existed", CancellationToken.None);
    }

    [Fact]
    public async Task ThresholdOverride_RoundTrips()
    {
        var record = MakeEnrolled("tenant-a") with { ThresholdOverride = 0.65f };
        await this.store.UpsertAsync(record, CancellationToken.None);

        var fetched = await this.store.GetAsync("tenant-a", CancellationToken.None);
        Assert.Equal(0.65f, fetched!.ThresholdOverride);
    }

    [Fact]
    public async Task FeatureEnabledFalse_RoundTrips()
    {
        var record = MakeEnrolled("tenant-a") with { FeatureEnabled = false };
        await this.store.UpsertAsync(record, CancellationToken.None);

        var fetched = await this.store.GetAsync("tenant-a", CancellationToken.None);
        Assert.False(fetched!.FeatureEnabled);
    }

    [Fact]
    public async Task Tenants_AreIsolated()
    {
        await this.store.UpsertAsync(MakeEnrolled("tenant-a"), CancellationToken.None);
        await this.store.UpsertAsync(MakeEnrolled("tenant-b") with { State = VoiceEnrollmentState.Declined }, CancellationToken.None);

        var a = await this.store.GetAsync("tenant-a", CancellationToken.None);
        var b = await this.store.GetAsync("tenant-b", CancellationToken.None);

        Assert.Equal(VoiceEnrollmentState.Enrolled, a!.State);
        Assert.Equal(VoiceEnrollmentState.Declined, b!.State);
    }

    private static VoiceprintRecord MakeEnrolled(string tenantId) => new(
        TenantId: tenantId,
        State: VoiceEnrollmentState.Enrolled,
        Embedding: [1.0f, 0.0f, 0.0f],
        EmbeddingDim: 3,
        ModelId: "test-model-v1",
        SampleCount: 3,
        CreatedAt: new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero),
        ConfirmedAt: new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero),
        DeclinedAt: null,
        ThresholdOverride: null,
        FeatureEnabled: true);
}
