using Cortex.Contained.Bridge.Channels;
using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Bridge.Recording;
using Cortex.Contained.Channels.Discord;
using Cortex.Contained.Channels.Voice;
using Cortex.Contained.Channels.WebChat;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Recording;

namespace Cortex.Contained.Bridge.Hosting;

/// <summary>
/// Owns channel registration with the <see cref="ChannelManager"/>, Discord
/// slash-command routing, the active-channel ID list, and reconciliation of
/// voice handlers from tenant config.
///
/// Extracted from <see cref="Worker"/> as the channel-lifecycle responsibility.
/// All logic is moved verbatim; only the host fields it depends on are now
/// injected explicitly.
/// </summary>
public sealed partial class ChannelLifecycleManager
{
    private readonly Tenants.TenantRouter tenantRouter;
    private readonly Tenants.TenantRegistry tenantRegistry;
    private readonly ChannelManager channelManager;
    private readonly WebChatChannel webChatChannel;
    private readonly VoiceChannel? voiceChannel;
    private readonly DiscordChannel? discordChannel;
    private readonly DiscordChannelOptions? discordOptions;
    private readonly BridgeConfig config;
    private readonly Cortex.Contained.Speech.SpeakerId.ISpeakerVerifier? speakerVerifier;
    private readonly Cortex.Contained.Speech.SpeakerId.VerificationMetrics verificationMetrics;
    private readonly Cortex.Contained.Speech.SpeakerId.ISpeakerEmbedder? speakerEmbedder;
    private readonly Cortex.Contained.Speech.Tts.ILanguageDetector? languageDetector;
    private readonly Cortex.Contained.Speech.Tts.ChannelLanguageStore? languageStore;
    private readonly Cortex.Contained.Channels.Discord.IEnrollmentProgressNotifier? enrollmentProgressNotifier;
    private readonly IRecordingController? recordingController;
    private readonly ILogger<ChannelLifecycleManager> logger;

    public ChannelLifecycleManager(
        Tenants.TenantRouter tenantRouter,
        Tenants.TenantRegistry tenantRegistry,
        ChannelManager channelManager,
        WebChatChannel webChatChannel,
        BridgeConfig config,
        ILogger<ChannelLifecycleManager> logger,
        VoiceChannel? voiceChannel = null,
        DiscordChannel? discordChannel = null,
        DiscordChannelOptions? discordOptions = null,
        Cortex.Contained.Speech.SpeakerId.ISpeakerVerifier? speakerVerifier = null,
        Cortex.Contained.Speech.SpeakerId.VerificationMetrics? verificationMetrics = null,
        Cortex.Contained.Channels.Discord.IEnrollmentProgressNotifier? enrollmentProgressNotifier = null,
        IRecordingController? recordingController = null,
        Cortex.Contained.Speech.SpeakerId.ISpeakerEmbedder? speakerEmbedder = null,
        Cortex.Contained.Speech.Tts.ILanguageDetector? languageDetector = null,
        Cortex.Contained.Speech.Tts.ChannelLanguageStore? languageStore = null)
    {
        this.tenantRouter = tenantRouter;
        this.tenantRegistry = tenantRegistry;
        this.channelManager = channelManager;
        this.webChatChannel = webChatChannel;
        this.config = config;
        this.logger = logger;
        this.voiceChannel = voiceChannel;
        this.discordChannel = discordChannel;
        this.discordOptions = discordOptions;
        this.speakerVerifier = speakerVerifier;
        this.verificationMetrics = verificationMetrics ?? new Cortex.Contained.Speech.SpeakerId.VerificationMetrics();
        this.enrollmentProgressNotifier = enrollmentProgressNotifier;
        this.recordingController = recordingController;
        this.speakerEmbedder = speakerEmbedder;
        this.languageDetector = languageDetector;
        this.languageStore = languageStore;
    }

    public void RegisterChannels()
    {
        if (this.config.WebUi.Enabled)
        {
            this.channelManager.RegisterChannel(this.webChatChannel);
            this.LogChannelRegistered("WebChat", this.webChatChannel.ChannelId);
        }

        if (this.voiceChannel is not null)
        {
            this.channelManager.RegisterChannel(this.voiceChannel);
            this.LogChannelRegistered("Voice", this.voiceChannel.ChannelId);
        }

        if (this.discordChannel is not null)
        {
            this.channelManager.RegisterChannel(this.discordChannel);
            this.LogChannelRegistered("Discord", this.discordChannel.ChannelId);

            // DiscordChannel is registered under its primary ChannelId ("discord-dm").
            // Realtime voice conversations carry ChannelId="discord-voice" on their
            // inbound and outbound messages, so alias the same instance under that ID
            // to make Bridge.HubMessageDispatcher.TryGetChannel("discord-voice") resolve.
            this.channelManager.RegisterChannelAlias("discord-voice", this.discordChannel);
            this.LogChannelRegistered("Discord", "discord-voice");

            // Wire slash command lifecycle. The channel handles defer/reply,
            // we route the actual side-effect (Agent.Host call) here.
            this.discordChannel.TenantResolver = userId =>
            {
                var idStr = userId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                foreach (var (tenantId, tenant) in this.tenantRegistry.GetAll())
                {
                    if (string.Equals(tenant.DiscordUserId, idStr, StringComparison.Ordinal))
                    {
                        return tenantId;
                    }
                }
                return null;
            };
            // Voice-record slash command runs first; if it returns null the
            // existing voice-id handler is asked. Channels offered to a tenant
            // are STRICTLY the ones it has configured:
            //   - the single Discord voice channel in TenantConfig.DiscordVoiceChannelId
            //     (resolved to its current display name via Discord client),
            //   - the literal "host" entry IFF the host voice channel is bound
            //     to this tenant (VoiceChannel.TenantId == tenantId).
            // The same Func is given to the slash-command handler AND the
            // autocomplete resolver so both see the same set.
            if (this.recordingController is not null)
            {
                var discord = this.discordChannel;
                var hostChannel = this.voiceChannel;

                Func<string?, IReadOnlyList<(string Name, ulong Id)>> available = tenantId =>
                {
                    if (string.IsNullOrEmpty(tenantId))
                    {
                        return Array.Empty<(string, ulong)>();
                    }

                    var list = new List<(string Name, ulong Id)>(2);

                    var tenant = this.tenantRegistry.GetAll().GetValueOrDefault(tenantId);
                    if (tenant is not null
                        && !string.IsNullOrWhiteSpace(tenant.DiscordGuildId)
                        && !string.IsNullOrWhiteSpace(tenant.DiscordVoiceChannelId)
                        && ulong.TryParse(tenant.DiscordGuildId, out var guildId)
                        && ulong.TryParse(tenant.DiscordVoiceChannelId, out var channelId))
                    {
                        var name = discord.GetVoiceChannelName(guildId, channelId);
                        if (!string.IsNullOrEmpty(name))
                        {
                            list.Add((name, channelId));
                        }
                    }

                    if (hostChannel is not null
                        && string.Equals(hostChannel.TenantId, tenantId, StringComparison.Ordinal))
                    {
                        list.Add(("host", 0UL));
                    }

                    return list;
                };

                var voiceRecord = new VoiceRecordSlashCommandHandler(this.recordingController, available);
                this.discordChannel.AvailableVoiceChannelsResolver = available;
                this.discordChannel.SlashCommandHandler = async req =>
                    await voiceRecord.HandleAsync(req).ConfigureAwait(false)
                        ?? await HandleSlashCommandAsync(req).ConfigureAwait(false);
            }
            else
            {
                this.discordChannel.SlashCommandHandler = HandleSlashCommandAsync;
            }
            this.discordChannel.EnrollmentProgressNotifier = this.enrollmentProgressNotifier;

            // Voice barge-in: route the Bridge-detected interruption to the
            // tenant's Agent Host over SignalR. Resolved per-call via
            // TenantRouter because HubClient is per-tenant (not a singleton) —
            // same model as HandleSlashCommandAsync above.
            this.discordChannel.TurnInterruptedHandler = async (tenantId, notification) =>
            {
                var client = this.tenantRouter.GetClient(tenantId);
                if (client is null || !client.IsConnected)
                {
                    this.LogVoiceBargeInHubUnavailable(tenantId, "OnTurnInterrupted");
                    return;
                }

                await client.OnTurnInterruptedAsync(notification, CancellationToken.None).ConfigureAwait(false);
            };
            this.discordChannel.AbortGenerationHandler = async (tenantId, conversationId) =>
            {
                var client = this.tenantRouter.GetClient(tenantId);
                if (client is null || !client.IsConnected)
                {
                    this.LogVoiceBargeInHubUnavailable(tenantId, "AbortGeneration");
                    return;
                }

                await client.AbortGenerationAsync(conversationId, CancellationToken.None).ConfigureAwait(false);
            };
        }
    }

    private async Task<SlashCommandResult?> HandleSlashCommandAsync(SlashCommandRequest request)
    {
        if (request.TenantId is null)
        {
            return new SlashCommandResult("Your Discord account isn't linked to a tenant. Use the pairing flow first.");
        }

        var client = this.tenantRouter.GetClient(request.TenantId);
        if (client is null || !client.IsConnected)
        {
            return new SlashCommandResult($"Agent for tenant `{request.TenantId}` is not connected. Try again in a moment.");
        }

        try
        {
            return request.CommandPath switch
            {
                "voice-id/status" => await DescribeVoiceIdStatusAsync(client, request.TenantId).ConfigureAwait(false),
                "voice-id/enroll" => await this.StartVoiceIdEnrollmentAsync(client, request.TenantId, request.CurrentVoiceChannelId).ConfigureAwait(false),
                "voice-id/forget" => await ForgetVoiceIdAsync(client, request.TenantId).ConfigureAwait(false),
                "voice-id/enable" => await SetVoiceIdFeatureAsync(client, request.TenantId, enabled: true).ConfigureAwait(false),
                "voice-id/disable" => await SetVoiceIdFeatureAsync(client, request.TenantId, enabled: false).ConfigureAwait(false),
                "compact" => await RunInlineSlashCommandAsync(client, "/compact", request.SourceChannelKind).ConfigureAwait(false),
                "context" => await RunInlineSlashCommandAsync(client, "/context", request.SourceChannelKind).ConfigureAwait(false),
                _ => new SlashCommandResult($"Unknown subcommand: `{request.CommandPath}`."),
            };
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            return new SlashCommandResult($"Error: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Run an inline slash command (/compact, /context) against the Cortex
    /// channel that matches the *Discord* channel the user invoked from:
    /// Discord DM → <c>discord-dm</c>, guild text channel → <c>discord-guild</c>.
    /// This matches the text-prefix path's channel mapping at
    /// DiscordChannel.cs:1100-1115 — `/compact` typed as a plain message and
    /// `/compact` invoked via Discord slash command from the same surface now
    /// compact the same Cortex conversation.
    /// </summary>
    private static async Task<SlashCommandResult> RunInlineSlashCommandAsync(
        HubClient client, string commandText, string? sourceChannelKind)
    {
        var channelId = sourceChannelKind == "guild" ? "discord-guild" : "discord-dm";
        var text = await client.RunInlineSlashCommandAsync(channelId, commandText, CancellationToken.None)
            .ConfigureAwait(false);
        return new SlashCommandResult(text);
    }

    private static async Task<SlashCommandResult> DescribeVoiceIdStatusAsync(HubClient client, string tenantId)
    {
        var snapshot = await client.GetVoiceprintAsync(tenantId, CancellationToken.None).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new SlashCommandResult("**Voice ID**: no record yet (state: `Unknown`). The agent will offer enrollment shortly.");
        }

        var thresholdLine = snapshot.ThresholdOverride is { } th
            ? $" — threshold override `{th:F2}`"
            : string.Empty;
        return new SlashCommandResult(
            $"**Voice ID**: state `{snapshot.StateName}`, feature {(snapshot.FeatureEnabled ? "enabled" : "disabled")}{thresholdLine}.");
    }

    private static async Task<SlashCommandResult> ForgetVoiceIdAsync(HubClient client, string tenantId)
    {
        await client.ResetVoiceEnrollmentAsync(tenantId, CancellationToken.None).ConfigureAwait(false);
        return new SlashCommandResult("Voiceprint wiped. State is now `Declined`. Run `/voice-id enroll` to set it up again.");
    }

    private async Task<SlashCommandResult> StartVoiceIdEnrollmentAsync(
        HubClient client,
        string tenantId,
        ulong? currentVoiceChannelId)
    {
        var error = await client.StartVoiceEnrollmentAsync(tenantId, CancellationToken.None).ConfigureAwait(false);
        if (error is not null)
        {
            return new SlashCommandResult(error);
        }

        var gate = EnrollGateDecision.RingAndProceed; // default: ring if we can't resolve config
        var tenantConfig = this.tenantRegistry.GetAll().GetValueOrDefault(tenantId);
        if (tenantConfig is not null
            && ulong.TryParse(tenantConfig.DiscordVoiceChannelId, out var configuredVoiceChannelId))
        {
            gate = EnrollVoiceStateGate.Decide(currentVoiceChannelId, configuredVoiceChannelId);
        }

        var action = WizardEntryDecision.Decide(enrollStarted: true, gate);
        var replyText = action == WizardEntryAction.StartNow
            ? "Enrollment started — listen for my voice in the channel and repeat each phrase."
            : "Enrollment started. I'll call you into the voice channel — join and I'll guide you by voice.";

        return new SlashCommandResult(
            replyText,
            RingVoice: action == WizardEntryAction.RingThenStart,
            StartWizard: action == WizardEntryAction.StartNow);
    }

    private static async Task<SlashCommandResult> SetVoiceIdFeatureAsync(HubClient client, string tenantId, bool enabled)
    {
        await client.SetVoiceFeatureEnabledAsync(tenantId, enabled, CancellationToken.None).ConfigureAwait(false);
        return new SlashCommandResult(enabled
            ? "Voice identification **enabled**. The agent will offer enrollment after a few utterances."
            : "Voice identification **disabled**. The verification gate is now inert.");
    }

    /// <summary>
    /// Builds the array of active channel IDs based on registered channels and configuration.
    /// </summary>
    public string[] BuildActiveChannelIds()
    {
        var ids = new List<string>();

        if (this.config.WebUi.Enabled)
        {
            ids.Add("webchat-default");
        }

        if (this.discordChannel is not null)
        {
            // DMs are always available when Discord is enabled
            ids.Add("discord-dm");

            // Guild text channel is available if any tenant has a guild configured
            var hasGuildTenant = this.tenantRegistry.GetAll()
                .Any(t => !string.IsNullOrWhiteSpace(t.Value.DiscordGuildId));
            if (hasGuildTenant)
            {
                ids.Add("discord-guild");
            }

            // Realtime voice is available if any tenant has a Discord voice channel configured
            var hasVoiceTenant = this.tenantRegistry.GetAll()
                .Any(t => !string.IsNullOrWhiteSpace(t.Value.DiscordVoiceChannelId));
            if (hasVoiceTenant)
            {
                ids.Add("discord-voice");
            }
        }

        if (this.voiceChannel is not null)
        {
            ids.Add("voice-default");
        }

        return ids.ToArray();
    }

    /// <summary>
    /// Builds <see cref="VoiceHandlerConfig"/> entries from the tenant registry
    /// and reconciles them with the <see cref="DiscordChannel"/>.
    /// Called at startup after channels connect, and can be called on reconnect.
    /// </summary>
    public async Task ReconcileVoiceHandlersFromConfigAsync()
    {
        if (this.discordChannel is null || this.discordOptions is null)
        {
            return;
        }

        Func<string, float[], string, Task> submitVoiceprint = async (tenantId, embedding, modelId) =>
        {
            var client = this.tenantRouter.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                this.LogVoiceprintSubmitSkipped(tenantId);
                return;
            }

            await client.SubmitVoiceprintAsync(tenantId, embedding, modelId, CancellationToken.None).ConfigureAwait(false);
        };

        var voiceConfigs = Tenants.TenantEndpoints.BuildVoiceHandlerConfigs(
            this.tenantRegistry,
            this.discordOptions,
            this.recordingController,
            this.speakerVerifier,
            this.verificationMetrics,
            this.speakerEmbedder,
            submitVoiceprint,
            this.languageDetector,
            this.languageStore);
        await this.discordChannel.ReconcileVoiceHandlers(voiceConfigs).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Channel registered: {ChannelType} ({ChannelId})")]
    private partial void LogChannelRegistered(string channelType, string channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Voice barge-in: agent for tenant '{TenantId}' not connected — skipping {HubCall}")]
    private partial void LogVoiceBargeInHubUnavailable(string tenantId, string hubCall);

    [LoggerMessage(Level = LogLevel.Warning, Message = "voice-enroll: voiceprint submit skipped, agent not connected tenant={TenantId}")]
    private partial void LogVoiceprintSubmitSkipped(string tenantId);
}
