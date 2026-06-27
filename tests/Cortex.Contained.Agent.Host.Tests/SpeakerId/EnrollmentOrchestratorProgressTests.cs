namespace Cortex.Contained.Agent.Host.Tests.SpeakerId;

using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.SpeakerId;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Speech.SpeakerId;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class EnrollmentOrchestratorProgressTests
{
    private const string TenantId = "tenant-progress";

    [Fact]
    public async Task TryStartAsync_PushesProgressWithEnrollingState()
    {
        var (orch, bridgeClient) = MakeOrchestrator();

        var outcome = await orch.TryStartAsync(TenantId, CancellationToken.None);

        Assert.IsType<EnrollmentOutcome.Transitioned>(outcome);
        await bridgeClient.Received(1).OnVoiceEnrollmentProgress(
            TenantId,
            "Enrolling",
            0,
            EnrollmentOrchestrator.DefaultSamplesRequired);
    }

    [Fact]
    public async Task TryCancelAsync_PushesProgressWithUnknownState()
    {
        var (orch, bridgeClient) = MakeOrchestrator();
        await orch.TryStartAsync(TenantId, CancellationToken.None);

        var outcome = await orch.TryCancelAsync(TenantId, CancellationToken.None);

        Assert.IsType<EnrollmentOutcome.Transitioned>(outcome);
        await bridgeClient.Received(1).OnVoiceEnrollmentProgress(
            TenantId,
            "Unknown",
            0,
            EnrollmentOrchestrator.DefaultSamplesRequired);
    }

    [Fact]
    public async Task TryConfirmReenrollAsync_PushesEnrollingState()
    {
        var (orch, bridgeClient, store) = MakeOrchestratorWithStore();

        // Set up: move to Enrolled via store, then request reenroll to get to PendingReenroll.
        await store.UpsertAsync(EnrolledRecord([1, 0, 0, 0]), CancellationToken.None);
        await orch.TryRequestReenrollAsync(TenantId, CancellationToken.None);

        // Now confirm the reenroll.
        var outcome = await orch.TryConfirmReenrollAsync(TenantId, CancellationToken.None);

        Assert.IsType<EnrollmentOutcome.Transitioned>(outcome);
        await bridgeClient.Received(1).OnVoiceEnrollmentProgress(
            TenantId,
            "Enrolling",
            0,
            EnrollmentOrchestrator.DefaultSamplesRequired);
    }

    [Fact]
    public async Task TryForgetAsync_PushesDeclinedState()
    {
        var (orch, bridgeClient, store) = MakeOrchestratorWithStore();

        // Set up: move to Enrolled first via store.
        await store.UpsertAsync(EnrolledRecord([1, 0, 0, 0]), CancellationToken.None);

        var outcome = await orch.TryForgetAsync(TenantId, CancellationToken.None);

        Assert.IsType<EnrollmentOutcome.Transitioned>(outcome);
        await bridgeClient.Received(1).OnVoiceEnrollmentProgress(
            TenantId,
            "Declined",
            0,
            EnrollmentOrchestrator.DefaultSamplesRequired);
    }

    [Fact]
    public async Task WriteVoiceprintAsync_StoresEnrolledWithEmbeddingAndModel()
    {
        var (orch, _, store) = MakeOrchestratorWithStore();
        float[] vp = [0.5f, 0.5f, 0.5f, 0.5f];

        await orch.WriteVoiceprintAsync(TenantId, vp, "eres2netv2-base", CancellationToken.None);

        var rec = await store.GetAsync(TenantId, CancellationToken.None);
        Assert.NotNull(rec);
        Assert.Equal(VoiceEnrollmentState.Enrolled, rec!.State);
        Assert.Equal(vp, rec.Embedding);
        Assert.Equal("eres2netv2-base", rec.ModelId);
        Assert.Equal(4, rec.EmbeddingDim);
        Assert.NotNull(rec.ConfirmedAt);
    }

    [Fact]
    public async Task WriteVoiceprintAsync_PushesEnrolledProgress()
    {
        var (orch, bridgeClient, _) = MakeOrchestratorWithStore();

        await orch.WriteVoiceprintAsync(TenantId, [0.5f, 0.5f, 0.5f, 0.5f], "eres2netv2-base", CancellationToken.None);

        await bridgeClient.Received(1).OnVoiceEnrollmentProgress(
            TenantId,
            "Enrolled",
            EnrollmentOrchestrator.DefaultSamplesRequired,
            EnrollmentOrchestrator.DefaultSamplesRequired);
    }

    [Fact]
    public async Task WriteVoiceprintAsync_PreservesExistingThresholdOverride()
    {
        var (orch, _, store) = MakeOrchestratorWithStore();
        await store.UpsertAsync(EnrolledRecord([1, 0, 0, 0]) with { ThresholdOverride = 0.42f }, CancellationToken.None);

        await orch.WriteVoiceprintAsync(TenantId, [0.6f, 0.6f, 0.6f, 0.6f], "eres2netv2-base", CancellationToken.None);

        var rec = await store.GetAsync(TenantId, CancellationToken.None);
        Assert.Equal(0.42f, rec!.ThresholdOverride);
    }

    [Fact]
    public async Task WriteVoiceprintAsync_EmptyEmbedding_Throws()
    {
        var (orch, _, _) = MakeOrchestratorWithStore();
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await orch.WriteVoiceprintAsync(TenantId, Array.Empty<float>(), "m", CancellationToken.None));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static (EnrollmentOrchestrator Orch, IAgentHubClient BridgeClient) MakeOrchestrator()
    {
        var store = new InMemoryVoiceprintStore();
        var bridgeClient = Substitute.For<IAgentHubClient>();

        // Wire up BridgeClientAccessor the same way existing tests do (SendMessageToolTests pattern).
        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubClients.Client(Arg.Any<string>()).Returns(bridgeClient);
        hubContext.Clients.Returns(hubClients);

        var accessor = new BridgeClientAccessor(hubContext);
        accessor.SetConnectionId("test-conn");

        var orch = new EnrollmentOrchestrator(
            store,
            logger: NullLogger<EnrollmentOrchestrator>.Instance,
            bridgeClientAccessor: accessor);

        return (orch, bridgeClient);
    }

    private static (EnrollmentOrchestrator Orch, IAgentHubClient BridgeClient, InMemoryVoiceprintStore Store) MakeOrchestratorWithStore()
    {
        var store = new InMemoryVoiceprintStore();
        var bridgeClient = Substitute.For<IAgentHubClient>();

        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubClients.Client(Arg.Any<string>()).Returns(bridgeClient);
        hubContext.Clients.Returns(hubClients);

        var accessor = new BridgeClientAccessor(hubContext);
        accessor.SetConnectionId("test-conn");

        var orch = new EnrollmentOrchestrator(
            store,
            logger: NullLogger<EnrollmentOrchestrator>.Instance,
            bridgeClientAccessor: accessor);

        return (orch, bridgeClient, store);
    }

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
