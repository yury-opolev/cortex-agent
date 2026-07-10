using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;

using Discord;
using Discord.WebSocket;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Messages;
using Cortex.Contained.Speech;
using Microsoft.Extensions.Logging;

using ChannelType = Cortex.Contained.Contracts.Channels.ChannelType;
using IChannel = Cortex.Contained.Contracts.Channels.IChannel;

namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Discord bot channel. Connects to Discord using a bot token and handles three
/// logical sub-channels: DM text (<see cref="DmChannelId"/>), guild text
/// (<see cref="GuildChannelId"/>), and realtime voice
/// (<see cref="VoiceChannelId"/>). All three route through a single
/// <see cref="DiscordSocketClient"/>; the Bridge registers the primary channel
/// under <see cref="DmChannelId"/> and aliases the other IDs via
/// <c>ChannelManager.RegisterChannelAlias</c>.
/// </summary>
public sealed partial class DiscordChannel : IChannelWithStreaming, IDiscordChannelHost
{
    /// <summary>Discord messages can be up to 2000 characters.</summary>
    private const int MaxDiscordMessageLength = 2000;

    /// <summary>Content type for OGG/Opus voice messages sent by Discord.</summary>
    private const string OggContentType = "audio/ogg";

    /// <summary>Fixed channel ID for DM conversations.</summary>
    internal const string DmChannelId = "discord-dm";

    /// <summary>Fixed channel ID for guild text conversations (not currently wired).</summary>
    internal const string GuildChannelId = "discord-guild";

    /// <summary>Fixed channel ID for realtime voice conversations in Discord voice channels.</summary>
    internal const string VoiceChannelId = "discord-voice";

    private readonly ILogger<DiscordChannel> logger;
    private readonly DiscordChannelOptions options;
    private readonly DiscordSocketClient client;
    private readonly ISpeechToText? stt;
    private readonly IStreamingSpeechToText? streamingStt;
    private readonly ITurnDetector? turnDetector;
    private readonly ITextToSpeech? tts;
    private readonly HttpClient? httpClient;
    private IEnrollmentProgressNotifier? progressNotifier;

    /// <summary>Handles slash-command registration, dispatch, and autocomplete.</summary>
    private readonly DiscordSlashCommandModule slashCommandModule;

    /// <summary>Handles inbound message filtering, routing, and forwarding.</summary>
    private readonly DiscordMessageRouter messageRouter;

    /// <summary>
    /// Tracks conversations that were initiated with a voice message so
    /// that the reply is also sent as audio. Keyed by channelId (discord-dm/discord-guild).
    /// Entries expire after being consumed on the next outbound message.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> voiceConversations = new();

    /// <summary>
    /// Stores the last-seen DM channel snowflake so outbound DM replies
    /// can be routed to the correct Discord DM channel.
    /// </summary>
    private ulong dmChannelSnowflake;

    /// <summary>
    /// Recipient user id for outbound DMs, learned from inbound DMs and from the tenant's
    /// configured linked user (<see cref="ReconcileVoiceHandlers"/>). Lets the channel open a
    /// DM on demand when no <see cref="dmChannelSnowflake"/> is cached (e.g. after a restart),
    /// instead of failing because no inbound DM has primed the snowflake yet.
    /// </summary>
    private ulong dmRecipientUserId;

    /// <summary>
    /// Per-tenant voice handlers. Keyed by tenant ID. Created/destroyed dynamically
    /// via <see cref="ReconcileVoiceHandlers"/>.
    /// </summary>
    private readonly Dictionary<string, DiscordVoiceHandler> voiceHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object voiceHandlersLock = new();

    /// <summary>
    /// DAVE voice-decoding telemetry: counts decrypt failures, malformed frames,
    /// unknown SSRC / user, and MLS handshake failures. Populated by
    /// <see cref="OnDiscordLog"/> as Discord.Net emits diagnostic events. Read
    /// by voice handlers to compute per-utterance deltas, and flushed to an
    /// Info-level summary log every <see cref="DaveStatsFlushIntervalMs"/>
    /// milliseconds of activity.
    /// </summary>
    private readonly DaveEventStats daveStats = new();

    /// <summary>
    /// Minimum wall-clock gap between periodic DAVE summary log lines. Prevents
    /// flooding the log with summaries when no events are happening.
    /// </summary>
    private const int DaveStatsFlushIntervalMs = 5000;

    private long daveStatsLastFlushTicks = DateTimeOffset.UtcNow.Ticks;
    private DaveEventStats.Snapshot daveStatsLastFlushed;

    /// <summary>Public snapshot for per-utterance diffs from voice handlers.</summary>
    internal DaveEventStats.Snapshot SnapshotDaveStats() => this.daveStats.Take();

    private ChannelStatus status = ChannelStatus.Disconnected;

    /// <summary>The bot's Discord username (e.g. "Emma"), available after connection.</summary>
    public string? BotUsername { get; private set; }

    /// <summary>The bot's Discord user ID, available after connection.</summary>
    public ulong? BotUserId { get; private set; }

    /// <summary>The Discord application ID (for OAuth2 URLs), available after connection.</summary>
    public ulong? ApplicationId { get; private set; }

    /// <summary>
    /// The underlying <see cref="DiscordSocketClient"/>. Exposed internally so that
    /// <see cref="DiscordEnrollmentProgressNotifier"/> can resolve text channels without
    /// needing a separate DI registration of the client.
    /// </summary>
    internal DiscordSocketClient SocketClient => this.client;

    public DiscordChannel(ILogger<DiscordChannel> logger, DiscordChannelOptions options)
        : this(logger, options, null, null, null, null, null)
    {
    }

    public DiscordChannel(
        ILogger<DiscordChannel> logger,
        DiscordChannelOptions options,
        ISpeechToText? stt,
        ITextToSpeech? tts,
        HttpClient? httpClient,
        IStreamingSpeechToText? streamingStt = null,
        ITurnDetector? turnDetector = null)
    {
        this.logger = logger;
        this.options = options;
        this.stt = stt;
        this.streamingStt = streamingStt;
        this.turnDetector = turnDetector;
        this.tts = tts;
        this.httpClient = httpClient;

        // Base intents — GuildVoiceStates is added dynamically when voice handlers exist
        var intents = GatewayIntents.Guilds
            | GatewayIntents.GuildMessages
            | GatewayIntents.DirectMessages
            | GatewayIntents.MessageContent
            | GatewayIntents.GuildVoiceStates; // Always include — needed when any tenant has voice

        var config = new DiscordSocketConfig
        {
            GatewayIntents = intents,
            LogLevel = LogSeverity.Debug,
            // libdave (Discord Audio/Video End-to-End Encryption) is required by Discord
            // for voice connections as of March 1st, 2026. Requires Discord.Net.Dave package
            // and the native libdave.dll library in the output directory.
            EnableVoiceDaveEncryption = this.options.EnableVoiceDaveEncryption,
        };

        this.client = new DiscordSocketClient(config);

        this.slashCommandModule = new DiscordSlashCommandModule(logger, this.client, this);
        this.messageRouter = new DiscordMessageRouter(logger, options, this.client, stt, httpClient, this);

        this.client.MessageReceived += this.messageRouter.OnDiscordMessageReceived;
        this.client.Ready += OnClientReady;
        this.client.Connected += OnClientConnected;
        this.client.Disconnected += OnClientDisconnected;
        this.client.Log += OnDiscordLog;
        this.client.UserVoiceStateUpdated += OnUserVoiceStateUpdated;
        this.client.SlashCommandExecuted += this.slashCommandModule.OnSlashCommandExecuted;
        this.client.AutocompleteExecuted += this.slashCommandModule.OnAutocompleteExecuted;
    }

    /// <summary>
    /// Voice channels (by name + id) belonging to a Discord guild. Used by the
    /// Bridge's slash-command handler to resolve <c>/voice-record channel:&lt;name&gt;</c>.
    /// Returns empty when the guild isn't found (client not yet caught up) or
    /// the guildId is zero.
    /// </summary>
    public IReadOnlyList<(string Name, ulong Id)> GetGuildVoiceChannels(ulong guildId)
    {
        if (guildId == 0)
        {
            return Array.Empty<(string, ulong)>();
        }

        var guild = this.client.GetGuild(guildId);
        if (guild is null)
        {
            return Array.Empty<(string, ulong)>();
        }

        return guild.VoiceChannels
            .Select(vc => (vc.Name, vc.Id))
            .ToArray();
    }

    /// <summary>Display name of a single voice channel by id, or null if not visible to the bot.</summary>
    public string? GetVoiceChannelName(ulong guildId, ulong channelId)
    {
        if (guildId == 0 || channelId == 0)
        {
            return null;
        }

        return this.client.GetGuild(guildId)?.GetVoiceChannel(channelId)?.Name;
    }

    /// <summary>
    /// Set by the Bridge at startup to return the (tenant-scoped) list of voice
    /// channels available to a tenant — typically that tenant's single
    /// configured Discord voice channel plus the literal <c>host</c> entry
    /// (Id 0) when the host voice channel is bound to the same tenant. Used by
    /// the <c>/voice-record</c> autocomplete to suggest only what the invoker
    /// can actually target. When null, the autocomplete falls back to the
    /// (broader) guild-wide voice channel list — V0 behaviour kept as a
    /// safety net only.
    /// </summary>
    public Func<string?, IReadOnlyList<(string Name, ulong Id)>>? AvailableVoiceChannelsResolver { get; set; }

    /// <summary>
    /// Invoked by the channel when a slash command lands. The Bridge wires
    /// this to call into Agent.Host via <c>HubClient</c>. Returning null
    /// causes a generic "Not handled" reply.
    /// </summary>
    public Func<SlashCommandRequest, Task<SlashCommandResult?>>? SlashCommandHandler { get; set; }

    /// <summary>
    /// Resolves a Discord user id to a tenant id. Wired by the Bridge using
    /// the tenant registry. Returns null when the user isn't paired with a tenant.
    /// </summary>
    public Func<ulong, string?>? TenantResolver { get; set; }

    /// <summary>
    /// Voice barge-in commit: Bridge → Agent <c>OnTurnInterrupted</c> for the
    /// given tenant's voice session. Wired by the Bridge using the per-tenant
    /// <c>HubClient</c> (same injection model as <see cref="SlashCommandHandler"/>
    /// / <see cref="TenantResolver"/>; the closure is passed into each
    /// <see cref="DiscordVoiceHandler"/> at construction, mirroring how
    /// <c>onTranscription</c> is threaded). The first arg is the tenant id.
    /// When <c>null</c>, barge-in still stops playback locally but the Agent
    /// Host's history is not edited (degraded, not fatal).
    /// </summary>
    public Func<string, TurnInterruptedNotification, Task>? TurnInterruptedHandler { get; set; }

    /// <summary>
    /// Voice hold-back abort: Bridge → Agent <c>AbortGeneration</c> for the
    /// given tenant's voice conversation. Wired the same way as
    /// <see cref="TurnInterruptedHandler"/>. Args: tenant id, conversation id.
    /// </summary>
    public Func<string, string, Task>? AbortGenerationHandler { get; set; }

    /// <summary>
    /// Posts enrollment progress messages to the Discord text channel where
    /// <c>/voice-id enroll</c> was issued. Wired by the Bridge after DI resolves
    /// the notifier singleton. When <c>null</c>, progress messages are silently skipped.
    /// </summary>
    public IEnrollmentProgressNotifier? EnrollmentProgressNotifier
    {
        get => this.progressNotifier;
        set => this.progressNotifier = value;
    }

    // ── IChannel properties ──────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// The DiscordChannel handles two logical channels: "discord-dm" and "discord-guild".
    /// The primary ChannelId is "discord-dm" (the default/DM channel).
    /// </remarks>
    public string ChannelId { get; } = DmChannelId;

    /// <inheritdoc />
    public ChannelType Type => ChannelType.Discord;

    /// <inheritdoc />
    public ChannelStatus Status => this.status;

    /// <inheritdoc />
    public ChannelCapabilities Capabilities { get; } = new()
    {
        SupportsMedia = true,
        SupportsStreaming = true,
        SupportsRichText = true,
        SupportsGroups = true,
        SupportsEditing = true,
        SupportsDeletion = true,
        SupportsReactions = true,
        MaxMessageLength = MaxDiscordMessageLength,
        SupportedMediaTypes = ["image/jpeg", "image/png", "image/gif", "image/webp", "video/mp4", "audio/mpeg", "audio/ogg", "application/pdf"],
    };

    // ── IChannel events ──────────────────────────────────────────────

    /// <inheritdoc />
    public event Func<InboundMessage, Task>? MessageReceived;

    /// <inheritdoc />
    public event Func<ChannelStatusChange, Task>? StatusChanged;

    // ── IChannel methods ─────────────────────────────────────────────

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        SetStatus(ChannelStatus.Connecting, "Logging in to Discord");

        this.LogConnecting(ChannelId);

        await this.client.LoginAsync(TokenType.Bot, this.options.BotToken).ConfigureAwait(false);
        await this.client.StartAsync().ConfigureAwait(false);

        // The Ready event will set status to Connected
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        this.LogDisconnecting(ChannelId);

        // Dispose all voice handlers before stopping the client
        DiscordVoiceHandler[] handlers;
        lock (this.voiceHandlersLock)
        {
            handlers = [.. this.voiceHandlers.Values];
            this.voiceHandlers.Clear();
        }

        foreach (var handler in handlers)
        {
            await handler.DisposeAsync().ConfigureAwait(false);
        }

        await this.client.StopAsync().ConfigureAwait(false);
        await this.client.LogoutAsync().ConfigureAwait(false);

        SetStatus(ChannelStatus.Disconnected, "Disconnected");
    }

    /// <summary>
    /// Reconcile the set of active voice handlers against the current tenant configs.
    /// Creates new handlers for tenants that gained voice config, destroys handlers for
    /// tenants that lost voice config, and recreates handlers whose config changed.
    /// </summary>
    /// <param name="tenantVoiceConfigs">
    /// Dictionary of tenant ID → <see cref="VoiceHandlerConfig"/> for all tenants
    /// that should have voice handlers. Tenants not in this dictionary will have
    /// their handlers removed.
    /// </param>
    public async Task ReconcileVoiceHandlers(IReadOnlyDictionary<string, VoiceHandlerConfig> tenantVoiceConfigs)
    {
        if (this.stt is null || this.tts is null)
        {
            this.LogVoiceReconcileSkipped("STT or TTS services not available");
            return;
        }

        List<DiscordVoiceHandler> toDispose = [];

        lock (this.voiceHandlersLock)
        {
            // Find handlers to remove (tenant no longer has voice config)
            var toRemove = new List<string>();
            foreach (var (tenantId, handler) in this.voiceHandlers)
            {
                if (!tenantVoiceConfigs.ContainsKey(tenantId))
                {
                    toRemove.Add(tenantId);
                    toDispose.Add(handler);
                }
            }

            foreach (var tenantId in toRemove)
            {
                this.voiceHandlers.Remove(tenantId);
                this.LogVoiceHandlerRemoved(tenantId);
            }

            // Find handlers to create or recreate
            foreach (var (tenantId, config) in tenantVoiceConfigs)
            {
                if (this.voiceHandlers.TryGetValue(tenantId, out var existing))
                {
                    // Handler exists — check if config changed
                    // For now, always recreate if reconcile is called for this tenant
                    // (the caller only includes changed tenants)
                    toDispose.Add(existing);
                    this.voiceHandlers.Remove(tenantId);
                }

                // Remember the tenant's linked user so proactive DMs can be opened on demand
                // even when no inbound DM has primed the snowflake (e.g. after a restart).
                this.dmRecipientUserId = config.LinkedUserId;

                var handlerTenantId = tenantId;
                var handler = new DiscordVoiceHandler(
                    this.logger,
                    config,
                    this.stt,
                    this.tts,
                    this.client,
                    onTranscription: msg =>
                    {
                        if (MessageReceived is { } callback)
                        {
                            return callback(msg);
                        }
                        return Task.CompletedTask;
                    },
                    streamingStt: this.streamingStt,
                    turnDetector: this.turnDetector,
                    daveStats: this.daveStats,
                    onTurnInterrupted: notification =>
                    {
                        if (TurnInterruptedHandler is { } handlerCb)
                        {
                            return handlerCb(handlerTenantId, notification);
                        }
                        return Task.CompletedTask;
                    },
                    onAbortGeneration: conversationId =>
                    {
                        if (AbortGenerationHandler is { } abortCb)
                        {
                            return abortCb(handlerTenantId, conversationId);
                        }
                        return Task.CompletedTask;
                    });

                this.voiceHandlers[tenantId] = handler;
                this.LogVoiceHandlerCreated(tenantId, config.GuildId, config.VoiceChannelId);
            }
        }

        // Dispose removed/recreated handlers outside the lock
        foreach (var handler in toDispose)
        {
            try
            {
                await handler.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.LogVoiceHandlerDisposeFailed(ex.Message);
            }
        }
    }

    /// <inheritdoc />
    public async Task<SendResult> SendMessageAsync(OutboundMessage message, CancellationToken ct = default)
    {
        if (this.status != ChannelStatus.Connected)
        {
            return SendResult.Error("Discord channel is not connected");
        }

        try
        {
            // Route outbound messages by channel ID
            ulong targetSnowflake;
            if (TryGetVoiceTenantId(message.ConversationId, out var tenantId))
            {
                DiscordVoiceHandler? handler;
                lock (this.voiceHandlersLock)
                {
                    this.voiceHandlers.TryGetValue(tenantId, out handler);
                }

                if (handler is null)
                {
                    return SendResult.Error($"No voice handler registered for tenant '{tenantId}'");
                }

                if (string.IsNullOrEmpty(message.Content.Text))
                {
                    return SendResult.Error("Voice message has no text content");
                }

                // EnqueueProactiveAsync handles both paths:
                //   connected     → speaks immediately (existing fast path)
                //   disconnected  → starts a ring (join voice, create invite, DM the user)
                //                   and queues the message for drain on user-join or fallback
                var outcome = await handler.EnqueueProactiveAsync(message.Content.Text, ct: ct).ConfigureAwait(false);
                return outcome.Delivery switch
                {
                    ProactiveDelivery.Dropped => SendResult.Error(outcome.Reason ?? "Proactive voice delivery failed"),
                    _ => SendResult.Ok(),
                };
            }
            else if (string.Equals(message.ChannelId, GuildChannelId, StringComparison.Ordinal))
            {
                // Legacy guild text routing — kept for backward compatibility
                // In per-tenant mode, guild text channels are per-tenant
                targetSnowflake = 0; // No global guild text channel
                return SendResult.Error("Guild text channel routing requires per-tenant config");
            }
            else if (string.Equals(message.ChannelId, DmChannelId, StringComparison.Ordinal)
                     || string.Equals(message.ConversationId, DmChannelId, StringComparison.Ordinal))
            {
                var (dmAction, dmValue) = DmTargetResolver.Resolve(this.dmChannelSnowflake, this.dmRecipientUserId);
                switch (dmAction)
                {
                    case DmTargetResolver.DmAction.UseSnowflake:
                        targetSnowflake = dmValue;
                        break;

                    case DmTargetResolver.DmAction.OpenDmForUser:
                        var openedSnowflake = await this.OpenDmChannelAsync(dmValue).ConfigureAwait(false);
                        if (openedSnowflake == 0)
                        {
                            return SendResult.Error("No DM channel available — could not open a DM with the linked user (not in a shared guild, or the user has DMs disabled)");
                        }

                        this.dmChannelSnowflake = openedSnowflake;
                        targetSnowflake = openedSnowflake;
                        break;

                    default:
                        return SendResult.Error("No DM channel available — no DM has been received yet and no linked user is known");
                }
            }
            else
            {
                // Fallback: try parsing ConversationId as a snowflake (backward compat)
                if (!ulong.TryParse(message.ConversationId, out targetSnowflake))
                {
                    return SendResult.Error($"Cannot route message: unknown channel '{message.ChannelId}'");
                }
            }

            var channel = await this.client.GetChannelAsync(targetSnowflake).ConfigureAwait(false);
            if (channel is not IMessageChannel msgChannel)
            {
                return SendResult.Error($"Channel {targetSnowflake} is not a message channel");
            }

            var text = message.Content.Text ?? "";
            var effectiveChannelId = message.ChannelId ?? message.ConversationId ?? "";

            // Check if this conversation was initiated with a voice message
            var isVoiceReply = this.voiceConversations.TryRemove(effectiveChannelId, out _);

            // Handle media attachments from the agent
            var attachments = message.Content.Attachments;
            var allAttachments = attachments is { Count: > 0 }
                ? new List<MediaAttachment>(attachments)
                : new List<MediaAttachment>();

            // In "voice" response mode, synthesize TTS and send audio instead of text
            if (isVoiceReply
                && this.tts is not null
                && text.Length > 0
                && string.Equals(this.options.DmVoiceReplyMode, "voice", StringComparison.OrdinalIgnoreCase))
            {
                var audioAttachment = await SynthesizeVoiceReplyAsync(text, ct).ConfigureAwait(false);
                if (audioAttachment is not null)
                {
                    allAttachments.Add(audioAttachment);
                    return await SendWithAttachmentsAsync(msgChannel, "", allAttachments, ct).ConfigureAwait(false);
                }
                // TTS failed — fall through to send as text
            }

            if (allAttachments.Count > 0)
            {
                return await SendWithAttachmentsAsync(msgChannel, text, allAttachments, ct).ConfigureAwait(false);
            }

            // Send text, chunked if needed
            var chunks = ChunkText(text, MaxDiscordMessageLength);
            ulong lastMessageId = 0;

            foreach (var chunk in chunks)
            {
                var sent = await msgChannel.SendMessageAsync(chunk).ConfigureAwait(false);
                lastMessageId = sent.Id;
            }

            return SendResult.Ok(lastMessageId.ToString(CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException)
        {
            // Propagate caller cancellation rather than mapping it to a generic
            // delivery failure. Channel-layer mapping of ProactiveDelivery.Dropped →
            // SendResult.Success = false is exercised by the coordinator's outcome
            // flow tests (see ProactiveVoiceCoordinatorTests). A unit test for the
            // voice-tenant branch here would require a fake DiscordVoiceHandler
            // injection seam not currently present in the public API; manual
            // verification covers it post-deploy.
            throw;
        }
        catch (Exception ex)
        {
            this.LogSendFailed(ChannelId, message.ConversationId, ex.Message);
            return SendResult.Error(ex.Message);
        }
    }

    // ── IChannelWithStreaming methods ─────────────────────────────────

    /// <inheritdoc />
    public Task SendTypingIndicatorAsync(string conversationId, CancellationToken ct = default)
    {
        // Discord doesn't have a persistent typing indicator for voice;
        // for text channels we could call TriggerTypingAsync but the LLM
        // streams are short-lived enough that it's not worth the API call.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendStreamingUpdateAsync(string conversationId, string partialText, CancellationToken ct = default)
    {
        if (TryGetVoiceTenantId(conversationId, out var tenantId))
        {
            DiscordVoiceHandler? handler;
            lock (this.voiceHandlersLock)
            {
                this.voiceHandlers.TryGetValue(tenantId, out handler);
            }

            if (handler is { IsConnected: true })
            {
                handler.AcceptTextChunk(partialText);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task FinalizeStreamingAsync(string conversationId, OutboundMessage finalMessage, CancellationToken ct = default)
    {
        // Flush voice handler for voice conversations
        if (TryGetVoiceTenantId(conversationId, out var tenantId))
        {
            DiscordVoiceHandler? handler;
            lock (this.voiceHandlersLock)
            {
                this.voiceHandlers.TryGetValue(tenantId, out handler);
            }

            if (handler is { IsConnected: true })
            {
                handler.FlushAccumulator();
            }

            // Voice conversations: don't send text to a channel (voice is the output)
            return;
        }

        // Text channel finalization (DM or legacy guild)
        if (this.status != ChannelStatus.Connected)
        {
            return;
        }

        try
        {
            ulong targetSnowflake;
            if (string.Equals(finalMessage.ChannelId, DmChannelId, StringComparison.Ordinal)
                     || string.Equals(finalMessage.ConversationId, DmChannelId, StringComparison.Ordinal))
            {
                targetSnowflake = this.dmChannelSnowflake;
                if (targetSnowflake == 0)
                {
                    return;
                }
            }
            else
            {
                if (!ulong.TryParse(finalMessage.ConversationId, out targetSnowflake))
                {
                    return;
                }
            }

            var channel = await this.client.GetChannelAsync(targetSnowflake).ConfigureAwait(false);
            if (channel is not IMessageChannel msgChannel)
            {
                return;
            }

            var text = finalMessage.Content.Text ?? "";

            // Handle voice reply mode for DM voice conversations
            var effectiveChannelId = finalMessage.ChannelId ?? finalMessage.ConversationId;
            var isVoiceReply = this.voiceConversations.TryRemove(effectiveChannelId, out _);

            var attachments = finalMessage.Content.Attachments;
            var allAttachments = attachments is { Count: > 0 }
                ? new List<MediaAttachment>(attachments)
                : new List<MediaAttachment>();

            if (isVoiceReply
                && this.tts is not null
                && text.Length > 0
                && string.Equals(this.options.DmVoiceReplyMode, "voice", StringComparison.OrdinalIgnoreCase))
            {
                var audioAttachment = await SynthesizeVoiceReplyAsync(text, ct).ConfigureAwait(false);
                if (audioAttachment is not null)
                {
                    allAttachments.Add(audioAttachment);
                    await SendWithAttachmentsAsync(msgChannel, "", allAttachments, ct).ConfigureAwait(false);
                    return;
                }
            }

            if (allAttachments.Count > 0)
            {
                await SendWithAttachmentsAsync(msgChannel, text, allAttachments, ct).ConfigureAwait(false);
                return;
            }

            var chunks = ChunkText(text, MaxDiscordMessageLength);
            foreach (var chunk in chunks)
            {
                await msgChannel.SendMessageAsync(chunk).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            this.LogSendFailed(ChannelId, finalMessage.ConversationId, ex.Message);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        this.client.MessageReceived -= this.messageRouter.OnDiscordMessageReceived;
        this.client.Ready -= OnClientReady;
        this.client.Connected -= OnClientConnected;
        this.client.Disconnected -= OnClientDisconnected;
        this.client.Log -= OnDiscordLog;
        this.client.UserVoiceStateUpdated -= OnUserVoiceStateUpdated;

        DiscordVoiceHandler[] handlers;
        lock (this.voiceHandlersLock)
        {
            handlers = [.. this.voiceHandlers.Values];
            this.voiceHandlers.Clear();
        }

        foreach (var handler in handlers)
        {
            await handler.DisposeAsync().ConfigureAwait(false);
        }

        if (this.status != ChannelStatus.Disconnected)
        {
            await DisconnectAsync().ConfigureAwait(false);
        }

        this.client.Dispose();
    }

    // ── Discord event handlers ───────────────────────────────────────

    private async Task OnUserVoiceStateUpdated(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        DiscordVoiceHandler[] handlers;
        lock (this.voiceHandlersLock)
        {
            handlers = [.. this.voiceHandlers.Values];
        }

        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleVoiceStateUpdatedAsync(user, before, after).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.LogVoiceStateUpdateFailed(handler.TenantId, ex.Message);
            }
        }
    }

    private async Task OnClientReady()
    {
        var botUser = this.client.CurrentUser;
        BotUsername = botUser.Username;
        BotUserId = botUser.Id;

        try
        {
            var appInfo = await this.client.GetApplicationInfoAsync().ConfigureAwait(false);
            ApplicationId = appInfo.Id;
        }
        catch (Exception ex)
        {
            this.LogApplicationInfoFailed(ex);
            ApplicationId = botUser.Id;
        }

        await this.slashCommandModule.RegisterSlashCommandsAsync().ConfigureAwait(false);

        this.LogConnected(ChannelId, botUser.Username, botUser.Id);
        SetStatus(ChannelStatus.Connected, $"Connected as {botUser.Username}#{botUser.Discriminator}");
    }

    private Task OnClientConnected()
    {
        // Fires on every gateway (re)connection, including after auto-reconnect.
        // The Ready event only fires on the initial identify; subsequent reconnects
        // fire Connected instead. Without this, the channel stays in Reconnecting
        // state forever after a gateway disconnect.
        if (this.status == ChannelStatus.Reconnecting)
        {
            this.LogReconnected(ChannelId);
            SetStatus(ChannelStatus.Connected, "Reconnected to Discord gateway");

            // A gateway reconnect silently kills any active voice transport.
            // Nudge each voice handler to re-establish if the linked user is
            // still in the channel — otherwise voice stays dead until a manual
            // leave/rejoin (the 2026-05-15 outage).
            DiscordVoiceHandler[] handlers;
            lock (this.voiceHandlersLock)
            {
                handlers = [.. this.voiceHandlers.Values];
            }

            foreach (var handler in handlers)
            {
                _ = handler.OnGatewayReconnectedAsync();
            }
        }

        return Task.CompletedTask;
    }

    private Task OnClientDisconnected(Exception ex)
    {
        this.LogDisconnected(ChannelId, ex.Message);
        // Discord.Net will auto-reconnect; mark as Reconnecting
        SetStatus(ChannelStatus.Reconnecting, ex.Message);
        return Task.CompletedTask;
    }

    private Task OnDiscordLog(LogMessage logMsg)
    {
        // DAVE telemetry: classify + increment counters + rate-limited summary.
        // Intentionally runs before the severity dispatch so even debug-level
        // voice messages (Malformed Frame, Unknown SSRC, DecryptionFailure)
        // feed the counters.
        var kind = DaveEventStats.Classify(logMsg.Source, logMsg.Message);
        if (kind is not DaveEventKind.None)
        {
            this.daveStats.Record(kind);
            this.TryFlushDaveStatsSummary();
        }

        // Out-of-band audio-transport-death signal. A silent "Audio #N: A task was
        // canceled" raises no IAudioClient.Disconnected event and can leave
        // ConnectionState stale at Connected, so the event-driven recovery paths
        // miss it (the 2026-06-28 ~14-minute outage). Flag the voice handler(s) so
        // the watchdog forces a reconnect. The message can arrive on either the
        // log text or the exception, so check both.
        if (AudioDeathLogClassifier.IsAudioTransportDeath(logMsg.Source, logMsg.Message)
            || AudioDeathLogClassifier.IsAudioTransportDeath(logMsg.Source, logMsg.Exception?.Message))
        {
            DiscordVoiceHandler[] handlers;
            lock (this.voiceHandlersLock)
            {
                handlers = [.. this.voiceHandlers.Values];
            }

            foreach (var handler in handlers)
            {
                handler.NotifyAudioTransportSuspectDead();
            }

            this.LogAudioTransportDeathSignal(logMsg.Source, handlers.Length);
        }

        // DAVE end-to-end-encryption (MLS) failure. During the join race the
        // add-proposal for the just-joined user can fail ("Unexpected user ID in
        // add proposal"), wedging the encrypted group so the listener cannot
        // decrypt the bot's audio — the agent "delivers" but the user hears
        // silence, and the session never self-heals (2026-06-29 outage). Flag the
        // voice handler(s) so the watchdog forces one clean rejoin; the handler
        // ignores MLS failures outside the join-race window (benign epoch churn).
        if (kind is DaveEventKind.MlsFailure)
        {
            DiscordVoiceHandler[] handlers;
            lock (this.voiceHandlersLock)
            {
                handlers = [.. this.voiceHandlers.Values];
            }

            foreach (var handler in handlers)
            {
                handler.NotifyDaveSessionSuspect();
            }

            this.LogDaveMlsFailureSignal(handlers.Length);
        }

        if (logMsg.Exception is not null)
        {
            this.LogDiscordLibError(logMsg.Source, logMsg.Exception.Message);
        }
        else if (logMsg.Severity <= LogSeverity.Warning)
        {
            this.LogDiscordLibWarning(logMsg.Source, logMsg.Message);
        }
        else if (logMsg.Source is "Audio" or "Voice")
        {
            this.LogDiscordLibVoiceDebug(logMsg.Source, logMsg.Message);
        }
        else if (logMsg.Severity <= LogSeverity.Debug)
        {
            this.LogDiscordLibDebug(logMsg.Source, logMsg.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Emit an Info-level summary of DAVE counters since the last flush, at
    /// most once per <see cref="DaveStatsFlushIntervalMs"/> ms. Lock-free —
    /// safe to call from the DiscordLog event handler which may be invoked
    /// concurrently.
    /// </summary>
    private void TryFlushDaveStatsSummary()
    {
        var nowTicks = DateTimeOffset.UtcNow.Ticks;
        var last = Interlocked.Read(ref this.daveStatsLastFlushTicks);
        var elapsedMs = (nowTicks - last) / TimeSpan.TicksPerMillisecond;
        if (elapsedMs < DaveStatsFlushIntervalMs)
        {
            return;
        }

        // Claim the flush slot. If another caller beat us, bail.
        if (Interlocked.CompareExchange(ref this.daveStatsLastFlushTicks, nowTicks, last) != last)
        {
            return;
        }

        var current = this.daveStats.Take();
        var delta = current.Delta(this.daveStatsLastFlushed);
        this.daveStatsLastFlushed = current;

        if (delta.Total == 0)
        {
            return;
        }

        this.LogDaveStatsSummary(
            (int)elapsedMs,
            delta.DecryptFailure,
            delta.MissingKeyRatchet,
            delta.InvalidNonce,
            delta.MissingCryptor,
            delta.MalformedFrame,
            delta.UnknownSsrc,
            delta.UnknownUser,
            delta.MlsFailure);
    }

    // ── Private helpers ──────────────────────────────────────────────

    private static async Task<SendResult> SendWithAttachmentsAsync(
        IMessageChannel channel, string text, IReadOnlyList<MediaAttachment> attachments, CancellationToken ct)
    {
        // Send the first attachment with text, rest as separate messages
        ulong lastId = 0;

        foreach (var att in attachments)
        {
            if (att.Data is { Length: > 0 })
            {
                // Binary data — upload as file
                using var stream = new MemoryStream(att.Data);
                var msg = await channel.SendFileAsync(
                    stream,
                    att.FileName ?? "file",
                    text.Length > 0 ? text : null).ConfigureAwait(false);
                lastId = msg.Id;
                text = ""; // Only include text with the first attachment
            }
            else if (!string.IsNullOrEmpty(att.Url))
            {
                // URL reference — include in message text
                var msgText = text.Length > 0 ? $"{text}\n{att.Url}" : att.Url;
                var msg = await channel.SendMessageAsync(msgText).ConfigureAwait(false);
                lastId = msg.Id;
                text = "";
            }
        }

        // If there's remaining text (no attachments were sent), send it
        if (text.Length > 0)
        {
            var msg = await channel.SendMessageAsync(text).ConfigureAwait(false);
            lastId = msg.Id;
        }

        return SendResult.Ok(lastId.ToString(CultureInfo.InvariantCulture));
    }

    private void SetStatus(ChannelStatus newStatus, string? reason = null)
    {
        var prev = this.status;
        this.status = newStatus;

        if (prev != newStatus && StatusChanged is { } handler)
        {
            _ = handler(new ChannelStatusChange(prev, newStatus, reason));
        }
    }

    // ── IDiscordChannelHost ──────────────────────────────────────────

    /// <inheritdoc />
    Task IDiscordChannelHost.RaiseMessageReceivedAsync(InboundMessage message)
    {
        if (MessageReceived is { } handler)
        {
            return handler(message);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    void IDiscordChannelHost.SetDmChannelSnowflake(ulong snowflake) => this.dmChannelSnowflake = snowflake;

    /// <inheritdoc />
    void IDiscordChannelHost.SetDmRecipientUserId(ulong userId) => this.dmRecipientUserId = userId;

    /// <inheritdoc />
    void IDiscordChannelHost.MarkVoiceConversation(string channelId) => this.voiceConversations[channelId] = true;

    /// <summary>
    /// Opens (or reuses) the DM channel for <paramref name="userId"/> and returns its snowflake,
    /// or 0 when the user cannot be resolved or a DM cannot be opened. Mirrors the voice handler's
    /// proactive-DM open path so text DMs no longer require a prior inbound DM to prime the snowflake.
    /// </summary>
    private async Task<ulong> OpenDmChannelAsync(ulong userId)
    {
        try
        {
            var user = await this.client.GetUserAsync(userId).ConfigureAwait(false);
            if (user is null)
            {
                this.LogDmOpenUserMissing(userId);
                return 0;
            }

            var dm = await user.CreateDMChannelAsync().ConfigureAwait(false);
            return dm.Id;
        }
        catch (Exception ex)
        {
            this.LogDmOpenFailed(userId, ex.Message);
            return 0;
        }
    }

    /// <inheritdoc />
    DiscordVoiceHandler? IDiscordChannelHost.TryGetVoiceHandler(string tenantId)
    {
        lock (this.voiceHandlersLock)
        {
            this.voiceHandlers.TryGetValue(tenantId, out var handler);
            return handler;
        }
    }

    /// <summary>
    /// Synthesize text to an OGG/Opus audio file suitable for Discord voice messages.
    /// Returns a MediaAttachment with binary data, or null on failure.
    /// </summary>
    private async Task<MediaAttachment?> SynthesizeVoiceReplyAsync(string text, CancellationToken ct)
    {
        try
        {
            this.LogVoiceSynthesizing(ChannelId, text.Length);

            // Synthesize text → PCM at TTS engine's native rate
            var pcmData = await this.tts!.SynthesizeAsync(text, cancellationToken: ct).ConfigureAwait(false);
            var ttsFormat = this.tts.OutputFormat;

            // Encode to OGG/Opus — the encoder handles resampling to 48kHz internally
            var oggData = AudioConverter.EncodeOggOpus(pcmData, ttsFormat.SampleRate);

            this.LogVoiceSynthesisDone(ChannelId, oggData.Length);

            return new MediaAttachment
            {
                MimeType = OggContentType,
                FileName = "reply.ogg",
                Data = oggData,
            };
        }
        catch (Exception ex)
        {
            this.LogVoiceSynthesisFailed(ChannelId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Parses a voice conversation ID of the form <c>"discord-voice-{tenantId}"</c>
    /// and extracts the tenant ID. Returns false for non-voice conversation IDs
    /// or malformed inputs (null, empty, missing tenant suffix). Used for stateless
    /// routing of streaming/finalize callbacks to the correct tenant's voice handler —
    /// replaces a previous in-memory map that was populated on user transcription
    /// and cleaned up on finalize, which dropped agent-initiated messages (delayed
    /// tool replies, proactive messages, multi-turn follow-ups) that had no
    /// preceding user turn.
    /// </summary>
    internal static bool TryGetVoiceTenantId(string? conversationId, out string tenantId)
    {
        const string Prefix = "discord-voice-";
        if (conversationId is not null
            && conversationId.StartsWith(Prefix, StringComparison.Ordinal)
            && conversationId.Length > Prefix.Length)
        {
            tenantId = conversationId[Prefix.Length..];
            return true;
        }

        tenantId = string.Empty;
        return false;
    }

    internal static List<string> ChunkText(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return [text];
        }

        var chunks = new List<string>();
        var remaining = text.AsSpan();

        while (remaining.Length > 0)
        {
            if (remaining.Length <= maxLength)
            {
                chunks.Add(remaining.ToString());
                break;
            }

            // Try to split at a newline within the limit
            var slice = remaining[..maxLength];
            var splitAt = slice.LastIndexOf('\n');
            if (splitAt < maxLength / 2)
            {
                splitAt = -1; // Don't split too early
            }

            if (splitAt == -1)
            {
                // Fall back to space
                splitAt = slice.LastIndexOf(' ');
            }

            if (splitAt == -1)
            {
                splitAt = maxLength; // Hard cut
            }

            chunks.Add(remaining[..splitAt].ToString());
            remaining = remaining[splitAt..].TrimStart("\n");
        }

        return chunks;
    }

    // ── LoggerMessage source-generated methods ───────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord channel {ChannelId} connecting")]
    private partial void LogConnecting(string channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord channel {ChannelId} disconnecting")]
    private partial void LogDisconnecting(string channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord channel {ChannelId} connected as {BotUsername} ({BotId})")]
    private partial void LogConnected(string channelId, string botUsername, ulong botId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to fetch Discord application info, falling back to bot user ID for OAuth2 URL")]
    private partial void LogApplicationInfoFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord channel {ChannelId} disconnected: {Reason}")]
    private partial void LogDisconnected(string channelId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord channel {ChannelId} reconnected to gateway")]
    private partial void LogReconnected(string channelId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Discord channel {ChannelId} failed to send to {ConversationId}: {ErrorMessage}")]
    private partial void LogSendFailed(string channelId, string conversationId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Proactive DM open skipped — Discord user {UserId} not found (not in a shared guild or blocked the bot)")]
    private partial void LogDmOpenUserMissing(ulong userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Proactive DM open failed for user {UserId}: {ErrorMessage}")]
    private partial void LogDmOpenFailed(ulong userId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord.Net [{Source}]: {ErrorMessage}")]
    private partial void LogDiscordLibError(string source, string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Discord.Net [{Source}]: {WarningMessage}")]
    private partial void LogDiscordLibWarning(string source, string warningMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord.Net voice [{Source}]: {VoiceMessage}")]
    private partial void LogDiscordLibVoiceDebug(string source, string voiceMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Discord.Net debug [{Source}]: {DebugMessage}")]
    private partial void LogDiscordLibDebug(string source, string debugMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "DAVE stats last {ElapsedMs}ms: decryptFail={DecryptFail} missingKeyRatchet={MissingKeyRatchet} invalidNonce={InvalidNonce} missingCryptor={MissingCryptor} malformed={Malformed} unknownSsrc={UnknownSsrc} unknownUser={UnknownUser} mlsFail={MlsFail}")]
    private partial void LogDaveStatsSummary(int elapsedMs, long decryptFail, long missingKeyRatchet, long invalidNonce, long missingCryptor, long malformed, long unknownSsrc, long unknownUser, long mlsFail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audio-transport death signal from [{Source}] — flagged {HandlerCount} voice handler(s) for watchdog reconnect")]
    private partial void LogAudioTransportDeathSignal(string source, int handlerCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DAVE/MLS failure signal — flagged {HandlerCount} voice handler(s) to rejoin if within the join-race window")]
    private partial void LogDaveMlsFailureSignal(int handlerCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord channel {ChannelId} synthesizing voice reply ({CharCount} chars)")]
    private partial void LogVoiceSynthesizing(string channelId, int charCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord channel {ChannelId} voice synthesis complete ({ByteCount} bytes OGG)")]
    private partial void LogVoiceSynthesisDone(string channelId, int byteCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Discord channel {ChannelId} voice synthesis failed: {ErrorMessage}")]
    private partial void LogVoiceSynthesisFailed(string channelId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Voice handler created for tenant '{TenantId}' (guild={GuildId}, channel={VoiceChannelId})")]
    private partial void LogVoiceHandlerCreated(string tenantId, ulong guildId, ulong voiceChannelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Voice handler removed for tenant '{TenantId}'")]
    private partial void LogVoiceHandlerRemoved(string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Voice handler dispose failed: {ErrorMessage}")]
    private partial void LogVoiceHandlerDisposeFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Voice reconcile skipped: {Reason}")]
    private partial void LogVoiceReconcileSkipped(string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Voice state update failed for tenant '{TenantId}': {ErrorMessage}")]
    private partial void LogVoiceStateUpdateFailed(string tenantId, string errorMessage);
}
