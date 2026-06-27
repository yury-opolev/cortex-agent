namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Thin seam over <see cref="Discord.WebSocket.DiscordSocketClient"/> that allows
/// <see cref="DiscordEnrollmentProgressNotifier"/> to be unit-tested without a live
/// Discord gateway connection. The production implementation wraps
/// <see cref="DiscordChannel.SocketClient"/>.
/// </summary>
internal interface IDiscordChannelSender
{
    /// <summary>
    /// Post a message to the given text channel. Returns <c>false</c> when the channel
    /// cannot be resolved (unknown ID, not a text channel, etc.).
    /// </summary>
    ValueTask<bool> TrySendAsync(ulong channelId, string text);
}
