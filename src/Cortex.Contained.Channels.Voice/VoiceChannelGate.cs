namespace Cortex.Contained.Channels.Voice;

using Cortex.Contained.Speech.SpeakerId;

/// <summary>
/// Decision returned by <see cref="VoiceChannelGate.EvaluateAsync"/>.
/// </summary>
public readonly record struct VoiceChannelGateDecision(bool PassesTranscript, VerificationResult? Result);

/// <summary>
/// Channel-side helper around <see cref="ISpeakerVerifier.VerifyAsync"/>
/// enforcing the "never block the user on infrastructure failure" policy.
/// Pulled out so it can be unit-tested without constructing the full
/// <see cref="VoiceChannel"/>.
/// </summary>
internal static class VoiceChannelGate
{
    /// <summary>
    /// Hard timeout on the verifier call. If the embedder hangs, the gate
    /// fails open after this duration so the user is never silenced by an
    /// unresponsive verification path.
    /// </summary>
    internal static readonly TimeSpan VerifyTimeout = TimeSpan.FromMilliseconds(1500);

    public static async ValueTask<VoiceChannelGateDecision> EvaluateAsync(
        ISpeakerVerifier? verifier,
        string tenantId,
        ReadOnlyMemory<short> pcm16Mono16k,
        CancellationToken cancellationToken,
        VerificationMetrics? metrics = null)
    {
        if (verifier is null)
        {
            return new VoiceChannelGateDecision(PassesTranscript: true, Result: null);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(VerifyTimeout);

        try
        {
            var result = await verifier.VerifyAsync(pcm16Mono16k, tenantId, timeoutCts.Token).ConfigureAwait(false);
            metrics?.Record(tenantId, result);
            return new VoiceChannelGateDecision(result.PassesTranscript, result);
        }
#pragma warning disable CA1031 // Intentional: an embedder/store fault must not silence the user.
        catch
        {
            // Includes OperationCanceledException raised by the timeout.
            metrics?.RecordTimeout(tenantId);
            return new VoiceChannelGateDecision(PassesTranscript: true, Result: null);
        }
#pragma warning restore CA1031
    }
}
