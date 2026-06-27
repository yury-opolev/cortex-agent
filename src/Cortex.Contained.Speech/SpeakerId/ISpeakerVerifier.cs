namespace Cortex.Contained.Speech.SpeakerId;

/// <summary>
/// The verification gate. Channels call this once per finalised utterance
/// to decide whether the transcript should reach the agent.
/// </summary>
public interface ISpeakerVerifier
{
    /// <summary>
    /// Verifies <paramref name="pcm16Mono16k"/> against the enrolled voiceprint
    /// for <paramref name="tenantId"/>. See
    /// <see cref="VerificationResult"/> for the decision semantics.
    /// </summary>
    /// <param name="pcm16Mono16k">PCM16 mono audio at 16 kHz, already trimmed to voiced content.</param>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<VerificationResult> VerifyAsync(
        ReadOnlyMemory<short> pcm16Mono16k,
        string tenantId,
        CancellationToken cancellationToken);
}
