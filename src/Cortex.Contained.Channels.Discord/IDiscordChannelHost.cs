namespace Cortex.Contained.Channels.Discord;

using Cortex.Contained.Contracts.Messages;

/// <summary>
/// Narrow seam that <see cref="DiscordChannel"/> exposes to its extracted handler
/// modules (<see cref="DiscordSlashCommandModule"/> and
/// <see cref="DiscordMessageRouter"/>). It surfaces only the mutable channel
/// state and live callbacks those modules need, keeping the modules decoupled
/// from the rest of the facade. Reads are intentionally "live" — the slash and
/// pairing callbacks are assigned by the Bridge after construction, so modules
/// must read them through this interface rather than capturing them.
/// </summary>
internal interface IDiscordChannelHost
{
    // ── Inbound routing (DiscordMessageRouter) ───────────────────────

    /// <summary>Raise the channel's <c>MessageReceived</c> event, or no-op when unsubscribed.</summary>
    Task RaiseMessageReceivedAsync(InboundMessage message);

    /// <summary>Record the snowflake of the DM channel a message arrived on, for outbound routing.</summary>
    void SetDmChannelSnowflake(ulong snowflake);

    /// <summary>Record the user id behind an inbound DM, so outbound DMs can be opened on demand.</summary>
    void SetDmRecipientUserId(ulong userId);

    /// <summary>Mark a channel as having used voice input so its reply is also delivered as audio.</summary>
    void MarkVoiceConversation(string channelId);

    // ── Slash-command callbacks (DiscordSlashCommandModule) ──────────

    /// <summary>Resolves a Discord user id to a tenant id; null when unpaired or unwired.</summary>
    Func<ulong, string?>? TenantResolver { get; }

    /// <summary>Bridge → Agent slash-command dispatch; null when unwired.</summary>
    Func<SlashCommandRequest, Task<SlashCommandResult?>>? SlashCommandHandler { get; }

    /// <summary>Tenant-scoped voice-channel suggestions for <c>/voice-record</c> autocomplete; null falls back to guild-wide.</summary>
    Func<string?, IReadOnlyList<(string Name, ulong Id)>>? AvailableVoiceChannelsResolver { get; }

    /// <summary>Enrollment progress notifier; null skips progress messages.</summary>
    IEnrollmentProgressNotifier? EnrollmentProgressNotifier { get; }

    /// <summary>Look up the voice handler for a tenant, or null when none is active.</summary>
    DiscordVoiceHandler? TryGetVoiceHandler(string tenantId);
}
