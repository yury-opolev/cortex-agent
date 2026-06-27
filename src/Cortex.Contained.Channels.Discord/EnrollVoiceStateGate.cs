namespace Cortex.Contained.Channels.Discord;

/// <summary>Outcome of a voice-state check before enrollment.</summary>
public enum EnrollGateDecision
{
    /// <summary>User is in the configured voice channel — proceed silently.</summary>
    Proceed,

    /// <summary>User is not in voice (or wrong channel) — caller should ring the channel.</summary>
    RingAndProceed,
}

/// <summary>
/// Pure policy: decides whether to ring the user into voice before starting
/// enrollment, based on the user's current voice channel id and the tenant's
/// configured voice channel id. Extracted so the decision is unit-testable
/// without constructing a Discord client or a voice handler.
/// </summary>
public static class EnrollVoiceStateGate
{
    /// <summary>
    /// Returns <see cref="EnrollGateDecision.Proceed"/> when
    /// <paramref name="currentVoiceChannelId"/> matches
    /// <paramref name="configuredVoiceChannelId"/>; otherwise
    /// <see cref="EnrollGateDecision.RingAndProceed"/>.
    /// </summary>
    public static EnrollGateDecision Decide(ulong? currentVoiceChannelId, ulong configuredVoiceChannelId)
    {
        if (currentVoiceChannelId is { } current && current == configuredVoiceChannelId)
        {
            return EnrollGateDecision.Proceed;
        }

        return EnrollGateDecision.RingAndProceed;
    }
}
