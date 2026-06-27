namespace Cortex.Contained.Contracts.Recording;

/// <summary>
/// Runtime per-channel voice recording. State lives only in memory; the
/// Discord <c>/voice-record</c> slash command is the sole control plane.
/// Audio sources (Discord, host voice channel) call
/// <see cref="RecordCommittedUtterance"/> once per committed user turn —
/// the controller pre-pads inter-utterance silence based on the wall-time
/// gap since the previous commit, writes the PCM to the continuous
/// <c>session.wav</c>, and emits a <c>commit</c> event whose
/// <c>audioOffsetMs.{start,end}</c> reflect the WAV timeline.
/// </summary>
public interface IRecordingController
{
    /// <summary>
    /// Start recording for a (tenant, channel) pair. The session is placed
    /// under <c>&lt;root&gt;/&lt;tenantId&gt;/&lt;channelDisplay&gt;/&lt;sessionId&gt;/</c>.
    /// <paramref name="channelDisplay"/> is the human-readable channel name
    /// used for the folder (Discord voice-channel name or "host");
    /// <paramref name="tenantId"/> is the invoking user's tenant.
    /// </summary>
    Task<StartResult> StartAsync(
        string channelKey,
        string? label,
        CancellationToken ct,
        string? channelDisplay = null,
        string? tenantId = null);

    Task<StopResult> StopAsync(string channelKey, StopReason reason, CancellationToken ct);

    ActiveSession? GetActive(string channelKey);

    IReadOnlyCollection<ActiveSession> AllActive { get; }

    /// <summary>
    /// Append a committed utterance to the active recording session (if any)
    /// for the channel. Pre-pads the inter-utterance silence from the wall-time
    /// gap since the previous commit, writes 16 kHz mono 16-bit PCM to
    /// <c>session.wav</c>, and emits a <c>commit</c> event with correct
    /// timeline offsets. No-op when no session is active for the channel.
    /// </summary>
    /// <param name="channelKey">The channel-key (<c>discord:&lt;id&gt;</c> or <c>host</c>).</param>
    /// <param name="pcm16k">16 kHz mono 16-bit PCM bytes of the committed utterance.</param>
    /// <param name="utteranceId">Stable utterance identifier from the source pipeline.</param>
    /// <param name="text">Final STT transcript for this utterance (may be empty).</param>
    /// <param name="reason">Commit reason from the source (e.g. <c>"discord-commit"</c>, <c>"host-commit"</c>).</param>
    void RecordCommittedUtterance(
        string channelKey,
        ReadOnlySpan<byte> pcm16k,
        string utteranceId,
        string text,
        string reason);
}
