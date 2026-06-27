namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Drives one wizard turn: embeds + captures the utterance via the session, then
/// speaks the next scripted line. On completion, pushes the finished voiceprint
/// (BEFORE speaking the done line so the Agent is enrolled by the time the user
/// hears confirmation). Pure orchestration — no Discord/SignalR types — so it is
/// fully unit-testable.
/// </summary>
public static class WizardTurnAdvancer
{
    /// <summary>Advance the wizard by one captured utterance.</summary>
    /// <param name="session">The active enrollment capture session.</param>
    /// <param name="pcm16Mono16k">Raw 16-bit mono 16 kHz PCM samples for the utterance.</param>
    /// <param name="speakAsync">Delegate that speaks a scripted line to the user.</param>
    /// <param name="submitVoiceprintAsync">Delegate that pushes the finished voiceprint (embedding, modelId) to the Agent.</param>
    /// <param name="samplesRequired">Enrollment samples required to build the candidate voiceprint.</param>
    /// <param name="matchesRequired">Confirmation matches required to finish enrollment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see cref="WizardAdvanceResult.Completed"/> once the voiceprint is submitted; otherwise <see cref="WizardAdvanceResult.InProgress"/>.</returns>
    public static async Task<WizardAdvanceResult> AdvanceAsync(
        WizardEnrollmentSession session,
        ReadOnlyMemory<short> pcm16Mono16k,
        Func<string, Task> speakAsync,
        Func<float[], string, Task> submitVoiceprintAsync,
        int samplesRequired,
        int matchesRequired,
        CancellationToken cancellationToken)
    {
        await session.AddUtteranceAsync(pcm16Mono16k, cancellationToken).ConfigureAwait(false);
        var line = WizardScriptMapper.LineFor(session.Phase, session.CapturedInPhase, samplesRequired, matchesRequired);

        if (session.Phase == WizardPhase.Complete)
        {
            await submitVoiceprintAsync(session.Voiceprint!, session.ModelId).ConfigureAwait(false);
            await speakAsync(line.Text).ConfigureAwait(false);
            return WizardAdvanceResult.Completed;
        }

        await speakAsync(line.Text).ConfigureAwait(false);
        return WizardAdvanceResult.InProgress;
    }
}
