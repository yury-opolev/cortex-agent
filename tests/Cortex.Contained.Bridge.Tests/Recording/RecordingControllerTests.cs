using Cortex.Contained.Bridge.Recording;
using Cortex.Contained.Contracts.Recording;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Recording;

public sealed class RecordingControllerTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "cortex-rec-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 5, 20, 19, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.root, true);
        }
        catch
        {
            // Best-effort.
        }

        GC.SuppressFinalize(this);
    }

    private RecordingController NewController(long capMs = RecordingController.DefaultCapMs) =>
        new(NullLogger<RecordingController>.Instance, this.clock, () => this.root, capMs);

    [Fact]
    public async Task Start_Stop_HappyPath_WritesAllArtifacts()
    {
        var c = this.NewController();
        var start = await c.StartAsync("discord:1", "demo", CancellationToken.None,
            channelDisplay: "General", tenantId: "default");
        var s = Assert.IsType<StartResult.Started>(start);

        c.RecordCommittedUtterance("discord:1", new byte[3200], "u1", "hello", "test");

        var stop = await c.StopAsync("discord:1", StopReason.Manual, CancellationToken.None);
        var done = Assert.IsType<StopResult.Stopped>(stop);

        Assert.Equal(s.Id, done.Id);
        Assert.StartsWith("demo-", s.Id, StringComparison.Ordinal);
        var sessionDir = Path.Combine(this.root, "default", "General", s.Id);
        Assert.True(File.Exists(Path.Combine(sessionDir, "session.wav")));
        Assert.True(File.Exists(Path.Combine(sessionDir, "events.jsonl")));
        Assert.True(File.Exists(Path.Combine(sessionDir, "manifest.json")));
        // sessionElapsedMs == 0 at the Record (fake clock didn't advance), so
        // there's no silence padding; WAV = 44-byte header + 3200 bytes of PCM.
        Assert.Equal(44 + 3200, new FileInfo(Path.Combine(sessionDir, "session.wav")).Length);

        var manifest = RecordingManifest.FromJson(File.ReadAllText(Path.Combine(sessionDir, "manifest.json")));
        Assert.NotNull(manifest.EndUtc);
        Assert.Equal("manual", manifest.StopReason);
        Assert.False(manifest.CapReached);

        var events = File.ReadAllLines(Path.Combine(sessionDir, "events.jsonl"));
        Assert.Contains(events, l => l.Contains("\"type\":\"session_start\"", StringComparison.Ordinal));
        Assert.Contains(events, l => l.Contains("\"type\":\"audio_start\"", StringComparison.Ordinal));
        Assert.Contains(events, l => l.Contains("\"type\":\"commit\"", StringComparison.Ordinal));
        Assert.Contains(events, l => l.Contains("\"utteranceId\":\"u1\"", StringComparison.Ordinal));
        Assert.Contains(events, l => l.Contains("\"type\":\"auto_stop\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordCommittedUtterance_PadsInterUtteranceSilenceToSessionTimeline()
    {
        var c = this.NewController();
        var s = (StartResult.Started)await c.StartAsync("discord:1", null, CancellationToken.None,
            channelDisplay: "General", tenantId: "default");

        c.RecordCommittedUtterance("discord:1", new byte[1000], "u1", "first", "test");
        this.clock.Advance(TimeSpan.FromSeconds(1));
        c.RecordCommittedUtterance("discord:1", new byte[2000], "u2", "second", "test");
        await c.StopAsync("discord:1", StopReason.Manual, CancellationToken.None);

        // 1000 bytes (first utterance) + ~31000 silence pad bytes (sessionElapsed
        // 1000 ms minus current wavDuration 31 ms = 969 ms ≈ 31008 bytes) +
        // 2000 bytes (second utterance) ≈ 34008 bytes of data.
        var wavBytes = new FileInfo(Path.Combine(this.root, "default", "General", s.Id, "session.wav")).Length;
        Assert.InRange(wavBytes, 44 + 33000, 44 + 35000);
    }

    [Fact]
    public async Task Start_DoubleStartSameChannel_ReturnsAlreadyActive()
    {
        var c = this.NewController();
        var first = (StartResult.Started)await c.StartAsync("discord:1", null, CancellationToken.None);
        var second = await c.StartAsync("discord:1", null, CancellationToken.None);

        var already = Assert.IsType<StartResult.AlreadyActive>(second);
        Assert.Equal(first.Id, already.ExistingId);

        await c.StopAsync("discord:1", StopReason.Manual, CancellationToken.None);
    }

    [Fact]
    public async Task Stop_NoActive_ReturnsNotActive()
    {
        var c = this.NewController();
        Assert.IsType<StopResult.NotActive>(
            await c.StopAsync("discord:1", StopReason.Manual, CancellationToken.None));
    }

    [Fact]
    public async Task TwoChannels_AreIsolated()
    {
        var c = this.NewController();
        var a = (StartResult.Started)await c.StartAsync("discord:1", null, CancellationToken.None,
            channelDisplay: "General", tenantId: "default");
        var b = (StartResult.Started)await c.StartAsync(ChannelKey.Host, null, CancellationToken.None,
            tenantId: "default");

        c.RecordCommittedUtterance("discord:1", new byte[1600], "u1", "a", "test");
        c.RecordCommittedUtterance(ChannelKey.Host, new byte[3200], "u2", "b", "test");

        await c.StopAsync("discord:1", StopReason.Manual, CancellationToken.None);
        Assert.NotNull(c.GetActive(ChannelKey.Host));
        Assert.Null(c.GetActive("discord:1"));
        Assert.Equal(44 + 1600,
            new FileInfo(Path.Combine(this.root, "default", "General", a.Id, "session.wav")).Length);

        await c.StopAsync(ChannelKey.Host, StopReason.Manual, CancellationToken.None);
        Assert.Equal(44 + 3200,
            new FileInfo(Path.Combine(this.root, "default", "host", b.Id, "session.wav")).Length);
    }

    [Fact]
    public async Task Start_InvalidChannelKey_ReturnsRejected()
    {
        var c = this.NewController();
        Assert.IsType<StartResult.Rejected>(
            await c.StartAsync("garbage", null, CancellationToken.None));
    }

    [Fact]
    public void RecordCommittedUtterance_WhenNoSession_IsNoOp()
    {
        var c = this.NewController();
        c.RecordCommittedUtterance("discord:99", new byte[1600], "u1", "x", "test");
        Assert.Empty(c.AllActive);
    }

    [Fact]
    public async Task RecordCommittedUtterance_FirstCommit_EmitsAudioStartOnce()
    {
        var c = this.NewController();
        var start = (StartResult.Started)await c.StartAsync("discord:1", null, CancellationToken.None,
            channelDisplay: "General", tenantId: "default");

        c.RecordCommittedUtterance("discord:1", new byte[1600], "u1", "a", "test");
        c.RecordCommittedUtterance("discord:1", new byte[1600], "u2", "b", "test");

        await c.StopAsync("discord:1", StopReason.Manual, CancellationToken.None);
        var events = File.ReadAllLines(
            Path.Combine(this.root, "default", "General", start.Id, "events.jsonl"));
        var audioStarts = events.Count(l => l.Contains("\"type\":\"audio_start\"", StringComparison.Ordinal));
        Assert.Equal(1, audioStarts);
    }

    [Fact]
    public async Task HostedShutdown_FinalisesAllActive_WithReasonShutdown()
    {
        var c = this.NewController();
        await ((IHostedService)c).StartAsync(CancellationToken.None);

        var a = (StartResult.Started)await c.StartAsync("discord:1", null, CancellationToken.None,
            channelDisplay: "General", tenantId: "default");
        var b = (StartResult.Started)await c.StartAsync(ChannelKey.Host, null, CancellationToken.None,
            tenantId: "default");

        await ((IHostedService)c).StopAsync(CancellationToken.None);
        Assert.Empty(c.AllActive);
        Assert.Equal("shutdown",
            RecordingManifest.FromJson(
                File.ReadAllText(Path.Combine(this.root, "default", "General", a.Id, "manifest.json"))).StopReason);
        Assert.Equal("shutdown",
            RecordingManifest.FromJson(
                File.ReadAllText(Path.Combine(this.root, "default", "host", b.Id, "manifest.json"))).StopReason);
    }
}
