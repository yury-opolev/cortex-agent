namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Pure policy deciding whether an observed DAVE <c>MLS Failure</c> log line
/// warrants forcing a clean voice rejoin. Extracted so the recovery rule is
/// unit-testable without a live Discord client.
/// </summary>
/// <remarks>
/// Root cause (2026-06-29 outage): when the proactive "ring" pulls the bot into
/// voice at almost the same moment the linked user joins, Discord's DAVE/MLS
/// add-proposal for that user can fail
/// (<c>"MLS Failure: ... Unexpected user ID in add proposal"</c>), wedging the
/// end-to-end-encrypted group. The bot then transmits audio the just-joined
/// listener cannot decrypt — the agent reports "delivered" (synthesis, pacing
/// and frame transmission all succeed) while the user hears pure silence — and
/// the session does NOT self-heal. A full leave+rejoin rebuilds the MLS group
/// cleanly with the user already present (user-confirmed fix).
/// <para>
/// Recovery is a <em>two-stage</em> decision, and both stages matter.
/// </para>
/// <para>
/// <strong>Stage 1 — <see cref="ShouldArm"/> (does this failure look like the join
/// race?).</strong> Only failures inside the post-join window qualify. Later,
/// isolated MLS proposals are part of normal epoch churn (members coming and
/// going) and must not trigger a reconnect, or the bot would thrash its own voice
/// connection.
/// </para>
/// <para>
/// <strong>Stage 2 — <see cref="ShouldRecover"/> (did it fail to heal itself?).</strong>
/// This stage was missing until 2026-08-19 and caused a ~100-minute outage. The MLS
/// failure is logged the instant a proposal is rejected, but Discord very often follows
/// it milliseconds later with a <c>DaveMLSWelcome</c> that establishes the group
/// correctly. The old code latched a bare "suspect" flag and let the <em>next</em>
/// watchdog tick act on it ~950&#160;ms later — by which time the session had already
/// recovered, decrypted the user's audio and produced a speech onset. The rejoin tore
/// down a working connection, its own reconnect timed out, and the replacement session
/// never completed its DAVE handshake, leaving the bot deaf for 100 minutes.
/// </para>
/// <para>
/// Reviewing every MLS failure recorded across 2026-08-08…08-19 (18 occurrences), the
/// session reached <see cref="DaveLifecycleEvent.SessionReady"/> within 5 seconds in
/// <em>every single case</em> — so the unconditional rejoin was a false positive every
/// time it fired. Stage 2 therefore demands positive evidence of non-recovery: no
/// session-ready milestone since the failure, <em>and</em> a settle window elapsed to
/// give the welcome time to land.
/// </para>
/// </remarks>
public static class DaveMlsRecoveryPolicy
{
    /// <summary>
    /// Stage 1: decide whether an observed MLS failure is attributable to the join
    /// race and should arm recovery. Arming only records suspicion — see
    /// <see cref="ShouldRecover"/> for the decision to actually rejoin.
    /// </summary>
    /// <param name="everJoined">A voice (re)join has happened at least once
    /// (i.e. the join timestamp is set). False before the first join — there is
    /// no session to heal.</param>
    /// <param name="ticksSinceJoin">Ticks elapsed since the last successful
    /// (re)join. Negative values (clock skew / not-yet-joined) never recover.</param>
    /// <param name="joinRaceWindowTicks">Width of the post-join window within
    /// which an MLS failure is attributed to the join race.</param>
    public static bool ShouldArm(bool everJoined, long ticksSinceJoin, long joinRaceWindowTicks)
    {
        if (!everJoined)
        {
            return false;
        }

        if (ticksSinceJoin < 0)
        {
            return false;
        }

        return ticksSinceJoin <= joinRaceWindowTicks;
    }

    /// <summary>
    /// Stage 2: decide whether an armed MLS failure has failed to heal itself and a
    /// clean rejoin should now be forced.
    /// </summary>
    /// <param name="armedTicks">Ticks of the armed MLS failure (0 = not armed).</param>
    /// <param name="sessionReadyTicks">Ticks of the last
    /// <see cref="DaveLifecycleEvent.SessionReady"/> milestone (0 = never). When this is
    /// at or after <paramref name="armedTicks"/> the MLS group was established
    /// <em>after</em> the failure, so the failure was transient and must be ignored.</param>
    /// <param name="nowTicks">Current time in ticks.</param>
    /// <param name="settleWindowTicks">Grace period after the failure during which the
    /// welcome may still arrive. Acting before it elapses risks destroying a session
    /// that is about to become healthy.</param>
    public static bool ShouldRecover(
        long armedTicks,
        long sessionReadyTicks,
        long nowTicks,
        long settleWindowTicks)
    {
        if (armedTicks == 0)
        {
            return false;
        }

        // The group was (re)established after the failure — it healed itself.
        if (sessionReadyTicks >= armedTicks)
        {
            return false;
        }

        var age = nowTicks - armedTicks;
        if (age < 0)
        {
            return false;
        }

        return age >= settleWindowTicks;
    }
}
