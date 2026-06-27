namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Snapshot of what TTS actually reached the user when playback was stopped.
/// <paramref name="FullyPlayedSentences"/> are known exactly (drained from the
/// queue); <paramref name="InterruptedPlayedRatio"/> is the frames-written /
/// frames-total estimate for the one in-flight sentence.
/// </summary>
public sealed record PlaybackProgress(
    IReadOnlyList<string> FullyPlayedSentences,
    string? InterruptedSentenceText,
    double InterruptedPlayedRatio);
