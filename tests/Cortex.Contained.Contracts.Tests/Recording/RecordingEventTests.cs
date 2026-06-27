using System.Text.Json;
using Cortex.Contained.Contracts.Recording;

namespace Cortex.Contained.Contracts.Tests.Recording;

public class RecordingEventTests
{
    [Fact]
    public void SessionStart_Serialisation_HasExpectedFields()
    {
        var e = RecordingEvent.SessionStart(0, "2026-05-20T19:12:23Z", "discord:1", "demo", 3600000);
        var root = JsonDocument.Parse(e.ToJsonLine()).RootElement;

        Assert.Equal(0, root.GetProperty("t").GetInt64());
        Assert.Equal("2026-05-20T19:12:23Z", root.GetProperty("wallUtc").GetString());
        Assert.Equal("session_start", root.GetProperty("type").GetString());
        Assert.Equal("discord:1", root.GetProperty("channelKey").GetString());
        Assert.Equal("demo", root.GetProperty("label").GetString());
        Assert.Equal(3600000, root.GetProperty("capMs").GetInt64());
    }

    [Fact]
    public void AudioStart_HasOnlyEnvelope()
    {
        var e = RecordingEvent.AudioStart(42, "x");
        var root = JsonDocument.Parse(e.ToJsonLine()).RootElement;

        Assert.Equal("audio_start", root.GetProperty("type").GetString());
        Assert.Equal(42, root.GetProperty("t").GetInt64());
    }

    [Fact]
    public void Commit_HasOffsetsAndPEou()
    {
        var e = RecordingEvent.Commit(
            t: 1234, wallUtc: "x",
            silenceMs: 480, pEou: 0.72, reason: "silence",
            utteranceId: "u1", text: "hi",
            audioStartMs: 100, audioEndMs: 1200);
        var root = JsonDocument.Parse(e.ToJsonLine()).RootElement;

        Assert.Equal("commit", root.GetProperty("type").GetString());
        Assert.Equal(480, root.GetProperty("silenceMs").GetDouble());
        Assert.Equal(0.72, root.GetProperty("pEou").GetDouble(), 3);
        Assert.Equal("silence", root.GetProperty("reason").GetString());
        Assert.Equal("u1", root.GetProperty("utteranceId").GetString());
        Assert.Equal("hi", root.GetProperty("text").GetString());
        Assert.Equal(100, root.GetProperty("audioOffsetMs").GetProperty("start").GetInt64());
        Assert.Equal(1200, root.GetProperty("audioOffsetMs").GetProperty("end").GetInt64());
    }

    [Fact]
    public void CapWarning_HasElapsedAndCap()
    {
        var e = RecordingEvent.CapWarning(0, "x", elapsedMs: 54_000, capMs: 60_000);
        var root = JsonDocument.Parse(e.ToJsonLine()).RootElement;

        Assert.Equal("cap_warning", root.GetProperty("type").GetString());
        Assert.Equal(54_000, root.GetProperty("elapsedMs").GetInt64());
        Assert.Equal(60_000, root.GetProperty("capMs").GetInt64());
    }

    [Theory]
    [InlineData(StopReason.Manual, "manual")]
    [InlineData(StopReason.Cap, "cap")]
    [InlineData(StopReason.Shutdown, "shutdown")]
    [InlineData(StopReason.Crash, "crash")]
    public void AutoStop_Reason_IsLowercaseString(StopReason reason, string expected)
    {
        var e = RecordingEvent.AutoStop(0, "x", reason);
        Assert.Equal(expected,
            JsonDocument.Parse(e.ToJsonLine()).RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void ToJsonLine_DoesNotEndWithNewline()
    {
        var e = RecordingEvent.AudioStart(0, "x");
        Assert.DoesNotContain('\n', e.ToJsonLine());
    }
}
