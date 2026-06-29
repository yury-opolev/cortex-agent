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
/// We only treat an MLS failure as recoverable when it lands inside the
/// join-race window after a (re)join. Later, isolated MLS proposals are part of
/// normal epoch churn (members coming and going) and must not trigger a
/// reconnect, or the bot would thrash its own voice connection.
/// </para>
/// </remarks>
public static class DaveMlsRecoveryPolicy
{
    /// <summary>
    /// Decide whether an observed MLS failure should force a clean rejoin.
    /// </summary>
    /// <param name="everJoined">A voice (re)join has happened at least once
    /// (i.e. the join timestamp is set). False before the first join — there is
    /// no session to heal.</param>
    /// <param name="ticksSinceJoin">Ticks elapsed since the last successful
    /// (re)join. Negative values (clock skew / not-yet-joined) never recover.</param>
    /// <param name="joinRaceWindowTicks">Width of the post-join window within
    /// which an MLS failure is attributed to the join race.</param>
    public static bool ShouldRecover(bool everJoined, long ticksSinceJoin, long joinRaceWindowTicks)
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
}
