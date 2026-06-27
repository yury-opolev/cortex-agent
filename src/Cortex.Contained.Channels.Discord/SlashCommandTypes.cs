namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// A slash command invocation surfaced from <see cref="DiscordChannel"/> to
/// the Bridge. The channel handles interaction lifecycle (defer, reply); the
/// Bridge owns the side-effect (typically a SignalR call to Agent.Host).
/// </summary>
/// <param name="TenantId">Tenant the invoking user is linked to. Null when the user is not paired.</param>
/// <param name="UserId">Discord user id that invoked the command.</param>
/// <param name="CommandPath">Slash-separated path, e.g. <c>voice-id/forget</c>. Subcommand groups join with '/'.</param>
/// <param name="CurrentVoiceChannelId">
/// Snowflake of the Discord voice channel the invoking user is currently in,
/// or <see langword="null"/> when the user is not in any voice channel.
/// Populated by <see cref="DiscordChannel"/> from the user's gateway voice state.
/// Used by the Bridge to decide whether to ring the user before enrollment.
/// </param>
public sealed record SlashCommandRequest(
    string? TenantId,
    ulong UserId,
    string CommandPath,
    ulong? CurrentVoiceChannelId = null,
    string? ChannelArg = null,
    string? SourceChannelKind = null);

/// <summary>
/// Reply the Bridge wants to send back to the user.
/// </summary>
/// <param name="Message">Markdown reply text.</param>
/// <param name="Ephemeral">When true the reply is only visible to the invoking user.</param>
/// <param name="RingVoice">
/// When <see langword="true"/>, <see cref="DiscordChannel"/> should ring the tenant's
/// configured voice channel after sending this reply (user was not already present).
/// </param>
/// <param name="StartWizard">
/// When <see langword="true"/>, <see cref="DiscordChannel"/> should start the enrollment
/// wizard immediately (the user is already in the configured voice channel).
/// </param>
public sealed record SlashCommandResult(string Message, bool Ephemeral = true, bool RingVoice = false, bool StartWizard = false);
