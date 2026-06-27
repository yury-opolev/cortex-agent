namespace Cortex.Contained.Agent.Host.Reminders;

/// <summary>
/// Delivers a pre-composed voice cue (the exact words to speak) to a
/// Discord-voice conversation when a <see cref="SessionReminderService"/>
/// timer fires. Decoupled from the service so it can be unit-tested with a
/// fake deliverer; the production wiring goes through the Bridge proactive
/// message path used by <c>SendMessageTool</c>.
/// </summary>
public interface IVoiceCueDeliverer
{
    /// <summary>
    /// Send <paramref name="text"/> to the channel identified by
    /// <paramref name="channelId"/> for delivery as proactive speech in the
    /// active Discord-voice conversation. Implementations must not throw on
    /// transient failures — log and swallow so the timer thread cannot crash.
    /// </summary>
    Task SpeakAsync(string conversationId, string channelId, string text, CancellationToken cancellationToken);
}
