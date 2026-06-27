namespace Cortex.Contained.Bridge.Tests.SpeakerId;

using Cortex.Contained.Bridge.SpeakerId;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Speech.SpeakerId;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class SignalRVoiceprintCacheTests
{
    private static SignalRVoiceprintCache MakeCache(out FakeTenantRouter fake)
    {
        fake = new FakeTenantRouter();
        return new SignalRVoiceprintCache(fake, NullLogger<SignalRVoiceprintCache>.Instance);
    }

    private static VoiceprintSnapshotDto MakeDto(string tenantId)
        => new(
            TenantId: tenantId,
            StateName: "Enrolled",
            Embedding: new float[192],
            EmbeddingDim: 192,
            ModelId: "eres2netv2-base",
            ThresholdOverride: null,
            FeatureEnabled: true);

    [Fact]
    public async Task GetAsync_NoClientForTenant_ReturnsNull()
    {
        var sut = MakeCache(out var router);
        router.SetClient("tenant-1", null);

        var record = await sut.GetAsync("tenant-1", CancellationToken.None);

        Assert.Null(record);
    }

    [Fact]
    public async Task GetAsync_ClientReturnsDto_ReturnsRecord()
    {
        var sut = MakeCache(out var router);
        var client = new FakeHubClient(MakeDto("tenant-1"));
        router.SetClient("tenant-1", client);

        var record = await sut.GetAsync("tenant-1", CancellationToken.None);

        Assert.NotNull(record);
        Assert.Equal("tenant-1", record!.TenantId);
        Assert.Equal(VoiceEnrollmentState.Enrolled, record.State);
        Assert.Equal(1, client.GetVoiceprintCallCount);
    }

    [Fact]
    public async Task GetAsync_CacheHit_DoesNotCallHub()
    {
        var sut = MakeCache(out var router);
        var client = new FakeHubClient(MakeDto("tenant-1"));
        router.SetClient("tenant-1", client);

        _ = await sut.GetAsync("tenant-1", CancellationToken.None);
        _ = await sut.GetAsync("tenant-1", CancellationToken.None);

        Assert.Equal(1, client.GetVoiceprintCallCount);
    }

    [Fact]
    public async Task Invalidate_ForcesReFetch()
    {
        var sut = MakeCache(out var router);
        var client = new FakeHubClient(MakeDto("tenant-1"));
        router.SetClient("tenant-1", client);

        _ = await sut.GetAsync("tenant-1", CancellationToken.None);
        sut.Invalidate("tenant-1");
        _ = await sut.GetAsync("tenant-1", CancellationToken.None);

        Assert.Equal(2, client.GetVoiceprintCallCount);
    }

    [Fact]
    public async Task GetAsync_HubThrows_ReturnsNullAndDoesNotCache()
    {
        var sut = MakeCache(out var router);
        var client = new ThrowingHubClient();
        router.SetClient("tenant-1", client);

        var first = await sut.GetAsync("tenant-1", CancellationToken.None);
        var second = await sut.GetAsync("tenant-1", CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(2, client.GetVoiceprintCallCount);
    }

    private sealed class FakeTenantRouter : ITenantRouterForCache
    {
        private readonly Dictionary<string, IVoiceprintHubClient?> clients = new(StringComparer.Ordinal);

        public void SetClient(string tenantId, IVoiceprintHubClient? client) => this.clients[tenantId] = client;

        public IVoiceprintHubClient? GetClient(string tenantId) => this.clients.GetValueOrDefault(tenantId);
    }

    private sealed class FakeHubClient : IVoiceprintHubClient
    {
        private readonly VoiceprintSnapshotDto dto;

        public int GetVoiceprintCallCount { get; private set; }

        public FakeHubClient(VoiceprintSnapshotDto dto) { this.dto = dto; }

        public Task<VoiceprintSnapshotDto?> GetVoiceprintAsync(string tenantId, CancellationToken ct)
        {
            this.GetVoiceprintCallCount++;
            return Task.FromResult<VoiceprintSnapshotDto?>(this.dto);
        }
    }

    private sealed class ThrowingHubClient : IVoiceprintHubClient
    {
        public int GetVoiceprintCallCount { get; private set; }

        public Task<VoiceprintSnapshotDto?> GetVoiceprintAsync(string tenantId, CancellationToken ct)
        {
            this.GetVoiceprintCallCount++;
            throw new InvalidOperationException("simulated hub failure");
        }
    }
}
