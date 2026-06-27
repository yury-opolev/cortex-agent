using Cortex.Contained.Bridge.Recording;
using Cortex.Contained.Contracts.Recording;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Recording;

public sealed class RecordingControllerCapTests : IDisposable
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

    private RecordingController NewControllerWithCap(long capMs) =>
        new(NullLogger<RecordingController>.Instance, this.clock, () => this.root, capMs);

    [Fact]
    public async Task At90Percent_CapWarning_IsEmitted()
    {
        // capMs = 1000 → 90% threshold at 900 ms.
        var c = this.NewControllerWithCap(1000);
        await ((IHostedService)c).StartAsync(CancellationToken.None);

        var s = (StartResult.Started)await c.StartAsync("discord:1", "x", CancellationToken.None, channelDisplay: "General", tenantId: "default");

        // Step past the 90% boundary; FakeTimeProvider fires the timer callback synchronously.
        this.clock.Advance(TimeSpan.FromMilliseconds(950));

        // Session still active (not yet at cap).
        Assert.NotNull(c.GetActive("discord:1"));

        // Force events to flush by stopping; then read events.
        await c.StopAsync("discord:1", StopReason.Manual, CancellationToken.None);
        var events = File.ReadAllLines(Path.Combine(this.root, "default", "General", s.Id, "events.jsonl"));
        Assert.Contains(events, l => l.Contains("\"type\":\"cap_warning\""));
    }

    [Fact]
    public async Task AtCap_AutoStop_FinalisesWithReasonCap()
    {
        var c = this.NewControllerWithCap(1000);
        await ((IHostedService)c).StartAsync(CancellationToken.None);

        var s = (StartResult.Started)await c.StartAsync("discord:1", "x", CancellationToken.None, channelDisplay: "General", tenantId: "default");

        this.clock.Advance(TimeSpan.FromMilliseconds(1100));

        // The cap auto-stop runs synchronously inside the FakeTimeProvider tick.
        Assert.Null(c.GetActive("discord:1"));

        var manifest = RecordingManifest.FromJson(
            File.ReadAllText(Path.Combine(this.root, "default", "General", s.Id, "manifest.json")));
        Assert.True(manifest.CapReached);
        Assert.Equal("cap", manifest.StopReason);

        var events = File.ReadAllLines(Path.Combine(this.root, "default", "General", s.Id, "events.jsonl"));
        Assert.Contains(events, l => l.Contains("\"reason\":\"cap\""));
    }

    [Fact]
    public async Task CapWarning_FiresOnlyOnce()
    {
        var c = this.NewControllerWithCap(1000);
        await ((IHostedService)c).StartAsync(CancellationToken.None);

        var s = (StartResult.Started)await c.StartAsync("discord:1", "x", CancellationToken.None, channelDisplay: "General", tenantId: "default");

        this.clock.Advance(TimeSpan.FromMilliseconds(920));   // past 90%
        this.clock.Advance(TimeSpan.FromMilliseconds(60));    // still under cap, another tick

        await c.StopAsync("discord:1", StopReason.Manual, CancellationToken.None);

        var warnings = File.ReadAllLines(Path.Combine(this.root, "default", "General", s.Id, "events.jsonl"))
            .Count(l => l.Contains("\"type\":\"cap_warning\""));
        Assert.Equal(1, warnings);
    }
}
