namespace Cortex.Contained.Agent.Host.Tests.SpeakerId;

using Cortex.Contained.Agent.Host.SpeakerId;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Speech.SpeakerId;

public sealed class VoiceEnrollmentToolsTests
{
    private const string TenantId = "tenant-a";
    private const string VoiceConvId = "discord-voice-tenant-a";

    [Fact]
    public async Task StartTool_FromUnknown_TransitionsToEnrolling()
    {
        var (orch, _) = MakeOrchestrator();
        var tool = new StartVoiceEnrollmentTool(orch);

        var result = await tool.ExecuteAsync("{}", VoiceContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("\"to\":\"Enrolling\"", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartTool_NonVoiceConversation_Refuses()
    {
        var (orch, _) = MakeOrchestrator();
        var tool = new StartVoiceEnrollmentTool(orch);

        var result = await tool.ExecuteAsync("{}", new ToolExecutionContext { ConversationId = "webchat-default", ChannelId = "webchat-default" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("only available in voice conversations", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeclineTool_FromUnknown_TransitionsToDeclined()
    {
        var (orch, _) = MakeOrchestrator();
        var tool = new DeclineVoiceEnrollmentTool(orch);

        var result = await tool.ExecuteAsync("{}", VoiceContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("\"to\":\"Declined\"", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeclineTool_FromEnrolled_ReportsInvalidState()
    {
        var (orch, store) = MakeOrchestrator();
        await store.UpsertAsync(EnrolledRecord(), CancellationToken.None);
        var tool = new DeclineVoiceEnrollmentTool(orch);

        var result = await tool.ExecuteAsync("{}", VoiceContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("invalid_state", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelTool_FromEnrolling_ReturnsToUnknown()
    {
        var (orch, _) = MakeOrchestrator();
        await orch.TryStartAsync(TenantId, CancellationToken.None);
        var tool = new CancelVoiceEnrollmentTool(orch);

        var result = await tool.ExecuteAsync("{}", VoiceContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("\"to\":\"Unknown\"", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestReenrollTool_FromEnrolled_TransitionsToPendingReenroll()
    {
        var (orch, store) = MakeOrchestrator();
        await store.UpsertAsync(EnrolledRecord(), CancellationToken.None);
        var tool = new RequestVoiceReenrollmentTool(orch);

        var result = await tool.ExecuteAsync("{}", VoiceContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("\"to\":\"PendingReenroll\"", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmReenrollTool_FromPendingReenroll_WipesAndStartsEnrolling()
    {
        var (orch, store) = MakeOrchestrator();
        await store.UpsertAsync(EnrolledRecord(), CancellationToken.None);
        await orch.TryRequestReenrollAsync(TenantId, CancellationToken.None);
        var tool = new ConfirmVoiceReenrollmentTool(orch);

        var result = await tool.ExecuteAsync("{}", VoiceContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("\"to\":\"Enrolling\"", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgetTool_FromEnrolled_WipesAndDeclines()
    {
        var (orch, store) = MakeOrchestrator();
        await store.UpsertAsync(EnrolledRecord(), CancellationToken.None);
        var tool = new ForgetVoiceEnrollmentTool(orch);

        var result = await tool.ExecuteAsync("{}", VoiceContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("\"to\":\"Declined\"", result.Content, StringComparison.Ordinal);
        var record = await store.GetAsync(TenantId, CancellationToken.None);
        Assert.Null(record!.Embedding);
    }

    [Theory]
    [InlineData("start_voice_enrollment")]
    [InlineData("decline_voice_enrollment")]
    [InlineData("cancel_voice_enrollment")]
    [InlineData("request_voice_reenrollment")]
    [InlineData("confirm_voice_reenrollment")]
    [InlineData("forget_voice_enrollment")]
    public void AllTools_Listed_InToolsForState_Matrix(string toolName)
    {
        // Every tool must appear in the state matrix for at least one state.
        var anyState = false;
        foreach (var state in Enum.GetValues<VoiceEnrollmentState>())
        {
            if (VoiceEnrollmentToolHelpers.ToolsForState(state).Contains(toolName))
            {
                anyState = true;
                break;
            }
        }
        Assert.True(anyState, $"{toolName} is not exposed in any state.");
    }

    [Fact]
    public void ToolsForState_MatchesSpec()
    {
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "start_voice_enrollment", "decline_voice_enrollment" },
            VoiceEnrollmentToolHelpers.ToolsForState(VoiceEnrollmentState.Unknown));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "start_voice_enrollment" },
            VoiceEnrollmentToolHelpers.ToolsForState(VoiceEnrollmentState.Declined));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "cancel_voice_enrollment" },
            VoiceEnrollmentToolHelpers.ToolsForState(VoiceEnrollmentState.Enrolling));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "cancel_voice_enrollment" },
            VoiceEnrollmentToolHelpers.ToolsForState(VoiceEnrollmentState.Confirming));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "request_voice_reenrollment", "forget_voice_enrollment" },
            VoiceEnrollmentToolHelpers.ToolsForState(VoiceEnrollmentState.Enrolled));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "confirm_voice_reenrollment", "cancel_voice_enrollment" },
            VoiceEnrollmentToolHelpers.ToolsForState(VoiceEnrollmentState.PendingReenroll));
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static (EnrollmentOrchestrator Orch, IVoiceprintStore Store) MakeOrchestrator()
    {
        var store = new InMemoryVoiceprintStore();
        var orch = new EnrollmentOrchestrator(store);
        return (orch, store);
    }

    private static ToolExecutionContext VoiceContext() => new()
    {
        ConversationId = VoiceConvId,
        ChannelId = "discord-voice",
    };

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
