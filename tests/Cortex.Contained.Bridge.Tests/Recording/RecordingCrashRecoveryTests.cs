using System.Text;
using Cortex.Contained.Bridge.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Recording;

public sealed class RecordingCrashRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "cortex-rec-" + Guid.NewGuid().ToString("N"));

    public RecordingCrashRecoveryTests() => Directory.CreateDirectory(this.root);

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

    [Fact]
    public void Sweep_FinalisesTornArtifacts()
    {
        var id = "torn-20260520-191223";
        var dir = Path.Combine(this.root, id);
        Directory.CreateDirectory(dir);

        // Torn WAV: zero-size header + 1600 bytes of PCM appended.
        using (var fs = new FileStream(Path.Combine(dir, "session.wav"), FileMode.Create, FileAccess.Write))
        {
            var hdr = new byte[44];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(hdr, 0);
            BitConverter.GetBytes(36).CopyTo(hdr, 4);
            Encoding.ASCII.GetBytes("WAVE").CopyTo(hdr, 8);
            Encoding.ASCII.GetBytes("fmt ").CopyTo(hdr, 12);
            BitConverter.GetBytes(16).CopyTo(hdr, 16);
            BitConverter.GetBytes((short)1).CopyTo(hdr, 20);
            BitConverter.GetBytes((short)1).CopyTo(hdr, 22);
            BitConverter.GetBytes(16000).CopyTo(hdr, 24);
            BitConverter.GetBytes(32000).CopyTo(hdr, 28);
            BitConverter.GetBytes((short)2).CopyTo(hdr, 32);
            BitConverter.GetBytes((short)16).CopyTo(hdr, 34);
            Encoding.ASCII.GetBytes("data").CopyTo(hdr, 36);
            // hdr[40..44] left zero — the torn data-size.
            fs.Write(hdr);
            fs.Write(new byte[1600]);
        }

        // events.jsonl with one prior line.
        File.WriteAllText(
            Path.Combine(dir, "events.jsonl"),
            "{\"t\":0,\"wallUtc\":\"2026-05-20T19:12:23Z\",\"type\":\"session_start\"}\n");

        // Manifest with EndUtc null = the torn marker.
        File.WriteAllText(
            Path.Combine(dir, "manifest.json"),
            "{\"id\":\"" + id + "\",\"label\":\"x\",\"channelKey\":\"discord:1\","
            + "\"startUtc\":\"2026-05-20T19:12:23+00:00\",\"capMs\":3600000,"
            + "\"capReached\":false,\"crashed\":false,\"groundTruthTurns\":[]}");

        var fixedCount = RecordingCrashRecovery.SweepAndFinalise(this.root, NullLogger.Instance);
        Assert.Equal(1, fixedCount);

        var m = RecordingManifest.FromJson(File.ReadAllText(Path.Combine(dir, "manifest.json")));
        Assert.True(m.Crashed);
        Assert.Equal("crash", m.StopReason);
        Assert.NotNull(m.EndUtc);

        var wavBytes = File.ReadAllBytes(Path.Combine(dir, "session.wav"));
        Assert.Equal(1600, BitConverter.ToInt32(wavBytes, 40));

        var events = File.ReadAllLines(Path.Combine(dir, "events.jsonl"));
        Assert.Contains(events, l => l.Contains("\"reason\":\"crash\""));
    }

    [Fact]
    public void Sweep_IgnoresAlreadyFinalisedSessions()
    {
        var id = "done-20260520-191223";
        var dir = Path.Combine(this.root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "manifest.json"),
            "{\"id\":\"" + id + "\",\"label\":\"x\",\"channelKey\":\"discord:1\","
            + "\"startUtc\":\"2026-05-20T19:12:23+00:00\","
            + "\"endUtc\":\"2026-05-20T19:22:23+00:00\","
            + "\"durationMs\":600000,\"capMs\":3600000,"
            + "\"capReached\":false,\"crashed\":false,"
            + "\"stopReason\":\"manual\",\"groundTruthTurns\":[]}");

        Assert.Equal(0, RecordingCrashRecovery.SweepAndFinalise(this.root, NullLogger.Instance));
    }

    [Fact]
    public void Sweep_OnMissingRoot_IsNoOp()
    {
        var noSuchRoot = Path.Combine(Path.GetTempPath(), "cortex-rec-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, RecordingCrashRecovery.SweepAndFinalise(noSuchRoot, NullLogger.Instance));
    }
}
