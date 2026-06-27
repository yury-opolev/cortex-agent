namespace Cortex.Contained.Channels.Discord.Tests;

using Cortex.Contained.Channels.Discord;
using Cortex.Contained.Contracts.Messages;
using Cortex.Contained.Speech.SpeakerId;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Handler-level tests for the post-STT dispatch decision in
/// <see cref="DiscordVoiceHandler.TryDispatchUtteranceAsync"/>. These drive
/// the actual production dispatch path (the one wired into
/// <c>ProcessUserAudioAsync</c>) and verify the contract that
/// <c>onTranscription</c> is invoked exactly when, and only when, the
/// speaker-verification gate permits.
/// </summary>
public sealed class DiscordVoiceHandlerDispatchTests
{
    private const string TenantId = "tenant-a";
    private const ulong UserId = 42UL;
    private const string DisplayName = "Yuri";

    [Fact]
    public async Task RejectGate_DoesNotInvokeOnTranscription()
    {
        var recorder = new DispatchRecorder();

        var outcome = await DiscordVoiceHandler.TryDispatchUtteranceAsync(
            NullLogger.Instance,
            utteranceId: "u1",
            userId: UserId,
            whisperMs: 100,
            transcription: "hello world",
            gateDecision: new VoiceGateDecision(PassesTranscript: false, Result: new VerificationResult.Reject(0.18f)),
            tenantId: TenantId,
            displayName: DisplayName,
            recorder.OnTranscription);

        Assert.Equal(DiscordVoiceHandler.UtteranceDispatchOutcome.SkippedRejected, outcome);
        Assert.Empty(recorder.Dispatched);
    }

    [Fact]
    public async Task EmptyTranscription_DoesNotInvokeOnTranscription()
    {
        var recorder = new DispatchRecorder();

        var outcome = await DiscordVoiceHandler.TryDispatchUtteranceAsync(
            NullLogger.Instance,
            utteranceId: "u2",
            userId: UserId,
            whisperMs: 100,
            transcription: "   ",
            gateDecision: new VoiceGateDecision(PassesTranscript: true, Result: new VerificationResult.Accept(0.9f)),
            tenantId: TenantId,
            displayName: DisplayName,
            recorder.OnTranscription);

        Assert.Equal(DiscordVoiceHandler.UtteranceDispatchOutcome.SkippedEmpty, outcome);
        Assert.Empty(recorder.Dispatched);
    }

    [Fact]
    public async Task NullTranscription_DoesNotInvokeOnTranscription()
    {
        var recorder = new DispatchRecorder();

        var outcome = await DiscordVoiceHandler.TryDispatchUtteranceAsync(
            NullLogger.Instance,
            utteranceId: "u3",
            userId: UserId,
            whisperMs: 100,
            transcription: null,
            gateDecision: new VoiceGateDecision(PassesTranscript: true, Result: null),
            tenantId: TenantId,
            displayName: DisplayName,
            recorder.OnTranscription);

        Assert.Equal(DiscordVoiceHandler.UtteranceDispatchOutcome.SkippedEmpty, outcome);
        Assert.Empty(recorder.Dispatched);
    }

    [Fact]
    public async Task AcceptGate_InvokesOnTranscriptionWithCorrectMessage()
    {
        var recorder = new DispatchRecorder();

        var outcome = await DiscordVoiceHandler.TryDispatchUtteranceAsync(
            NullLogger.Instance,
            utteranceId: "u4",
            userId: UserId,
            whisperMs: 100,
            transcription: "hello world",
            gateDecision: new VoiceGateDecision(PassesTranscript: true, Result: new VerificationResult.Accept(0.93f)),
            tenantId: TenantId,
            displayName: DisplayName,
            recorder.OnTranscription);

        Assert.Equal(DiscordVoiceHandler.UtteranceDispatchOutcome.Dispatched, outcome);
        var message = Assert.Single(recorder.Dispatched);
        Assert.Equal("hello world", message.Content.Text);
        Assert.Equal(UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), message.Sender.Id);
        Assert.Equal(DisplayName, message.Sender.DisplayName);
        Assert.True(message.IsGroup);
        Assert.Equal($"discord-voice-{TenantId}", message.ConversationId);
        Assert.NotNull(message.Properties);
        Assert.Equal("true", message.Properties!["voice"]);
        Assert.Equal(TenantId, message.Properties["tenantId"]);
    }

    [Fact]
    public async Task NotEnrolledGate_InvokesOnTranscription()
    {
        var recorder = new DispatchRecorder();

        var outcome = await DiscordVoiceHandler.TryDispatchUtteranceAsync(
            NullLogger.Instance,
            utteranceId: "u5",
            userId: UserId,
            whisperMs: 100,
            transcription: "hello",
            gateDecision: new VoiceGateDecision(PassesTranscript: true, Result: VerificationResult.NotEnrolled),
            tenantId: TenantId,
            displayName: DisplayName,
            recorder.OnTranscription);

        Assert.Equal(DiscordVoiceHandler.UtteranceDispatchOutcome.Dispatched, outcome);
        Assert.Single(recorder.Dispatched);
    }

    [Theory]
    [InlineData(VerificationResult.SkipReason.FeatureOff)]
    [InlineData(VerificationResult.SkipReason.EnrollmentInProgress)]
    [InlineData(VerificationResult.SkipReason.TooShort)]
    [InlineData(VerificationResult.SkipReason.Error)]
    public async Task SkippedGate_InvokesOnTranscription(VerificationResult.SkipReason reason)
    {
        var recorder = new DispatchRecorder();

        var outcome = await DiscordVoiceHandler.TryDispatchUtteranceAsync(
            NullLogger.Instance,
            utteranceId: "u6",
            userId: UserId,
            whisperMs: 100,
            transcription: "hello",
            gateDecision: new VoiceGateDecision(PassesTranscript: true, Result: new VerificationResult.Skipped(reason)),
            tenantId: TenantId,
            displayName: DisplayName,
            recorder.OnTranscription);

        Assert.Equal(DiscordVoiceHandler.UtteranceDispatchOutcome.Dispatched, outcome);
        Assert.Single(recorder.Dispatched);
    }

    [Fact]
    public async Task NullGateResult_PassesTranscript_StillDispatches()
    {
        // Defence-in-depth: if a future gate path returns PassesTranscript=true
        // without a Result (e.g. null verifier), dispatch still happens.
        var recorder = new DispatchRecorder();

        var outcome = await DiscordVoiceHandler.TryDispatchUtteranceAsync(
            NullLogger.Instance,
            utteranceId: "u7",
            userId: UserId,
            whisperMs: 100,
            transcription: "hello",
            gateDecision: new VoiceGateDecision(PassesTranscript: true, Result: null),
            tenantId: TenantId,
            displayName: DisplayName,
            recorder.OnTranscription);

        Assert.Equal(DiscordVoiceHandler.UtteranceDispatchOutcome.Dispatched, outcome);
        Assert.Single(recorder.Dispatched);
    }

    private sealed class DispatchRecorder
    {
        public List<InboundMessage> Dispatched { get; } = new();

        public Task OnTranscription(InboundMessage message)
        {
            this.Dispatched.Add(message);
            return Task.CompletedTask;
        }
    }
}
