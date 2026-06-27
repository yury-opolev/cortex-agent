using System.Globalization;
using Cortex.Contained.Channels.Discord;
using Cortex.Contained.Channels.Discord.Recording;
using Cortex.Contained.Contracts.Recording;

namespace Cortex.Contained.Bridge.Recording;

/// <summary>
/// Routes the <c>/voice-record</c> Discord slash command to
/// <see cref="IRecordingController"/>. Pure dispatcher — all state lives in
/// the controller. Channel resolution lives in
/// <see cref="VoiceChannelNameResolver"/>.
/// </summary>
public sealed class VoiceRecordSlashCommandHandler
{
    private readonly IRecordingController controller;
    private readonly Func<string?, IReadOnlyList<(string Name, ulong Id)>> voiceChannelsForTenant;

    public VoiceRecordSlashCommandHandler(
        IRecordingController controller,
        Func<string?, IReadOnlyList<(string Name, ulong Id)>> voiceChannelsForTenant)
    {
        this.controller = controller;
        this.voiceChannelsForTenant = voiceChannelsForTenant;
    }

    public async Task<SlashCommandResult?> HandleAsync(SlashCommandRequest req, CancellationToken ct = default)
    {
        if (!req.CommandPath.StartsWith("voice-record/", StringComparison.Ordinal))
        {
            return null;
        }

        var sub = req.CommandPath["voice-record/".Length..];

        // start/stop/list are tenant-scoped. status doesn't need a tenant.
        if ((sub == "start" || sub == "stop" || sub == "list") && string.IsNullOrEmpty(req.TenantId))
        {
            return new SlashCommandResult(
                "Your Discord account isn't paired with a Cortex tenant — pair first, then use /voice-record.",
                Ephemeral: true);
        }

        return sub switch
        {
            "start" => await this.HandleStart(req, ct).ConfigureAwait(false),
            "stop" => await this.HandleStop(req, ct).ConfigureAwait(false),
            "status" => this.HandleStatus(),
            "list" => this.HandleList(req),
            _ => new SlashCommandResult($"Unknown subcommand: `{sub}`", Ephemeral: true),
        };
    }

    private SlashCommandResult HandleList(SlashCommandRequest req)
    {
        var channels = this.voiceChannelsForTenant(req.TenantId);
        if (channels.Count == 0)
        {
            return new SlashCommandResult(
                $"No voice channels configured for tenant `{req.TenantId}`.",
                Ephemeral: true);
        }

        var lines = channels.Select(c =>
        {
            var hereMarker = (req.CurrentVoiceChannelId is { } cur && c.Id == cur)
                ? "  ← you are here"
                : string.Empty;
            var label = c.Id == 0
                ? "`host` (local host voice channel — wake-word / PTT)"
                : $"`{c.Name}` (Discord voice)";
            return $"• {label}{hereMarker}";
        });

        return new SlashCommandResult(
            $"**Voice channels available for /voice-record (tenant `{req.TenantId}`):**\n" + string.Join("\n", lines),
            Ephemeral: true);
    }

    private async Task<SlashCommandResult> HandleStart(SlashCommandRequest req, CancellationToken ct)
    {
        var (channelKey, channelDisplay, errReply) = this.ResolveTarget(req);
        if (errReply is not null)
        {
            return errReply;
        }

        var result = await this.controller
            .StartAsync(channelKey!, label: null, ct, channelDisplay, req.TenantId)
            .ConfigureAwait(false);
        return result switch
        {
            StartResult.Started s => new SlashCommandResult(
                $"Started recording session **{s.Id}** under tenant `{req.TenantId}` on `{s.ChannelKey}`"
                + (channelDisplay is not null ? $" ({channelDisplay})" : string.Empty)
                + $". Cap: {s.CapMs / 60000} min."),
            StartResult.AlreadyActive a => new SlashCommandResult(
                $"Already recording session **{a.ExistingId}** (since {a.SinceUtc.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)})."),
            StartResult.Rejected r => new SlashCommandResult($"Rejected: {r.Reason}", Ephemeral: true),
            _ => new SlashCommandResult("Unexpected result"),
        };
    }

    private async Task<SlashCommandResult> HandleStop(SlashCommandRequest req, CancellationToken ct)
    {
        var (channelKey, _, errReply) = this.ResolveTarget(req);
        if (errReply is not null)
        {
            return errReply;
        }

        var result = await this.controller.StopAsync(channelKey!, StopReason.Manual, ct).ConfigureAwait(false);
        return result switch
        {
            StopResult.Stopped s => new SlashCommandResult(
                $"Stopped session **{s.Id}** — {s.DurationMs / 1000.0:F1}s captured at `{s.WavPath}`."),
            StopResult.NotActive => new SlashCommandResult(
                $"No active recording on `{channelKey}`.", Ephemeral: true),
            _ => new SlashCommandResult("Unexpected result"),
        };
    }

    private SlashCommandResult HandleStatus()
    {
        var active = this.controller.AllActive;
        if (active.Count == 0)
        {
            return new SlashCommandResult("No active recordings.");
        }

        var lines = active.Select(s =>
            $"- `{s.ChannelKey}` → **{s.Id}** since {s.StartUtc.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)}");
        return new SlashCommandResult("Active recordings:\n" + string.Join("\n", lines));
    }

    private (string? ChannelKey, string? ChannelDisplay, SlashCommandResult? Error) ResolveTarget(SlashCommandRequest req)
    {
        var channels = this.voiceChannelsForTenant(req.TenantId);
        var resolved = VoiceChannelNameResolver.Resolve(req.ChannelArg, channels);

        switch (resolved)
        {
            case ResolveResult.Resolved r:
                return (r.ChannelKey, r.Display, null);
            case ResolveResult.FallbackToCurrent:
                // Defensive — Discord's required-parameter validation should
                // reject empty `channel:` before reaching us. If it ever slips
                // through, ask the user to pass `channel:` explicitly.
                return (null, null, new SlashCommandResult(
                    "`channel:` is required. Run `/voice-record list` to see what's available.",
                    Ephemeral: true));
            case ResolveResult.NotFound n:
                return (null, null, new SlashCommandResult(
                    $"Voice channel `{n.Name}` is not configured for tenant `{req.TenantId}`. "
                    + "Run `/voice-record list` to see what's available.",
                    Ephemeral: true));
            case ResolveResult.Ambiguous a:
                return (null, null, new SlashCommandResult(
                    $"Channel name `{a.Name}` is ambiguous — multiple voice channels match.",
                    Ephemeral: true));
            default:
                return (null, null, new SlashCommandResult("Unexpected channel resolution result", Ephemeral: true));
        }
    }
}
