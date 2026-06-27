using Cortex.Contained.Bridge.Setup;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Setup;

/// <summary>
/// Guards the 2026-05-24 regression: per-language voice config
/// (speech.tts.languages) was dropped from cortex.yml on save whenever STT/TTS
/// engine settings were at defaults, because the persistence guard didn't
/// consider configured languages. The whole speech section was skipped, so the
/// config only lived in memory and vanished on restart.
/// </summary>
public class SpeechYamlPolicyTests
{
    [Fact]
    public void AllDefaults_NoLanguages_DoesNotWriteSection()
    {
        var speech = new SpeechConfig();
        Assert.False(SpeechYamlPolicy.ShouldWriteSpeechSection(speech));
    }

    [Fact]
    public void DefaultsButLanguagesConfigured_WritesSection()
    {
        // The exact regression: engine settings default, but the user configured
        // per-language voices. Must persist.
        var speech = new SpeechConfig();
        speech.Tts.Languages["da"] = new LanguageTtsConfig
        {
            MaleVoice = "roest-da:nic",
            FemaleVoice = "roest-da:mic",
        };

        Assert.True(SpeechYamlPolicy.ShouldWriteSpeechSection(speech));
    }

    [Fact]
    public void NonDefaultFallbackLanguage_WritesSection()
    {
        var speech = new SpeechConfig();
        speech.Tts.DefaultLanguage = "da";
        Assert.True(SpeechYamlPolicy.ShouldWriteSpeechSection(speech));
    }

    [Fact]
    public void NonDefaultSttLanguage_WritesSection()
    {
        var speech = new SpeechConfig();
        speech.Stt.Language = "auto";
        Assert.True(SpeechYamlPolicy.ShouldWriteSpeechSection(speech));
    }

    [Fact]
    public void NonDefaultKokoroVoice_WritesSection()
    {
        var speech = new SpeechConfig();
        speech.Tts.KokoroVoice = "am_adam";
        Assert.True(SpeechYamlPolicy.ShouldWriteSpeechSection(speech));
    }
}
