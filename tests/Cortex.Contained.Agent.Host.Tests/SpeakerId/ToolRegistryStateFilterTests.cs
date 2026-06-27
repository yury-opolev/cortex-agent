namespace Cortex.Contained.Agent.Host.Tests.SpeakerId;

using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.SpeakerId;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Speech.SpeakerId;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class ToolRegistryStateFilterTests
{
    private const string TenantId = "tenant-a";
    private const string VoiceConvId = "discord-voice-tenant-a";

    [Fact]
    public void NonVoiceConversation_HidesAllEnrollmentTools()
    {
        var (registry, _) = MakeRegistry();

        var defs = registry.GetDefinitionsForConversation("webchat-default");

        foreach (var name in VoiceEnrollmentToolHelpers.AllToolNames)
        {
            Assert.DoesNotContain(defs, d => string.Equals(d.Name, name, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task VoiceConversation_UnknownState_ExposesOnlyStartAndDecline()
    {
        var (registry, orch) = MakeRegistry();
        // No prior interaction → cached state defaults to Unknown.
        _ = await orch.GetStateAsync(TenantId, CancellationToken.None);

        var defs = registry.GetDefinitionsForConversation(VoiceConvId);
        var names = defs.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("start_voice_enrollment", names);
        Assert.Contains("decline_voice_enrollment", names);
        Assert.DoesNotContain("finalize_voice_enrollment_capture", names);
        Assert.DoesNotContain("confirm_voice_enrollment", names);
        Assert.DoesNotContain("request_voice_reenrollment", names);
        Assert.DoesNotContain("forget_voice_enrollment", names);
    }

    [Fact]
    public async Task VoiceConversation_EnrollingState_ExposesOnlyCancel()
    {
        var (registry, orch) = MakeRegistry();
        await orch.TryStartAsync(TenantId, CancellationToken.None);

        var defs = registry.GetDefinitionsForConversation(VoiceConvId);
        var names = defs.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("cancel_voice_enrollment", names);
        Assert.DoesNotContain("finalize_voice_enrollment_capture", names);
        Assert.DoesNotContain("start_voice_enrollment", names);
        Assert.DoesNotContain("decline_voice_enrollment", names);
        Assert.DoesNotContain("forget_voice_enrollment", names);
    }

    [Fact]
    public async Task VoiceConversation_EnrolledState_ExposesRequestReenrollAndForget()
    {
        var (registry, orch, store) = MakeRegistryWithStore();
        await store.UpsertAsync(EnrolledRecord(), CancellationToken.None);
        _ = await orch.GetStateAsync(TenantId, CancellationToken.None);  // populate cache

        var defs = registry.GetDefinitionsForConversation(VoiceConvId);
        var names = defs.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("request_voice_reenrollment", names);
        Assert.Contains("forget_voice_enrollment", names);
        Assert.DoesNotContain("start_voice_enrollment", names);
        Assert.DoesNotContain("confirm_voice_enrollment", names);
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static (ToolRegistry Registry, EnrollmentOrchestrator Orch) MakeRegistry()
    {
        var (registry, orch, _) = MakeRegistryWithStore();
        return (registry, orch);
    }

    private static (ToolRegistry Registry, EnrollmentOrchestrator Orch, IVoiceprintStore Store) MakeRegistryWithStore()
    {
        var store = new InMemoryVoiceprintStore();
        var orch = new EnrollmentOrchestrator(store);

        var tools = new IAgentTool[]
        {
            new StartVoiceEnrollmentTool(orch),
            new DeclineVoiceEnrollmentTool(orch),
            new CancelVoiceEnrollmentTool(orch),
            new RequestVoiceReenrollmentTool(orch),
            new ConfirmVoiceReenrollmentTool(orch),
            new ForgetVoiceEnrollmentTool(orch),
        };
        var registry = new ToolRegistry(
            tools,
            new ActiveChannelStore(),
            NullLogger<ToolRegistry>.Instance,
            new IConversationToolGate[] { orch });
        return (registry, orch, store);
    }

    private static VoiceprintRecord EnrolledRecord() => new(
        TenantId: TenantId,
        State: VoiceEnrollmentState.Enrolled,
        Embedding: [1, 0, 0, 0],
        EmbeddingDim: 4,
        ModelId: "fake-embedder-v1",
        SampleCount: 3,
        CreatedAt: DateTimeOffset.UtcNow,
        ConfirmedAt: DateTimeOffset.UtcNow,
        DeclinedAt: null,
        ThresholdOverride: null,
        FeatureEnabled: true);
}
