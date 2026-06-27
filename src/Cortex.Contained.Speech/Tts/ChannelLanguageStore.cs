namespace Cortex.Contained.Speech.Tts;

using System.Collections.Concurrent;

/// <summary>
/// Per-channel sticky "current language" that drives TTS voice selection.
/// Initialised to a default language; updated from a detection result via
/// <see cref="LanguageSwitchPolicy"/>. Thread-safe; in-memory only — resets on
/// process restart, which is fine because the first long message will
/// re-converge to the correct language.
/// </summary>
public sealed class ChannelLanguageStore
{
    private readonly string defaultLanguage;
    private readonly ConcurrentDictionary<string, string> currentByChannel = new(StringComparer.Ordinal);

    /// <summary>Initialises the store with the given default language (ISO 639-1 code).</summary>
    /// <param name="defaultLanguage">The language returned for any channel not yet seen.</param>
    public ChannelLanguageStore(string defaultLanguage)
    {
        this.defaultLanguage = defaultLanguage;
    }

    /// <summary>Get the current language for the channel, falling back to the default if unseen.</summary>
    /// <param name="channelKey">Unique channel identifier.</param>
    public string GetCurrent(string channelKey)
        => this.currentByChannel.GetOrAdd(channelKey, this.defaultLanguage);

    /// <summary>
    /// Run the detector on <paramref name="text"/>, consult the policy, and update
    /// the channel's current language if the policy says to switch. Returns a
    /// snapshot describing what happened.
    /// </summary>
    /// <param name="channelKey">Unique channel identifier.</param>
    /// <param name="text">The text to detect language from (transcript or TTS message).</param>
    /// <param name="detector">Language detector to run against the text.</param>
    /// <param name="thresholds">Policy thresholds controlling when a switch fires.</param>
    public LanguageUpdateResult UpdateFromDetection(
        string channelKey,
        string text,
        ILanguageDetector detector,
        LanguageSwitchThresholds thresholds)
    {
        var before = this.GetCurrent(channelKey);
        var conf = detector.DetectConfidences(text);
        var (topCode, topConf) = PickTop(conf, before);
        var confCurrent = conf.TryGetValue(before, out var c) ? c : 0d;
        var after = LanguageSwitchPolicy.Resolve(before, text, conf, thresholds);
        if (!string.Equals(after, before, StringComparison.Ordinal))
        {
            this.currentByChannel[channelKey] = after;
        }

        return new LanguageUpdateResult(
            CurrentBefore: before,
            CurrentAfter: after,
            Candidate: topCode,
            ConfTop: topConf,
            ConfCurrentBefore: confCurrent,
            TextLength: text.Length,
            Switched: !string.Equals(after, before, StringComparison.Ordinal));
    }

    private static (string Code, double Conf) PickTop(IReadOnlyDictionary<string, double> conf, string fallback)
    {
        var topCode = fallback;
        var topConf = 0d;
        foreach (var kv in conf)
        {
            if (kv.Value > topConf)
            {
                topConf = kv.Value;
                topCode = kv.Key;
            }
        }

        return (topCode, topConf);
    }
}
