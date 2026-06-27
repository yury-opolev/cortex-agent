using Cortex.Contained.Speech;

namespace Cortex.Contained.Speech.Tests;

public class WhisperModelPathResolverTests
{
    [Fact]
    public void ExplicitConfigPath_Wins()
    {
        var p = WhisperModelPathResolver.Resolve(
            configured: "C:/models/custom.bin",
            voiceSetting: "C:/models/other.bin",
            legacyConfig: null,
            defaultModelsDir: "C:/def");
        Assert.Equal("C:/models/custom.bin", p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankConfig_FallsThrough(string? blank)
    {
        var p = WhisperModelPathResolver.Resolve(
            configured: blank, voiceSetting: blank, legacyConfig: blank,
            defaultModelsDir: "C:/def");
        Assert.Equal(Path.Combine("C:/def", "ggml-large-v3-turbo-q8_0.bin"), p);
    }

    [Fact]
    public void VoiceSetting_UsedWhenConfigBlank()
    {
        var p = WhisperModelPathResolver.Resolve(
            configured: null, voiceSetting: "C:/v/x.bin", legacyConfig: null,
            defaultModelsDir: "C:/def");
        Assert.Equal("C:/v/x.bin", p);
    }

    [Fact]
    public void DefaultIsTurboQ8()
    {
        var p = WhisperModelPathResolver.Resolve(null, null, null, "C:/def");
        Assert.EndsWith("ggml-large-v3-turbo-q8_0.bin", p);
    }
}
