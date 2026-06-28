using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

/// <summary>
/// Pins the predicate that recognises a silent Discord audio-transport death
/// from a Discord.Net diagnostic log line. The 2026-06-28 outage was an
/// "Audio #2: A task was canceled" that raised no Disconnected event — this
/// classifier is what lets the watchdog notice it.
/// </summary>
public class AudioDeathLogClassifierTests
{
    [Theory]
    [InlineData("Audio #4", "A task was canceled.")]
    [InlineData("Audio #2", "A task was canceled")]
    [InlineData("Audio", "A task was canceled.")]
    [InlineData("Voice", "A task was cancelled.")] // British spelling variant
    public void IsAudioTransportDeath_AudioOrVoiceTaskCanceled_True(string source, string message)
    {
        Assert.True(AudioDeathLogClassifier.IsAudioTransportDeath(source, message));
    }

    [Theory]
    [InlineData("Gateway", "A task was canceled.")] // gateway has its own reconnect path
    [InlineData("Audio #4", "Connecting")]
    [InlineData("Audio", "Malformed Frame")]        // benign DAVE noise
    [InlineData("Audio", "Unknown SSRC 12345")]     // benign DAVE noise
    [InlineData("Rest", "A task was canceled.")]
    public void IsAudioTransportDeath_UnrelatedOrBenign_False(string source, string message)
    {
        Assert.False(AudioDeathLogClassifier.IsAudioTransportDeath(source, message));
    }

    [Theory]
    [InlineData(null, "A task was canceled.")]
    [InlineData("Audio #4", null)]
    [InlineData("Audio #4", "")]
    [InlineData(null, null)]
    public void IsAudioTransportDeath_NullOrEmpty_False(string? source, string? message)
    {
        Assert.False(AudioDeathLogClassifier.IsAudioTransportDeath(source, message));
    }
}
