namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Pure policy deciding whether a DAVE handshake that started but never completed
/// warrants forcing a clean voice rejoin. Extracted so the rule is unit-testable
/// without a live Discord client.
/// </summary>
/// <remarks>
/// Root cause (2026-08-19 outage, ~100 minutes of silence): after a forced rejoin the
/// bot's new voice session ran <c>Init dave protocol session</c> and sent its MLS key
/// package, received the external sender package — and then nothing. No proposals, no
/// welcome, no protocol transition, so no per-sender decryptors were ever installed and
/// no input stream was created. Every inbound packet was discarded, the bot heard
/// nothing for the rest of the session, and Discord showed the degraded-E2EE warning
/// because the bot never joined the MLS group.
/// <para>
/// Nothing detected it. The transport reported <c>Connected</c>, so the audio-death
/// watchdog stayed quiet; <c>decryptFail</c> was 0 (packets never reached a decryptor)
/// so <see cref="DaveDecryptFloodPolicy"/> stayed quiet; and <c>mlsFail</c> was 0 so
/// <see cref="DaveMlsRecoveryPolicy"/> stayed quiet.
/// </para>
/// <para>
/// <strong>Why not use the malformed-frame counter.</strong> It is tempting, because a
/// wedged session logs "Malformed Frame" forever. But those are simply packets whose RTP
/// payload type isn't Discord's Opus type 120 (RTCP / keepalive-class control traffic),
/// arriving on a ~1&#160;Hz timer per registered source and entirely independent of speech.
/// Measured across real logs, a <em>healthy</em> session produces roughly twice as many
/// (~2/s, two phase-offset sources) as a wedged one (~1/s, one source) — the counter is
/// inverted with respect to health and is useless as a fault signal.
/// </para>
/// <para>
/// So the rule keys on the structural fact instead: the handshake started and never
/// reached its completion milestone while the user was actually present. The caller
/// latches the first tick at which it confirms that state and clears the latch as soon
/// as the handshake completes or the user leaves, so an empty channel (where Discord
/// performs no protocol transition at all) can never trip it.
/// </para>
/// </remarks>
public static class DaveHandshakeStallPolicy
{
    /// <summary>
    /// True when a DAVE handshake has started but not reached
    /// <see cref="DaveLifecycleEvent.SessionReady"/>.
    /// </summary>
    /// <param name="handshakeStartedTicks">Ticks of the last
    /// <see cref="DaveLifecycleEvent.HandshakeStarted"/> (0 = DAVE never initialised,
    /// e.g. encryption disabled).</param>
    /// <param name="sessionReadyTicks">Ticks of the last
    /// <see cref="DaveLifecycleEvent.SessionReady"/> (0 = never).</param>
    public static bool IsUnhealed(long handshakeStartedTicks, long sessionReadyTicks)
    {
        if (handshakeStartedTicks == 0)
        {
            return false;
        }

        // A ready stamp only counts when it belongs to this handshake — i.e. it landed
        // at or after the start. An older stamp is from the previous session.
        return sessionReadyTicks < handshakeStartedTicks;
    }

    /// <summary>
    /// True when a confirmed-unhealed handshake has persisted past the stall window and
    /// a clean rejoin should be forced.
    /// </summary>
    /// <param name="stallSinceTicks">Ticks of the first tick at which the caller
    /// confirmed an unhealed handshake with the user present (0 = not currently
    /// stalling).</param>
    /// <param name="nowTicks">Current time in ticks.</param>
    /// <param name="stallWindowTicks">How long an unhealed handshake may persist before
    /// it is treated as wedged.</param>
    public static bool ShouldRecover(long stallSinceTicks, long nowTicks, long stallWindowTicks)
    {
        if (stallSinceTicks == 0)
        {
            return false;
        }

        var age = nowTicks - stallSinceTicks;
        if (age < 0)
        {
            return false;
        }

        return age >= stallWindowTicks;
    }
}
