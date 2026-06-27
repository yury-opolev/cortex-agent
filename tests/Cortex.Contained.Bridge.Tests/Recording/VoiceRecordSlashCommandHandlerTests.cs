using Cortex.Contained.Bridge.Recording;
using Cortex.Contained.Channels.Discord;
using Cortex.Contained.Contracts.Recording;

namespace Cortex.Contained.Bridge.Tests.Recording;

public class VoiceRecordSlashCommandHandlerTests
{
    private static readonly IReadOnlyList<(string Name, ulong Id)> EmptyChannels = [];
    private static readonly IReadOnlyList<(string Name, ulong Id)> GuildChannels = new[]
    {
        ("General", 100UL),
        ("Music", 200UL),
    };

    [Fact]
    public async Task Start_WithChannelArg_StartsControllerAndReplies()
    {
        var ctl = Substitute.For<IRecordingController>();
        ctl.StartAsync(
                Arg.Is("discord:42"),
                Arg.Is<string?>(s => s == null),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<string?>())
            .Returns(new StartResult.Started("s-1", "discord:42", DateTimeOffset.UtcNow, 3600000));

        var handler = new VoiceRecordSlashCommandHandler(ctl,
            _ => new[] { ("General", 42UL) });
        var req = new SlashCommandRequest("default", 1, "voice-record/start", ChannelArg: "General");

        var result = await handler.HandleAsync(req);

        Assert.NotNull(result);
        Assert.Contains("Started", result!.Message, StringComparison.Ordinal);
        Assert.Contains("General", result.Message, StringComparison.Ordinal);
        await ctl.Received(1).StartAsync(
            Arg.Is("discord:42"),
            Arg.Is<string?>(s => s == null),
            Arg.Any<CancellationToken>(),
            Arg.Is("General"),
            Arg.Is("default"));
    }

    [Fact]
    public async Task Start_WithChannelArgHost_TargetsHost()
    {
        var ctl = Substitute.For<IRecordingController>();
        ctl.StartAsync(
                Arg.Is(ChannelKey.Host),
                Arg.Is<string?>(s => s == null),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<string?>())
            .Returns(new StartResult.Started("s-2", ChannelKey.Host, DateTimeOffset.UtcNow, 3600000));

        var handler = new VoiceRecordSlashCommandHandler(ctl, _ => EmptyChannels);
        var req = new SlashCommandRequest("default", 1, "voice-record/start",
            CurrentVoiceChannelId: 42, ChannelArg: "host");

        await handler.HandleAsync(req);

        await ctl.Received(1).StartAsync(
            Arg.Is(ChannelKey.Host),
            Arg.Is<string?>(s => s == null),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Is("default"));
    }

    [Fact]
    public async Task Start_WithChannelArgName_LooksUpInGuild()
    {
        var ctl = Substitute.For<IRecordingController>();
        ctl.StartAsync(
                Arg.Is("discord:200"),
                Arg.Is<string?>(s => s == null),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<string?>())
            .Returns(new StartResult.Started("s-3", "discord:200", DateTimeOffset.UtcNow, 3600000));

        var handler = new VoiceRecordSlashCommandHandler(ctl, _ => GuildChannels);
        var req = new SlashCommandRequest("default", 1, "voice-record/start", ChannelArg: "Music");

        await handler.HandleAsync(req);

        await ctl.Received(1).StartAsync(
            Arg.Is("discord:200"),
            Arg.Is<string?>(s => s == null),
            Arg.Any<CancellationToken>(),
            Arg.Is("Music"),
            Arg.Is("default"));
    }

    [Fact]
    public async Task Start_WithoutChannelArg_DefensiveError()
    {
        // Discord rejects the slash command before reaching us when channel:
        // is required-but-missing. If it ever does land in our handler (e.g.
        // tests bypassing the slash-command schema), we should still refuse
        // with a clear message pointing the user at /voice-record list.
        var ctl = Substitute.For<IRecordingController>();
        var handler = new VoiceRecordSlashCommandHandler(ctl, _ => EmptyChannels);
        var req = new SlashCommandRequest("default", 1, "voice-record/start");

        var result = await handler.HandleAsync(req);

        Assert.NotNull(result);
        Assert.Contains("required", result!.Message, StringComparison.OrdinalIgnoreCase);
        await ctl.DidNotReceive().StartAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(),
            Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Start_UnknownChannelName_PointsToList()
    {
        var ctl = Substitute.For<IRecordingController>();
        var handler = new VoiceRecordSlashCommandHandler(ctl, _ => GuildChannels);
        var req = new SlashCommandRequest("default", 1, "voice-record/start", ChannelArg: "Nope");

        var result = await handler.HandleAsync(req);

        Assert.NotNull(result);
        Assert.Contains("not", result!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/voice-record list", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ReturnsConfiguredChannels_WithHereMarker()
    {
        var ctl = Substitute.For<IRecordingController>();
        var handler = new VoiceRecordSlashCommandHandler(ctl,
            tenantId => new[] { ("General", 100UL), ("host", 0UL) });

        var result = await handler.HandleAsync(new SlashCommandRequest(
            TenantId: "default", UserId: 1, CommandPath: "voice-record/list",
            CurrentVoiceChannelId: 100));

        Assert.NotNull(result);
        Assert.Contains("`General`", result!.Message, StringComparison.Ordinal);
        Assert.Contains("you are here", result.Message, StringComparison.Ordinal);
        Assert.Contains("`host`", result.Message, StringComparison.Ordinal);
        Assert.Contains("default", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_NoConfiguredChannels_RepliesEmpty()
    {
        var ctl = Substitute.For<IRecordingController>();
        var handler = new VoiceRecordSlashCommandHandler(ctl, _ => EmptyChannels);

        var result = await handler.HandleAsync(new SlashCommandRequest(
            "default", 1, "voice-record/list"));

        Assert.NotNull(result);
        Assert.Contains("No voice channels configured", result!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_UnpairedUser_Rejected()
    {
        var ctl = Substitute.For<IRecordingController>();
        var handler = new VoiceRecordSlashCommandHandler(ctl, _ => GuildChannels);

        var result = await handler.HandleAsync(new SlashCommandRequest(
            TenantId: null, UserId: 1, CommandPath: "voice-record/list"));

        Assert.NotNull(result);
        Assert.Contains("isn't paired", result!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_DispatchesAndFormats()
    {
        var ctl = Substitute.For<IRecordingController>();
        ctl.StopAsync("discord:42", StopReason.Manual, Arg.Any<CancellationToken>())
            .Returns(new StopResult.Stopped("s-1", @"C:\a.wav", 1234, StopReason.Manual));

        var handler = new VoiceRecordSlashCommandHandler(ctl,
            _ => new[] { ("General", 42UL) });
        var req = new SlashCommandRequest("default", 1, "voice-record/stop", ChannelArg: "General");

        var result = await handler.HandleAsync(req);

        Assert.NotNull(result);
        Assert.Contains("Stopped", result!.Message, StringComparison.Ordinal);
        Assert.Contains("1.2s", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_NotActive_RepliesGracefully()
    {
        var ctl = Substitute.For<IRecordingController>();
        ctl.StopAsync(Arg.Any<string>(), Arg.Any<StopReason>(), Arg.Any<CancellationToken>())
            .Returns(new StopResult.NotActive());

        var handler = new VoiceRecordSlashCommandHandler(ctl,
            _ => new[] { ("General", 42UL) });
        var req = new SlashCommandRequest("default", 1, "voice-record/stop", ChannelArg: "General");

        var result = await handler.HandleAsync(req);

        Assert.NotNull(result);
        Assert.Contains("No active recording", result!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_NoActive_RepliesEmpty()
    {
        var ctl = Substitute.For<IRecordingController>();
        ctl.AllActive.Returns(Array.Empty<ActiveSession>());

        var handler = new VoiceRecordSlashCommandHandler(ctl, _ => EmptyChannels);
        var req = new SlashCommandRequest("default", 1, "voice-record/status");

        var result = await handler.HandleAsync(req);

        Assert.NotNull(result);
        Assert.Contains("No active recordings", result!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_ListsActive()
    {
        var ctl = Substitute.For<IRecordingController>();
        ctl.AllActive.Returns(new ActiveSession[]
        {
            new("demo-20260520-191223", "discord:42", "demo",
                new DateTimeOffset(2026, 5, 20, 19, 12, 23, TimeSpan.Zero), 3600000),
        });

        var handler = new VoiceRecordSlashCommandHandler(ctl, _ => EmptyChannels);
        var result = await handler.HandleAsync(new SlashCommandRequest("default", 1, "voice-record/status"));

        Assert.NotNull(result);
        Assert.Contains("discord:42", result!.Message, StringComparison.Ordinal);
        Assert.Contains("demo-20260520-191223", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_UnpairedUser_NoTenantId_RejectsClearly()
    {
        var ctl = Substitute.For<IRecordingController>();
        var handler = new VoiceRecordSlashCommandHandler(ctl, _ => EmptyChannels);

        // tenantId = null → not paired
        var result = await handler.HandleAsync(new SlashCommandRequest(
            TenantId: null, UserId: 1, CommandPath: "voice-record/start", CurrentVoiceChannelId: 42));

        Assert.NotNull(result);
        Assert.Contains("isn't paired", result!.Message, StringComparison.Ordinal);
        await ctl.DidNotReceive().StartAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(),
            Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task UnknownSubcommand_RepliesUnknown()
    {
        var ctl = Substitute.For<IRecordingController>();
        var handler = new VoiceRecordSlashCommandHandler(ctl, _ => EmptyChannels);

        var result = await handler.HandleAsync(new SlashCommandRequest("default", 1, "voice-record/garbage"));

        Assert.NotNull(result);
        Assert.Contains("Unknown subcommand", result!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonVoiceRecordPath_ReturnsNullToLetOthersHandle()
    {
        var ctl = Substitute.For<IRecordingController>();
        var handler = new VoiceRecordSlashCommandHandler(ctl, _ => EmptyChannels);

        var result = await handler.HandleAsync(new SlashCommandRequest("default", 1, "voice-id/status"));

        Assert.Null(result);
    }
}
