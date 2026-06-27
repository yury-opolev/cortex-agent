namespace Cortex.Contained.Speech.Tts;

/// <summary>
/// Outcome of one <see cref="ChannelLanguageStore.UpdateFromDetection"/> call.
/// Surfaces everything telemetry wants: before/after, candidate, confidences,
/// text length, and whether a switch happened. Read-only snapshot.
/// </summary>
/// <param name="CurrentBefore">The channel's current language before this update.</param>
/// <param name="CurrentAfter">The channel's current language after this update.</param>
/// <param name="Candidate">The detector's top-guess language for this text.</param>
/// <param name="ConfTop">Detector confidence for <paramref name="Candidate"/>.</param>
/// <param name="ConfCurrentBefore">Detector confidence for <paramref name="CurrentBefore"/> (0 if the detector did not report it).</param>
/// <param name="TextLength">Length of the text the detector saw.</param>
/// <param name="Switched"><c>true</c> when the policy decided to flip <see cref="CurrentBefore"/> to <see cref="CurrentAfter"/>.</param>
public sealed record LanguageUpdateResult(
    string CurrentBefore,
    string CurrentAfter,
    string Candidate,
    double ConfTop,
    double ConfCurrentBefore,
    int TextLength,
    bool Switched)
{
    /// <summary>Convenience alias for <see cref="CurrentAfter"/>.</summary>
    public string NewCurrent => this.CurrentAfter;
}
