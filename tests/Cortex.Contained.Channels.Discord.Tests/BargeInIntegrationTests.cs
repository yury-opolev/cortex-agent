using Cortex.Contained.Channels.Discord;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Channels.Discord.Tests;

/// <summary>
/// Barge-in smoke: drives a real <see cref="VoiceTurnArbiter"/> in
/// <see cref="ConversationPhase.Speaking"/> through the exact actuator
/// callbacks the production handler wires (via
/// <see cref="DiscordVoiceHandler.BuildArbiterCallbacks"/>), with a fake
/// OnTurnInterrupted hub callback capturing the cross-process notification
/// and a StopForBargeIn returning a known <see cref="PlaybackProgress"/>.
///
/// Asserts the captured <see cref="TurnInterruptedNotification.PlayedText"/>
/// equals <see cref="PlaybackTruncation.BuildPlayedText"/> of that progress,
/// ends with the ellipsis marker, and carries the voice conversation id.
/// </summary>
public sealed class BargeInIntegrationTests
{
    private const string TenantId = "tenant-a";

    [Fact]
    public async Task SpeakingBargeIn_Real_InvokesOnTurnInterrupted_WithPlayedText()
    {
        var expectedConversationId = $"discord-voice-{TenantId}";
        var progress = new PlaybackProgress(
            ["First sentence.", "Second one."],
            "the quick brown fox jumps",
            0.5);

        TurnInterruptedNotification? captured = null;
        var abortCalls = new List<string>();

        // Build the callbacks EXACTLY as DiscordVoiceHandler wires them so this
        // test exercises the production CommitInterrupt / CancelGeneration path.
        var callbacks = DiscordVoiceHandler.BuildArbiterCallbacks(
            conversationId: expectedConversationId,
            stopPlayback: () => Task.FromResult(progress),
            reenqueueSentence: _ => { },
            classify: (_, _, _) => Task.FromResult(InterruptClass.Real),
            onTurnInterrupted: n =>
            {
                captured = n;
                return Task.CompletedTask;
            },
            onAbortGeneration: cid =>
            {
                abortCalls.Add(cid);
                return Task.CompletedTask;
            },
            logger: NullLogger.Instance);

        using var arbiter = new VoiceTurnArbiter(callbacks, bargeInEnabled: true, NullLogger.Instance);
        arbiter.SetPhase(ConversationPhase.Speaking);

        await arbiter.OnUserSpeechOnsetAsync("wait stop that", pEou: 0.6f, CancellationToken.None);

        var expectedPlayedText = PlaybackTruncation.BuildPlayedText(progress);

        Assert.NotNull(captured);
        Assert.Equal(expectedConversationId, captured!.ConversationId);
        Assert.Equal(expectedPlayedText, captured.PlayedText);
        Assert.EndsWith(PlaybackTruncation.Ellipsis, captured.PlayedText);
        Assert.Equal(ConversationPhase.Listening, arbiter.Phase);
        Assert.Empty(abortCalls);
    }

    [Fact]
    public async Task ThinkingBargeIn_InvokesAbortGeneration_WithConversationId()
    {
        var expectedConversationId = $"discord-voice-{TenantId}";
        TurnInterruptedNotification? captured = null;
        var abortCalls = new List<string>();

        var callbacks = DiscordVoiceHandler.BuildArbiterCallbacks(
            conversationId: expectedConversationId,
            stopPlayback: () => Task.FromResult(new PlaybackProgress([], null, 0.0)),
            reenqueueSentence: _ => { },
            classify: (_, _, _) => Task.FromResult(InterruptClass.Real),
            onTurnInterrupted: n =>
            {
                captured = n;
                return Task.CompletedTask;
            },
            onAbortGeneration: cid =>
            {
                abortCalls.Add(cid);
                return Task.CompletedTask;
            },
            logger: NullLogger.Instance);

        using var arbiter = new VoiceTurnArbiter(callbacks, bargeInEnabled: true, NullLogger.Instance);
        arbiter.SetPhase(ConversationPhase.Thinking);

        await arbiter.OnUserSpeechOnsetAsync("no wait", pEou: 0.3f, CancellationToken.None);

        Assert.Equal([expectedConversationId], abortCalls);
        Assert.Null(captured);
        Assert.Equal(ConversationPhase.Listening, arbiter.Phase);
    }
}
