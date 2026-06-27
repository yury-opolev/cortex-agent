namespace Cortex.Contained.Channels.Discord;

using global::Discord;

/// <summary>
/// Production <see cref="IDiscordChannelSender"/> that delegates to
/// <see cref="DiscordChannel.SocketClient"/>.
/// </summary>
internal sealed class DiscordChannelSender : IDiscordChannelSender
{
    private readonly DiscordChannel discordChannel;

    public DiscordChannelSender(DiscordChannel discordChannel)
    {
        this.discordChannel = discordChannel;
    }

    public async ValueTask<bool> TrySendAsync(ulong channelId, string text)
    {
        var channel = this.discordChannel.SocketClient.GetChannel(channelId) as IMessageChannel;
        if (channel is null)
        {
            return false;
        }
        await channel.SendMessageAsync(text).ConfigureAwait(false);
        return true;
    }
}
