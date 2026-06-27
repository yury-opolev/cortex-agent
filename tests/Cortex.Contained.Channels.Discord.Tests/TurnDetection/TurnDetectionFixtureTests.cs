using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Cortex.Contained.Channels.Discord.Tests.TurnDetection;

public class TurnDetectionFixtureTests
{
    private readonly ITestOutputHelper output;

    public TurnDetectionFixtureTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    public static IEnumerable<object[]> Scenarios()
    {
        foreach (var entry in FixtureCatalog.All)
        {
            yield return [entry.FileName, entry.Label, entry.ExpectedCommitCount];
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Fixture_ProducesExpectedCommits(
        string fileName, string label, int expectedCommitCount)
    {
        if (!SpeechModelLocator.TryLocate(out var whisperPath, out var detectorDir))
        {
            this.output.WriteLine($"[skip] speech models not present — fixture: {label}");
            return;
        }

        var entry = FixtureCatalog.All.First(e => e.FileName == fileName);
        var wav = FixtureCatalog.ResolvePath(entry);
        if (!File.Exists(wav))
        {
            this.output.WriteLine($"[skip] fixture WAV not yet recorded: {wav}");
            return;
        }

        await using var harness = await TurnDetectionPipelineHarness.CreateAsync(
            whisperPath, detectorDir, NullLoggerFactory.Instance);
        var commits = await harness.RunAsync(wav);

        this.output.WriteLine($"=== {label} ===");
        foreach (var c in commits)
        {
            this.output.WriteLine(
                $"  t={c.VirtualTimeMs}ms  reason={c.Reason}  silenceAtCommit={c.SilenceMsAtCommit}ms  "
                + $"pEou={c.PEou:F3}  '{c.PartialTranscript}'");
        }

        Assert.Equal(expectedCommitCount, commits.Count);
    }
}
