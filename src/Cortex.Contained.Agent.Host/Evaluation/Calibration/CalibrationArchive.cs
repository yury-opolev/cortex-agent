using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Evaluation.Orchestration;

namespace Cortex.Contained.Agent.Host.Evaluation.Calibration;

/// <summary>
/// The calibration set: every locked κ sample (the owner's submitted grades) of every run, kept under
/// <c>&lt;evaluation&gt;/calibration/sets/&lt;runId&gt;/&lt;tool&gt;/</c> so the judge can be piloted on them after the
/// run's workspaces were wiped by a fresh start. The sample and its agreement file are copied byte for byte: a sealed
/// file binds only its file name as associated data, so the copy stays sealed and readable. Rubric variants live under
/// <c>rubrics/</c> (plaintext, no personal data) and calibration reports under <c>reports/</c> (ids and numbers only).
/// </summary>
public sealed partial class CalibrationArchive
{
    /// <summary>The agreement file <see cref="GradingService.Submit"/> writes: its presence marks a locked sample.</summary>
    public const string AgreementFileName = "judge-agreement.csv";

    /// <summary>The calibration root inside the evaluation root (<c>/app/state/eval</c>).</summary>
    public static string RootIn(string evaluationDirectory) => Path.Combine(evaluationDirectory, "calibration");

    /// <param name="root">The calibration root (<c>/app/state/eval/calibration</c>).</param>
    public CalibrationArchive(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.Root = root;
    }

    public string Root { get; }

    public string SetsDirectory => Path.Combine(this.Root, "sets");

    public string RubricsDirectory => Path.Combine(this.Root, "rubrics");

    public string ReportsDirectory => Path.Combine(this.Root, "reports");

    /// <summary>
    /// Copies the locked samples of <paramref name="workspaces"/> (tool → directory) into the set of
    /// <paramref name="runId"/>; files already archived are kept. Returns the number of tools whose sample is archived.
    /// </summary>
    public int Archive(string runId, IReadOnlyDictionary<string, string> workspaces)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        if (runId is null || !RunIdPattern().IsMatch(runId))
        {
            throw new ArgumentException("A run id is 1–32 lowercase letters or digits.", nameof(runId));
        }

        var archived = 0;
        foreach (var tool in GradingService.Tools)
        {
            if (!workspaces.TryGetValue(tool, out var directory) || !IsLocked(directory))
            {
                continue;
            }

            var target = Path.Combine(this.SetsDirectory, runId, tool);
            Directory.CreateDirectory(target);
            foreach (var name in new[] { KappaSampleFile.FileName, AgreementFileName })
            {
                var destination = Path.Combine(target, name);
                if (!File.Exists(destination))
                {
                    var temp = destination + ".tmp";
                    File.Copy(Path.Combine(directory, name), temp, overwrite: true);
                    File.Move(temp, destination);
                }
            }

            archived++;
        }

        return archived;
    }

    /// <summary>
    /// Every graded item of every archived set (sets in run-id order). Each set is its own population: nothing is
    /// deduplicated, since query ids restart with every run and the same ids can name different items.
    /// </summary>
    public IReadOnlyList<KappaItem> Load(EvalProtection protection)
    {
        ArgumentNullException.ThrowIfNull(protection);
        return this.ArchivedSamples()
            .SelectMany(s => KappaSampleFile.Read(s.Directory, protection))
            .Where(i => i.HumanGrade is not null)
            .ToList();
    }

    /// <summary>
    /// The calibration set: the graded items of the locked workspace samples that are not archived yet, then every
    /// archived set (<see cref="Load"/>). A workspace sample counts as archived when some set holds a byte-identical
    /// <c>kappa-sample.json</c> for its tool (the archive copies byte for byte), so a run's sample never counts twice.
    /// </summary>
    public IReadOnlyList<KappaItem> CalibrationSet(IReadOnlyDictionary<string, string> workspaces, EvalProtection protection)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(protection);
        var archived = this.ArchivedSamples()
            .Select(s => (s.Tool, Hash: Hash(Path.Combine(s.Directory, KappaSampleFile.FileName))))
            .ToHashSet();
        var current = GradingService.Tools
            .Where(tool => workspaces.TryGetValue(tool, out var directory) && IsLocked(directory)
                && !archived.Contains((tool, Hash(Path.Combine(directory, KappaSampleFile.FileName)))))
            .SelectMany(tool => KappaSampleFile.Read(workspaces[tool], protection))
            .Where(i => i.HumanGrade is not null);
        return [.. current, .. this.Load(protection)];
    }

    /// <summary>
    /// Deletes every archived set (they hold message and memory text, so a history purge removes them all: which
    /// messages the items came from is not known); rubrics and reports, which hold none, stay. False when some could not
    /// be deleted.
    /// </summary>
    public bool DestroySets()
    {
        try
        {
            if (Directory.Exists(this.SetsDirectory))
            {
                Directory.Delete(this.SetsDirectory, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes the live κ sample of each workspace (<c>kappa-sample.json</c>, its pending <c>.tmp</c> write and
    /// <c>judge-agreement.csv</c>), for the same reason as <see cref="DestroySets"/>: a sample left behind would be
    /// archived again or piloted on. False when some file could not be deleted.
    /// </summary>
    public static bool DestroyWorkspaceSamples(IEnumerable<string> workspaces)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        var allGone = true;
        foreach (var path in workspaces.Where(w => !string.IsNullOrWhiteSpace(w)).SelectMany(w => new[] { KappaSampleFile.FileName, KappaSampleFile.FileName + ".tmp", AgreementFileName }.Select(n => Path.Combine(w, n))))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                allGone = false;
            }
        }

        return allGone;
    }

    // The locked samples of every set, in run-id order.
    private List<(string Tool, string Directory)> ArchivedSamples() =>
        !Directory.Exists(this.SetsDirectory)
            ? []
            : Directory.EnumerateDirectories(this.SetsDirectory)
                .Order(StringComparer.Ordinal)
                .SelectMany(set => GradingService.Tools.Select(tool => (Tool: tool, Directory: Path.Combine(set, tool))))
                .Where(s => IsLocked(s.Directory))
                .ToList();

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static bool IsLocked(string directory) =>
        File.Exists(Path.Combine(directory, KappaSampleFile.FileName)) && File.Exists(Path.Combine(directory, AgreementFileName));

    [GeneratedRegex(@"^[a-z0-9]{1,32}\z")]
    private static partial Regex RunIdPattern();
}
