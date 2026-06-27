namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Result classification for a proactive voice-channel delivery attempt.
/// </summary>
internal enum ProactiveDelivery
{
    /// <summary>The message was spoken directly into the voice channel.</summary>
    Spoken,

    /// <summary>A ring DM was sent and the message was queued for delivery on user-join.</summary>
    Rang,

    /// <summary>A ring was already in progress; the message was appended to the existing queue.</summary>
    Queued,

    /// <summary>Delivery could not be initiated (no handler, voice connect failed, DM unreachable, etc.).</summary>
    Dropped,
}

/// <summary>
/// Outcome of a single proactive delivery attempt. <see cref="Reason"/> is non-null only when
/// <see cref="Delivery"/> is <see cref="ProactiveDelivery.Dropped"/>.
/// </summary>
internal sealed record ProactiveOutcome(ProactiveDelivery Delivery, string? Reason = null);
