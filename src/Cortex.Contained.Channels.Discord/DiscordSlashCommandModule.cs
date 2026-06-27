namespace Cortex.Contained.Channels.Discord;

using global::Discord;
using global::Discord.WebSocket;

using Microsoft.Extensions.Logging;

/// <summary>
/// Owns Discord slash-command lifecycle for <see cref="DiscordChannel"/>:
/// command-tree registration (<c>/voice-id</c>, <c>/voice-record</c>,
/// <c>/compact</c>, <c>/context</c>), interaction dispatch
/// (<see cref="SocketSlashCommand"/>), and <c>/voice-record</c> channel
/// autocomplete. The facade subscribes this module's handlers to the socket
/// client and delegates registration from its <c>Ready</c> handler. All actions
/// that need live channel state (tenant resolution, the Bridge-wired dispatch
/// callback, voice handlers) are read through <see cref="IDiscordChannelHost"/>.
/// </summary>
internal sealed partial class DiscordSlashCommandModule
{
    private readonly ILogger logger;
    private readonly DiscordSocketClient client;
    private readonly IDiscordChannelHost host;

    private bool slashCommandsRegistered;

    public DiscordSlashCommandModule(
        ILogger logger,
        DiscordSocketClient client,
        IDiscordChannelHost host)
    {
        this.logger = logger;
        this.client = client;
        this.host = host;
    }

    public async Task OnAutocompleteExecuted(SocketAutocompleteInteraction interaction)
    {
        // We only autocomplete the channel: option for /voice-record start|stop.
        if (!string.Equals(interaction.Data.CommandName, "voice-record", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var focused = interaction.Data.Current;
            if (focused is null || !string.Equals(focused.Name, "channel", StringComparison.Ordinal))
            {
                await interaction.RespondAsync(Array.Empty<AutocompleteResult>()).ConfigureAwait(false);
                return;
            }

            var typed = (focused.Value as string ?? string.Empty).Trim();

            // Tenant-scoped suggestions when the Bridge has wired the
            // resolver: only voice channels actually configured for the
            // invoker's tenant (their Discord voice channel + optionally
            // "host"). The else-branch is a guild-only safety net for
            // deployments without a wired resolver and DM autocompletes
            // resolve to an empty list — same behaviour they had pre-resolver.
            IReadOnlyList<(string Name, ulong Id)> available;
            var resolver = this.host.AvailableVoiceChannelsResolver;
            if (resolver is not null)
            {
                var tenantId = this.host.TenantResolver?.Invoke(interaction.User.Id);
                available = resolver(tenantId);
            }
            else if (interaction.User is SocketGuildUser guildUser)
            {
                var hostEntry = new[] { ("host", 0UL) };
                available = hostEntry
                    .Concat(guildUser.Guild.VoiceChannels.Select(vc => (vc.Name, vc.Id)))
                    .ToArray();
            }
            else
            {
                available = Array.Empty<(string, ulong)>();
            }

            var suggestions = new List<AutocompleteResult>();
            foreach (var (name, _) in available)
            {
                if (string.IsNullOrEmpty(typed)
                    || name.Contains(typed, StringComparison.OrdinalIgnoreCase))
                {
                    suggestions.Add(new AutocompleteResult(name, name));
                    if (suggestions.Count >= 25)
                    {
                        break;
                    }
                }
            }

            await interaction.RespondAsync(suggestions).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Autocomplete failures must not break the interaction lifecycle.
        catch (Exception ex)
        {
            this.LogSlashCommandFailed("autocomplete:voice-record", ex.Message);
            try
            {
                await interaction.RespondAsync(Array.Empty<AutocompleteResult>()).ConfigureAwait(false);
            }
            catch
            {
                // Already responded / interaction gone — ignore.
            }
        }
#pragma warning restore CA1031
    }

    public async Task RegisterSlashCommandsAsync()
    {
        // Discord rate-limits global command registration to ~1/min per app
        // and propagates over up to an hour. Ready fires on every reconnect,
        // so guard against re-registering. Discord.Net upserts by name, so
        // even on failure we won't lose commands across reconnects.
        if (this.slashCommandsRegistered)
        {
            return;
        }

        try
        {
            var builder = new SlashCommandBuilder()
                .WithName("voice-id")
                .WithDescription("Manage voice identification for this tenant.")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("status")
                    .WithDescription("Show current voice-identification status.")
                    .WithType(ApplicationCommandOptionType.SubCommand))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("enroll")
                    .WithDescription("Begin voice enrollment. Speak in the voice channel after running this.")
                    .WithType(ApplicationCommandOptionType.SubCommand))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("forget")
                    .WithDescription("Wipe the voiceprint and disable verification.")
                    .WithType(ApplicationCommandOptionType.SubCommand))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("enable")
                    .WithDescription("Turn the voice-identification feature on.")
                    .WithType(ApplicationCommandOptionType.SubCommand))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("disable")
                    .WithDescription("Turn the voice-identification feature off.")
                    .WithType(ApplicationCommandOptionType.SubCommand));

            await this.client.CreateGlobalApplicationCommandAsync(builder.Build()).ConfigureAwait(false);

            // /voice-record start|stop|status|list. start/stop take a REQUIRED
            // `channel:` argument restricted to this tenant's configured voice
            // channels (Discord guild's configured channel + `host` if bound to
            // this tenant). Autocomplete shows exactly that set. `list` prints
            // it as a markdown reply. Spec:
            // docs/superpowers/specs/2026-05-20-voice-recording-slash-commands-design.md
            var record = new SlashCommandBuilder()
                .WithName("voice-record")
                .WithDescription("Record voice from a channel to a session WAV + events.jsonl for offline EOU eval.")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("start")
                    .WithDescription("Start recording a voice channel.")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("channel")
                        .WithDescription("Voice channel to record. Pick from autocomplete.")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)
                        .WithAutocomplete(true)))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("stop")
                    .WithDescription("Stop an active recording session.")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("channel")
                        .WithDescription("Voice channel whose recording to stop. Pick from autocomplete.")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)
                        .WithAutocomplete(true)))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("status")
                    .WithDescription("List all active recording sessions.")
                    .WithType(ApplicationCommandOptionType.SubCommand))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("list")
                    .WithDescription("Show the voice channels this tenant can record.")
                    .WithType(ApplicationCommandOptionType.SubCommand));

            await this.client.CreateGlobalApplicationCommandAsync(record.Build()).ConfigureAwait(false);

            // /compact and /context — Discord application commands that mirror
            // the existing text-prefix triggers handled by AgentRuntime. Type
            // them in any channel where this bot is present; the Bridge calls
            // IAgentHub.RunInlineSlashCommand and posts the result as the
            // (ephemeral) interaction reply. The text-prefix path keeps working
            // in WebChat / voice-channel / Discord chat messages alongside.
            var compact = new SlashCommandBuilder()
                .WithName("compact")
                .WithDescription("Flush extraction buffer and compact this channel's conversation history.");
            await this.client.CreateGlobalApplicationCommandAsync(compact.Build()).ConfigureAwait(false);

            var context = new SlashCommandBuilder()
                .WithName("context")
                .WithDescription("Show current context window usage: prompt tokens, window size, %, messages, model.");
            await this.client.CreateGlobalApplicationCommandAsync(context.Build()).ConfigureAwait(false);

            this.slashCommandsRegistered = true;
            this.LogSlashCommandsRegistered();
        }
#pragma warning disable CA1031 // Registration failure must not block channel startup.
        catch (Exception ex)
        {
            this.LogSlashCommandRegistrationFailed(ex.Message);
        }
#pragma warning restore CA1031
    }

    public async Task OnSlashCommandExecuted(SocketSlashCommand command)
    {
        if (!string.Equals(command.CommandName, "voice-id", StringComparison.Ordinal)
            && !string.Equals(command.CommandName, "voice-record", StringComparison.Ordinal)
            && !string.Equals(command.CommandName, "compact", StringComparison.Ordinal)
            && !string.Equals(command.CommandName, "context", StringComparison.Ordinal))
        {
            return;
        }

        // /compact and /context are top-level (no subcommands); commandPath is
        // just the command name, e.g. "compact".
        var isInline = string.Equals(command.CommandName, "compact", StringComparison.Ordinal)
            || string.Equals(command.CommandName, "context", StringComparison.Ordinal);

        var subOption = isInline ? null : command.Data.Options.FirstOrDefault();
        var subcommand = subOption?.Name ?? (isInline ? string.Empty : "status");

        // voice-record start/stop accept an optional `channel` string sub-option.
        string? channelArg = null;
        if (subOption?.Options is { } subOpts)
        {
            channelArg = subOpts
                .FirstOrDefault(o => string.Equals(o.Name, "channel", StringComparison.Ordinal))?
                .Value as string;
        }

        await command.DeferAsync(ephemeral: true).ConfigureAwait(false);

        try
        {
            var tenantId = this.host.TenantResolver?.Invoke(command.User.Id);
            var handler = this.host.SlashCommandHandler;
            if (handler is null)
            {
                await command.FollowupAsync("Slash commands are not wired in this build.", ephemeral: true).ConfigureAwait(false);
                return;
            }

            var currentVoiceChannelId = (command.User as SocketGuildUser)?.VoiceChannel?.Id;
            var commandPath = isInline ? command.CommandName : $"{command.CommandName}/{subcommand}";
            var sourceKind = command.Channel switch
            {
                IDMChannel => "dm",
                SocketGuildChannel => "guild",
                _ => null,
            };
            var request = new SlashCommandRequest(
                tenantId, command.User.Id, commandPath, currentVoiceChannelId, channelArg, sourceKind);
            var result = await handler(request).ConfigureAwait(false);
            var message = result?.Message ?? "Not handled.";
            await command.FollowupAsync(message, ephemeral: result?.Ephemeral ?? true).ConfigureAwait(false);

            // Track the text channel so enrollment progress notifications can be delivered here.
            // We track on any enroll attempt with a known tenant — if the state transition did not
            // succeed the orchestrator will never push a progress event, so the entry is harmless.
            if (string.Equals(subcommand, "enroll", StringComparison.Ordinal)
                && tenantId is not null
                && command.ChannelId is { } enrollChannelId
                && this.host.EnrollmentProgressNotifier is { } notifier)
            {
                notifier.TrackInteractionChannel(tenantId, enrollChannelId);
            }

            // Start the spoken wizard now (user already in voice) or arm it to start
            // when the user joins after the ring.
            if (tenantId is not null && result is { StartWizard: true } or { RingVoice: true })
            {
                var voiceHandler = this.host.TryGetVoiceHandler(tenantId);

                if (voiceHandler is not null)
                {
                    if (result.StartWizard)
                    {
                        // Fire-and-forget: the wizard speaks the intro on the live connection.
                        _ = voiceHandler.StartEnrollmentWizardAsync();
                    }
                    else
                    {
                        voiceHandler.ArmPendingEnrollment();
                        _ = voiceHandler.EnqueueProactiveAsync(
                            "You started voice-ID enrollment from text. Join the voice channel and I'll guide you by voice.",
                            ct: CancellationToken.None);
                    }
                }
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            this.LogSlashCommandFailed(command.CommandName, ex.Message);
            await command.FollowupAsync($"Error: {ex.Message}", ephemeral: true).ConfigureAwait(false);
        }
#pragma warning restore CA1031
    }

    // ── LoggerMessage source-generated methods ───────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord slash commands registered (voice-id)")]
    private partial void LogSlashCommandsRegistered();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord slash command registration failed: {Reason}")]
    private partial void LogSlashCommandRegistrationFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord slash command {Command} failed: {Reason}")]
    private partial void LogSlashCommandFailed(string command, string reason);
}
