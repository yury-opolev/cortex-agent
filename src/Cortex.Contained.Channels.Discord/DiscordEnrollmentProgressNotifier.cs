namespace Cortex.Contained.Channels.Discord;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

/// <summary>
/// Posts enrollment progress messages to the Discord text channel where
/// <c>/voice-id enroll</c> was originally issued.
/// </summary>
public sealed partial class DiscordEnrollmentProgressNotifier : IEnrollmentProgressNotifier
{
    private readonly IDiscordChannelSender sender;
    private readonly ILogger<DiscordEnrollmentProgressNotifier> logger;
    private readonly ConcurrentDictionary<string, ulong> tenantChannel = new(StringComparer.Ordinal);

    /// <summary>
    /// Production constructor. Wraps <paramref name="discordChannel"/> with the
    /// <see cref="DiscordChannelSender"/> that routes through the live gateway.
    /// </summary>
    public DiscordEnrollmentProgressNotifier(
        DiscordChannel discordChannel,
        ILogger<DiscordEnrollmentProgressNotifier> logger)
        : this(new DiscordChannelSender(discordChannel), logger)
    {
    }

    /// <summary>
    /// Seam constructor for unit tests. Injects a custom <see cref="IDiscordChannelSender"/>
    /// without requiring a live Discord gateway.
    /// </summary>
    internal DiscordEnrollmentProgressNotifier(
        IDiscordChannelSender sender,
        ILogger<DiscordEnrollmentProgressNotifier> logger)
    {
        this.sender = sender;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public void TrackInteractionChannel(string tenantId, ulong textChannelId)
        => this.tenantChannel[tenantId] = textChannelId;

    /// <inheritdoc/>
    public void Untrack(string tenantId) => this.tenantChannel.TryRemove(tenantId, out _);

    /// <inheritdoc/>
    public async ValueTask ReportAsync(string tenantId, string stateName, int captured, int required)
    {
        if (!this.tenantChannel.TryGetValue(tenantId, out var channelId))
        {
            return;
        }

        var text = stateName switch
        {
            "Enrolling" when captured == 0 => "Voice-ID enrollment started. Speak three sentences in the voice channel.",
            "Enrolling" => $"Captured {captured}/{required} samples.",
            "Confirming" => "Capture complete. Now say two more sentences to confirm.",
            "Enrolled" => "Voice enrolled. Only your voice will pass through from now on.",
            "Unknown" => "Voice-ID enrollment cancelled or timed out.",
            "Declined" => "Voice-ID enrollment declined.",
            "PendingReenroll" => "Re-enrollment requested. Run `/voice-id enroll` to restart.",
            _ => $"Voice-ID state: {stateName} ({captured}/{required}).",
        };

        try
        {
            await this.sender.TrySendAsync(channelId, text).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception ex) { this.LogPostFailed(tenantId, channelId, ex.Message); }
#pragma warning restore CA1031

        if (stateName is "Enrolled" or "Unknown" or "Declined")
        {
            this.Untrack(tenantId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "enrollment progress post failed tenant={TenantId} channel={ChannelId} error={Error}")]
    private partial void LogPostFailed(string tenantId, ulong channelId, string error);
}
