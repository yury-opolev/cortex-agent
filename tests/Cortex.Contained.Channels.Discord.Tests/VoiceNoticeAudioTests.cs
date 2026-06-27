using Cortex.Contained.Channels.Discord;
using Cortex.Contained.Speech;

namespace Cortex.Contained.Channels.Discord.Tests;

/// <summary>
/// Coverage for <see cref="VoiceNoticeAudio"/>: the embedded pre-baked "trouble
/// speaking" notices (male + female) load, are non-empty and 16-bit aligned so
/// they frame cleanly through the voice-out path, and <c>TroubleSpeaking</c>
/// selects the clip matching the agent's voice gender.
/// </summary>
public class VoiceNoticeAudioTests
{
    [Fact]
    public void TroubleSpeakingClips_AreLoadedAndSampleAligned()
    {
        var female = VoiceNoticeAudio.TroubleSpeakingFemalePcm48kMono;
        var male = VoiceNoticeAudio.TroubleSpeakingMalePcm48kMono;

        Assert.NotNull(female);
        Assert.NotNull(male);
        Assert.True(female.Length > 0, "embedded female notice PCM should not be empty");
        Assert.True(male.Length > 0, "embedded male notice PCM should not be empty");
        Assert.True(female.Length % 2 == 0, "16-bit PCM length must be even (female)");
        Assert.True(male.Length % 2 == 0, "16-bit PCM length must be even (male)");
        Assert.True(female.Length > 100000, $"female notice PCM unexpectedly small: {female.Length} bytes");
        Assert.True(male.Length > 100000, $"male notice PCM unexpectedly small: {male.Length} bytes");

        // The two clips are distinct assets (different spoken voices, hence
        // different lengths: 273600 female vs 285600 male).
        Assert.True(male.Length != female.Length, "male and female clips must be distinct assets");
    }

    [Fact]
    public void TroubleSpeaking_SelectsClipByGender()
    {
        Assert.Same(VoiceNoticeAudio.TroubleSpeakingMalePcm48kMono, VoiceNoticeAudio.TroubleSpeaking(VoiceGender.Male));
        Assert.Same(VoiceNoticeAudio.TroubleSpeakingFemalePcm48kMono, VoiceNoticeAudio.TroubleSpeaking(VoiceGender.Female));
    }

    [Fact]
    public void TroubleSpeakingClips_AreCachedAcrossCalls()
    {
        Assert.Same(VoiceNoticeAudio.TroubleSpeakingFemalePcm48kMono, VoiceNoticeAudio.TroubleSpeakingFemalePcm48kMono);
        Assert.Same(VoiceNoticeAudio.TroubleSpeakingMalePcm48kMono, VoiceNoticeAudio.TroubleSpeakingMalePcm48kMono);
    }
}
