namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Raised when a voice send is attempted but the bot is not connected to a voice channel
/// (or its voice-out pipeline is being disposed). The proactive coordinator catches this
/// and returns <see cref="ProactiveDelivery.Dropped"/>.
/// </summary>
internal sealed class VoiceNotConnectedException : Exception
{
    public VoiceNotConnectedException(string message) : base(message) { }
}
