namespace Cortex.Contained.Channels.Discord.Tests.TurnDetection;

/// <summary>
/// The six end-of-turn fixture recordings consumed by
/// <see cref="TurnDetectionFixtureTests"/>. Filenames match the WAVs
/// committed under <c>Fixtures/turn-detection/</c>; rows whose WAV is
/// absent are skipped at test time so the infrastructure can land before
/// every recording arrives.
/// </summary>
internal static class FixtureCatalog
{
    /// <summary>Subdirectory (relative to the test assembly's output) holding the WAVs.</summary>
    public const string FixturesSubdirectory = "Fixtures/turn-detection";

    public readonly record struct Entry(string FileName, string Label, int ExpectedCommitCount);

    public static IReadOnlyList<Entry> All { get; } =
    [
        new("single-sentence.wav", "single-sentence", 1),
        new("multi-sentence-3.wav", "multi-sentence-3 (gym-bug regression guard)", 1),
        new("multi-sentence-4.wav", "multi-sentence-4", 1),
        new("sentence-with-afterthought.wav", "sentence-with-afterthought", 1),
        new("mid-thought.wav", "mid-thought trailing-off", 1),
        new("short-yes.wav", "short-yes", 1),
    ];

    public static string ResolvePath(Entry entry)
    {
        return Path.Combine(AppContext.BaseDirectory, FixturesSubdirectory, entry.FileName);
    }
}
