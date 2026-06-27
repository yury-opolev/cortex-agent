namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Decides how an outbound DM should be routed. A cached DM-channel snowflake (learned from a
/// prior inbound DM) is preferred; otherwise, when a recipient user id is known (from a prior
/// inbound DM or from the tenant's configured linked user), the DM channel is opened on demand;
/// only when neither is available is there no target.
/// </summary>
/// <remarks>
/// Extracted as a pure function so the routing decision is unit-tested without a live Discord
/// client. Fixes the prior behavior where a proactive DM failed outright ("No DM channel
/// available — no DM has been received yet") whenever the volatile snowflake was unset
/// (e.g. after a Bridge restart, before any inbound DM re-primed it).
/// </remarks>
internal static class DmTargetResolver
{
    internal enum DmAction
    {
        /// <summary>Send to an already-known DM channel snowflake.</summary>
        UseSnowflake,

        /// <summary>Open a DM channel for the given recipient user id, then send.</summary>
        OpenDmForUser,

        /// <summary>No snowflake and no recipient — cannot deliver.</summary>
        NoTarget,
    }

    /// <summary>
    /// Resolves the outbound-DM action. Returns the snowflake to use, the user id to open a DM
    /// with, or a no-target signal. The returned <c>Value</c> is the snowflake for
    /// <see cref="DmAction.UseSnowflake"/>, the user id for <see cref="DmAction.OpenDmForUser"/>,
    /// and 0 for <see cref="DmAction.NoTarget"/>.
    /// </summary>
    internal static (DmAction Action, ulong Value) Resolve(ulong dmChannelSnowflake, ulong dmRecipientUserId)
    {
        if (dmChannelSnowflake != 0)
        {
            return (DmAction.UseSnowflake, dmChannelSnowflake);
        }

        if (dmRecipientUserId != 0)
        {
            return (DmAction.OpenDmForUser, dmRecipientUserId);
        }

        return (DmAction.NoTarget, 0);
    }
}
