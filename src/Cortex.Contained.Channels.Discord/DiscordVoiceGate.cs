namespace Cortex.Contained.Channels.Discord;

using Cortex.Contained.Speech.SpeakerId;

/// <summary>
/// Decision returned by <see cref="DiscordVoiceGate.EvaluateAsync"/>.
/// </summary>
public readonly record struct VoiceGateDecision(bool PassesTranscript, VerificationResult? Result);

/// <summary>
/// Wraps <see cref="ISpeakerVerifier.VerifyAsync"/> with the channel-side
/// "never block the user on infrastructure failure" policy. Pulled out so it
/// can be unit-tested without constructing the full
/// <see cref="DiscordVoiceHandler"/>.
/// </summary>
internal static class DiscordVoiceGate
{
    /// <summary>
    /// Hard timeout on the verifier call. If the embedder hangs (driver glitch,
    /// memory pressure, model load), the gate fails open after this duration so
    /// the user is never silenced by an unresponsive verification path.
    /// </summary>
    internal static readonly TimeSpan VerifyTimeout = TimeSpan.FromMilliseconds(1500);

    public static async ValueTask<VoiceGateDecision> EvaluateAsync(
        ISpeakerVerifier? verifier,
        string tenantId,
        ReadOnlyMemory<short> pcm16,
        CancellationToken cancellationToken,
        VerificationMetrics? metrics = null)
    {
        if (verifier is null)
        {
            return new VoiceGateDecision(PassesTranscript: true, Result: null);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(VerifyTimeout);

        try
        {
            var result = await verifier.VerifyAsync(pcm16, tenantId, timeoutCts.Token).ConfigureAwait(false);
            metrics?.Record(tenantId, result);
            return new VoiceGateDecision(result.PassesTranscript, result);
        }
#pragma warning disable CA1031 // Intentional: an embedder/store fault must not silence the user.
        catch
        {
            // Includes the OperationCanceledException raised by the timeout above.
            metrics?.RecordTimeout(tenantId);
            return new VoiceGateDecision(PassesTranscript: true, Result: null);
        }
#pragma warning restore CA1031
    }
}
