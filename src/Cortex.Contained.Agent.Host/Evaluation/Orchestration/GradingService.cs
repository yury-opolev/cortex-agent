using System.Globalization;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;

namespace Cortex.Contained.Agent.Host.Evaluation.Orchestration;

/// <summary>One item of the owner's κ sample as the grading screen shows it (decrypted on request; never logged).</summary>
public sealed record GradingItem(int Index, int Total, string Tool, string Query, string Candidate, int? HumanGrade);

/// <summary>Progress of the hand grading and, once complete, the LLM-vs-human agreement.</summary>
/// <param name="Total">Items in the sample.</param>
/// <param name="Graded">Items the owner graded.</param>
/// <param name="Kappa">Cohen's κ over every item (the gate), or null until complete.</param>
/// <param name="HistoryKappa">κ over the history items (reported), or null.</param>
/// <param name="MemoryKappa">κ over the memory items (reported), or null.</param>
public sealed record GradingProgress(int Total, int Graded, double? Kappa, double? HistoryKappa, double? MemoryKappa)
{
    public bool Complete => this.Total > 0 && this.Graded == this.Total;
}

/// <summary>
/// The owner's blind hand grading of the κ sample (§4.2: LLM-vs-human κ ≥ 0.6): items of the history workspace
/// first, then memory, one at a time, without the judge's grade. Grades are stored in the sealed sample files; when
/// every item is graded, each workspace gets its <c>judge-agreement.csv</c> (ids and grades only, the input of the
/// <c>kappa</c> command) and κ is computed over all items (the gate) and per tool (reported).
/// </summary>
public sealed class GradingService
{
    /// <summary>The tools whose samples are graded, in order.</summary>
    public static readonly IReadOnlyList<string> Tools = ["history-eval", "memory-eval"];

    private readonly IReadOnlyDictionary<string, string> workspaces;
    private readonly Func<EvalProtection> protection;
    private readonly Lock sync = new();

    /// <param name="workspaces">Workspace directory per tool.</param>
    /// <param name="protection">The protection (throws while the storage key is missing).</param>
    public GradingService(IReadOnlyDictionary<string, string> workspaces, Func<EvalProtection> protection)
    {
        this.workspaces = workspaces;
        this.protection = protection;
    }

    /// <summary>Item <paramref name="index"/> (0-based), or null outside the sample.</summary>
    public GradingItem? Item(int index)
    {
        lock (this.sync)
        {
            var items = this.Load();
            if (index < 0 || index >= items.Count)
            {
                return null;
            }

            var item = items[index].Item;
            return new GradingItem(index, items.Count, item.Tool, item.Query, item.Candidate, item.HumanGrade);
        }
    }

    /// <summary>Stores (or clears, with null) the owner's grade of item <paramref name="index"/>.</summary>
    public GradingProgress Grade(int index, int? grade)
    {
        if (grade is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(grade), "Grades are 0, 1 or 2.");
        }

        lock (this.sync)
        {
            var items = this.Load();
            if (index < 0 || index >= items.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "No such item.");
            }

            var tool = items[index].Item.Tool;
            var protection = this.protection();
            var updated = items
                .Where(i => i.Item.Tool == tool)
                .Select(i => i.Index == index ? i.Item with { HumanGrade = grade } : i.Item)
                .ToList();
            KappaSampleFile.Write(this.workspaces[tool], protection, updated);
            return this.Summarise(items.Select(i => i.Index == index ? i.Item with { HumanGrade = grade } : i.Item).ToList(), writeAgreement: true);
        }
    }

    /// <summary>Current progress (κ once complete).</summary>
    public GradingProgress Progress()
    {
        lock (this.sync)
        {
            return this.Summarise(this.Load().Select(i => i.Item).ToList(), writeAgreement: false);
        }
    }

    private List<(int Index, KappaItem Item)> Load()
    {
        var protection = this.protection();
        return Tools
            .Where(this.workspaces.ContainsKey)
            .SelectMany(tool => KappaSampleFile.Read(this.workspaces[tool], protection))
            .Select((item, i) => (i, item))
            .ToList();
    }

    private GradingProgress Summarise(List<KappaItem> items, bool writeAgreement)
    {
        var graded = items.Count(i => i.HumanGrade is not null);
        if (items.Count == 0 || graded < items.Count)
        {
            return new GradingProgress(items.Count, graded, null, null, null);
        }

        if (writeAgreement)
        {
            foreach (var group in items.GroupBy(i => i.Tool, StringComparer.Ordinal))
            {
                File.WriteAllLines(
                    Path.Combine(this.workspaces[group.Key], "judge-agreement.csv"),
                    group.Select(i => string.Create(CultureInfo.InvariantCulture, $"{i.CandidateId},{i.LlmGrade},{i.HumanGrade}")));
            }
        }

        return new GradingProgress(items.Count, graded, Kappa(items), Kappa(items.Where(i => i.Tool == Tools[0]).ToList()), Kappa(items.Where(i => i.Tool == Tools[1]).ToList()));
    }

    private static double? Kappa(List<KappaItem> items) =>
        items.Count < 2 ? null : EvalStatistics.CohensKappa(items.Select(i => i.LlmGrade).ToList(), items.Select(i => i.HumanGrade!.Value).ToList());
}
