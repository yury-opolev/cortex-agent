namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Pure policy deciding whether a sustained inbound DAVE decrypt-flood warrants
/// forcing a clean voice rejoin. Extracted so the recovery rule is unit-testable
/// without a live Discord client.
/// </summary>
/// <remarks>
/// Root cause (2026-07-08 outage): after a join-race-seeded MLS group develops a
/// per-sender media-key ratchet desync, inbound audio packets fail to decrypt in
/// bursts each time the user resumes speaking. No decrypted PCM reaches the VAD,
/// so the agent never hears the user and stays silent — the transport still
/// reports Connected and no MLS failure fires, so nothing self-heals until the
/// user manually rejoins. We detect the flood and force one clean rejoin.
/// <para>
/// The trip is scoped so ordinary silence and healthy conversation never fire it:
/// decrypt failures only accrue when the user is actually transmitting (packets
/// arriving but failing), and any successful speech commit resets the count to
/// zero (the caller's responsibility).
/// </para>
/// </remarks>
public static class DaveDecryptFloodPolicy
{
    /// <param name="userPresent">Linked user is in the target voice channel.</param>
    /// <param name="failuresSinceCommit">Decrypt failures accumulated since the
    /// last successful speech commit (reset to 0 on commit / (re)join).</param>
    /// <param name="ticksSinceFirstFailure">Ticks since the first failure of the
    /// current run. Only meaningful when <paramref name="failuresSinceCommit"/> &gt; 0.</param>
    /// <param name="floodThreshold">Minimum accumulated failures to consider a flood.</param>
    /// <param name="minWindowTicks">Minimum age of the flood before acting.</param>
    public static bool ShouldRecover(
        bool userPresent,
        long failuresSinceCommit,
        long ticksSinceFirstFailure,
        long floodThreshold,
        long minWindowTicks)
    {
        if (!userPresent)
        {
            return false;
        }

        if (failuresSinceCommit < floodThreshold)
        {
            return false;
        }

        return ticksSinceFirstFailure >= minWindowTicks;
    }
}
