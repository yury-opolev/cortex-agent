namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Pure, total turn-arbitration policy. Mirrors the EndOfTurnDecision /
/// VoiceStateRouter pattern: no Discord/audio dependencies, exhaustively
/// unit-tested. See docs/superpowers/specs/2026-05-16-voice-turn-arbitration-design.md.
/// </summary>
internal static class TurnDecision
{
    public static TurnAction Decide(
        ConversationPhase phase,
        TurnEvent evt,
        InterruptClass cls,
        bool bargeInEnabled)
    {
        if (evt == TurnEvent.UserSpeechOnset)
        {
            return phase switch
            {
                ConversationPhase.Listening => TurnAction.Accumulate,
                ConversationPhase.Committed => TurnAction.CancelCommitAndReabsorb,
                ConversationPhase.Thinking => TurnAction.CancelGenAndReabsorb,
                ConversationPhase.Speaking => bargeInEnabled
                    ? TurnAction.StopPlaybackPendingClassify
                    : TurnAction.Ignore,
                _ => TurnAction.Ignore,
            };
        }

        if (evt == TurnEvent.ClassifierResult && phase == ConversationPhase.Speaking)
        {
            // Backchannel resumes; Real and resolved-Unsure take the floor.
            return cls == InterruptClass.Backchannel
                ? TurnAction.ResumeFromSentenceStart
                : TurnAction.CommitInterrupt;
        }

        return TurnAction.Ignore;
    }
}
