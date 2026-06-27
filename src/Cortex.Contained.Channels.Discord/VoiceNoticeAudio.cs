using System.Reflection;
using Cortex.Contained.Speech;

namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Pre-baked voice notices played as a last-resort safety tier when live TTS
/// synthesis fails entirely, so the user never hears dead air. The PCM is loaded
/// once from an embedded resource and cached. A male and a female clip are
/// available so the notice matches the agent's configured voice gender.
/// </summary>
internal static class VoiceNoticeAudio
{
    private const string TroubleFemaleResourceSuffix = "tts-notice-trouble-female.pcm";
    private const string TroubleMaleResourceSuffix = "tts-notice-trouble-male.pcm";

    private static readonly Lazy<byte[]> troubleFemalePcm =
        new(() => LoadEmbedded(TroubleFemaleResourceSuffix), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<byte[]> troubleMalePcm =
        new(() => LoadEmbedded(TroubleMaleResourceSuffix), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Female-voice 48 kHz mono signed 16-bit little-endian PCM of "Sorry, having
    /// trouble speaking right now." Empty array if the embedded resource cannot be
    /// found (never throws).
    /// </summary>
    public static byte[] TroubleSpeakingFemalePcm48kMono => troubleFemalePcm.Value;

    /// <summary>
    /// Male-voice 48 kHz mono signed 16-bit little-endian PCM of "Sorry, having
    /// trouble speaking right now." Empty array if the embedded resource cannot be
    /// found (never throws).
    /// </summary>
    public static byte[] TroubleSpeakingMalePcm48kMono => troubleMalePcm.Value;

    /// <summary>
    /// Returns the "trouble speaking" notice PCM matching <paramref name="gender"/>:
    /// the male clip for <see cref="VoiceGender.Male"/>, otherwise the female clip.
    /// </summary>
    /// <param name="gender">The agent's configured voice gender.</param>
    public static byte[] TroubleSpeaking(VoiceGender gender) =>
        gender == VoiceGender.Male ? TroubleSpeakingMalePcm48kMono : TroubleSpeakingFemalePcm48kMono;

    private static byte[] LoadEmbedded(string resourceSuffix)
    {
        var assembly = typeof(VoiceNoticeAudio).Assembly;
        var name = Array.Find(
            assembly.GetManifestResourceNames(),
            n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return [];
        }

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return [];
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
