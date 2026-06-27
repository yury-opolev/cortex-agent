namespace Cortex.Contained.Channels.Discord;

using System.Globalization;
using System.Net.Http;

using global::Discord;
using global::Discord.WebSocket;

using Cortex.Contained.Contracts.Messages;
using Cortex.Contained.Speech;

using Microsoft.Extensions.Logging;

using ChannelType = Cortex.Contained.Contracts.Channels.ChannelType;

/// <summary>
/// Owns inbound <see cref="SocketMessage"/> handling for <see cref="DiscordChannel"/>:
/// self/bot filtering, DM vs guild routing, bot-mention stripping, Discord
/// invite-link resolution, voice-message transcription, attachment extraction,
/// and forwarding the resulting <see cref="InboundMessage"/> into the bridge
/// pipeline via <see cref="IDiscordChannelHost.RaiseMessageReceivedAsync"/>. The
/// facade subscribes <see cref="OnDiscordMessageReceived"/> to the socket
/// client's <c>MessageReceived</c> event.
/// </summary>
internal sealed partial class DiscordMessageRouter
{
    /// <summary>Content type for OGG/Opus voice messages sent by Discord.</summary>
    private const string OggContentType = "audio/ogg";

    private readonly ILogger logger;
    private readonly DiscordChannelOptions options;
    private readonly DiscordSocketClient client;
    private readonly ISpeechToText? stt;
    private readonly HttpClient? httpClient;
    private readonly IDiscordChannelHost host;

    public DiscordMessageRouter(
        ILogger logger,
        DiscordChannelOptions options,
        DiscordSocketClient client,
        ISpeechToText? stt,
        HttpClient? httpClient,
        IDiscordChannelHost host)
    {
        this.logger = logger;
        this.options = options;
        this.client = client;
        this.stt = stt;
        this.httpClient = httpClient;
        this.host = host;
    }

    public async Task OnDiscordMessageReceived(SocketMessage rawMessage)
    {
        // Ignore system messages and bot messages (including our own)
        if (rawMessage is not SocketUserMessage message)
        {
            return;
        }

        if (message.Author.IsBot)
        {
            return;
        }

        string channelId;
        bool isGroup;

        if (message.Channel is IDMChannel)
        {
            // DM message → discord-dm
            channelId = DiscordChannel.DmChannelId;
            isGroup = false;

            // Store the DM channel snowflake for outbound routing
            this.host.SetDmChannelSnowflake(message.Channel.Id);
        }
        else if (message.Channel is SocketGuildChannel guildChannel)
        {
            // Guild message — accept from any guild the bot is in.
            // Tenant routing happens in the message dispatcher via sender ID.
            channelId = DiscordChannel.GuildChannelId;
            isGroup = true;
        }
        else
        {
            // Unknown channel type — ignore
            return;
        }

        // Build the text — strip the bot mention from the beginning if present
        var text = message.Content;
        if (isGroup && this.client.CurrentUser is not null)
        {
            var mentionStr = $"<@{this.client.CurrentUser.Id}>";
            var mentionStrNick = $"<@!{this.client.CurrentUser.Id}>";
            if (text.StartsWith(mentionStr, StringComparison.Ordinal))
            {
                text = text[mentionStr.Length..].TrimStart();
            }
            else if (text.StartsWith(mentionStrNick, StringComparison.Ordinal))
            {
                text = text[mentionStrNick.Length..].TrimStart();
            }
        }

        // If the message is just a Discord invite URL — e.g. produced by
        // Discord's "Invite to Voice" menu — resolve it to a human-readable
        // description so the agent can respond to the actual intent rather
        // than receiving a bare URL. Falls back to a generic description on
        // lookup failure.
        if (DiscordInviteParser.IsInviteOnly(text)
            && DiscordInviteParser.TryExtractInviteCode(text, out var inviteCode))
        {
            text = await ResolveInviteDescriptionAsync(inviteCode, text).ConfigureAwait(false);
        }

        // Check for voice message (audio/ogg attachment) and transcribe if enabled
        var isVoiceMessage = false;
        List<MediaAttachment>? attachments = null;

        if (message.Attachments.Count > 0)
        {
            attachments = [];
            foreach (var att in message.Attachments)
            {
                var contentType = att.ContentType ?? "application/octet-stream";

                // Detect voice messages: audio/ogg attachments with voice features enabled
                if (this.options.DmVoiceTranscription
                    && this.stt is not null
                    && this.httpClient is not null
                    && string.Equals(contentType, OggContentType, StringComparison.OrdinalIgnoreCase))
                {
                    // Try to transcribe the voice message
                    var transcribed = await TranscribeVoiceAttachmentAsync(att.Url, att.Filename).ConfigureAwait(false);
                    if (transcribed is not null)
                    {
                        text = string.IsNullOrEmpty(text) ? transcribed : $"{text} {transcribed}";
                        isVoiceMessage = true;
                        // Don't add this attachment to the list — we've consumed it as text
                        continue;
                    }
                }

                attachments.Add(new MediaAttachment
                {
                    MimeType = contentType,
                    Url = att.Url,
                    FileName = att.Filename,
                    Caption = null,
                });
            }

            if (attachments.Count == 0)
            {
                attachments = null;
            }
        }

        // Track that this channel used voice so the reply includes audio
        if (isVoiceMessage)
        {
            this.host.MarkVoiceConversation(channelId);
        }

        var inbound = new InboundMessage
        {
            MessageId = message.Id.ToString(CultureInfo.InvariantCulture),
            ConversationId = channelId,
            ChannelId = channelId,
            ChannelType = ChannelType.Discord,
            Sender = new SenderInfo
            {
                Id = message.Author.Id.ToString(CultureInfo.InvariantCulture),
                DisplayName = message.Author.GlobalName ?? message.Author.Username,
            },
            Content = new MessageContent
            {
                Text = text,
                Attachments = attachments,
                IsMarkdown = true, // Discord uses markdown
            },
            Timestamp = message.Timestamp,
            IsGroup = isGroup,
            Properties = string.Equals(channelId, DiscordChannel.DmChannelId, StringComparison.Ordinal)
                ? new Dictionary<string, string> { ["dm_snowflake"] = message.Channel.Id.ToString(CultureInfo.InvariantCulture) }
                : null,
        };

        this.LogMessageReceived(channelId, message.Author.Username, message.Id);

        await this.host.RaiseMessageReceivedAsync(inbound).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve a Discord invite code to a short agent-friendly description.
    /// Translates e.g. <c>https://discord.gg/TTQEUKmyn</c> into
    /// <c>[Discord voice-channel invite: "General" in "oYster's Private Server"]</c>
    /// so the agent can respond to the user's intent rather than guessing at a
    /// bare URL. Falls back to a generic description on any lookup failure
    /// (expired invite, network error, bot lacks access, etc.) — we never
    /// want a failure here to silently hide what the user actually sent.
    /// </summary>
    private async Task<string> ResolveInviteDescriptionAsync(string inviteCode, string originalText)
    {
        try
        {
            var invite = await this.client.GetInviteAsync(inviteCode).ConfigureAwait(false);
            if (invite is null)
            {
                return $"[Discord invite link: {originalText}]";
            }

            var discordChannelType = invite.ChannelType;
            var kind = discordChannelType switch
            {
                global::Discord.ChannelType.Voice => "voice-channel",
                global::Discord.ChannelType.Stage => "stage-channel",
                global::Discord.ChannelType.Text => "text-channel",
                global::Discord.ChannelType.Forum => "forum-channel",
                _ => "channel",
            };
            var channelName = invite.ChannelName ?? "(unknown channel)";
            var guildName = invite.GuildName ?? "(unknown server)";

            var suffix = discordChannelType == global::Discord.ChannelType.Voice
                ? " To join and reply by voice, use the 'discord-voice' channel via send_message."
                : string.Empty;

            return $"[Discord {kind} invite: \"{channelName}\" in \"{guildName}\"]{suffix}";
        }
#pragma warning disable CA1031 // any failure → fall back to the raw URL description
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogInviteResolveFailed(inviteCode, ex.Message);
            return $"[Discord invite link: {originalText}]";
        }
    }

    /// <summary>
    /// Download and transcribe a voice message attachment from Discord CDN.
    /// Returns the transcribed text, or null on failure.
    /// </summary>
    private async Task<string?> TranscribeVoiceAttachmentAsync(string url, string filename)
    {
        try
        {
            this.LogVoiceMessageReceived(DiscordChannel.DmChannelId, filename);

            // Download audio from Discord CDN
            var oggData = await this.httpClient!.GetByteArrayAsync(url).ConfigureAwait(false);

            // Decode OGG/Opus → 48kHz mono 16-bit PCM
            var pcm48k = AudioConverter.DecodeOggOpus(oggData);

            // Resample 48kHz → 16kHz for Whisper STT
            var pcm16k = AudioConverter.Resample(pcm48k, AudioFormat.Discord.SampleRate, AudioFormat.Whisper.SampleRate);

            // Transcribe
            var transcription = await this.stt!.TranscribeAsync(pcm16k).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(transcription))
            {
                this.LogVoiceTranscriptionEmpty(DiscordChannel.DmChannelId, filename);
                return null;
            }

            this.LogVoiceTranscriptionDone(DiscordChannel.DmChannelId, filename, transcription.Length);
            return transcription;
        }
        catch (Exception ex)
        {
            this.LogVoiceTranscriptionFailed(DiscordChannel.DmChannelId, filename, ex.Message);
            return null;
        }
    }

    // ── LoggerMessage source-generated methods ───────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "Discord channel {ChannelId} received message from {Author} (id: {MessageId})")]
    private partial void LogMessageReceived(string channelId, string author, ulong messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord invite resolve failed for code '{InviteCode}': {ErrorMessage}")]
    private partial void LogInviteResolveFailed(string inviteCode, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord channel {ChannelId} received voice message: {FileName}")]
    private partial void LogVoiceMessageReceived(string channelId, string fileName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord channel {ChannelId} transcribed voice message {FileName} ({CharCount} chars)")]
    private partial void LogVoiceTranscriptionDone(string channelId, string fileName, int charCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord channel {ChannelId} voice transcription empty for {FileName}")]
    private partial void LogVoiceTranscriptionEmpty(string channelId, string fileName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Discord channel {ChannelId} voice transcription failed for {FileName}: {ErrorMessage}")]
    private partial void LogVoiceTranscriptionFailed(string channelId, string fileName, string errorMessage);
}
