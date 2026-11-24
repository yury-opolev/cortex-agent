using System.Globalization;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;

namespace Cortex.Contained.Agent.Host.Evaluation.Orchestration;

/// <summary>
/// One item of the owner's κ sample as the grading screen shows it (decrypted on request; never logged).
/// <see cref="LlmGrade"/> is the judge's grade, revealed only once the grades are submitted (locked); null before.
/// </summary>
public sealed record GradingItem(int Index, int Total, string Tool, string Query, string Candidate, int? HumanGrade, int? LlmGrade = null);

/// <summary>Progress of the hand grading and, once complete, the LLM-vs-human agreement.</summary>
/// <param name="Total">Items in the sample.</param>
/// <param name="Graded">Items the owner graded.</param>
/// <param name="Kappa">Cohen's κ over every item (the gate), or null until the owner has submitted the grades.</param>
/// <param name="HistoryKappa">κ over the history items (reported), or null.</param>
/// <param name="MemoryKappa">κ over the memory items (reported), or null.</param>
/// <param name="Submitted">True once the owner submitted the grades: κ is computed and the grades are locked.</param>
/// <param name="Disagreements">0-based indices of the items where the owner and the judge differ (indices only), or null until submitted.</param>
public sealed record GradingProgress(
    int Total, int Graded, double? Kappa, double? HistoryKappa, double? MemoryKappa, bool Submitted = false, IReadOnlyList<int>? Disagreements = null)
{
    public bool Complete => this.Total > 0 && this.Graded == this.Total;

    // Value equality over the disagreement indices too (a list compares by reference otherwise).
    public bool Equals(GradingProgress? other) =>
        other is not null
        && (this.Total, this.Graded, this.Kappa, this.HistoryKappa, this.MemoryKappa, this.Submitted)
            == (other.Total, other.Graded, other.Kappa, other.HistoryKappa, other.MemoryKappa, other.Submitted)
        && (this.Disagreements is null ? other.Disagreements is null : other.Disagreements is not null && this.Disagreements.SequenceEqual(other.Disagreements));

    public override int GetHashCode() => HashCode.Combine(this.Total, this.Graded, this.Kappa, this.Submitted, this.Disagreements?.Count);
}

/// <summary>
/// The owner's blind hand grading of the κ sample (§4.2: LLM-vs-human κ ≥ 0.6): items of the history workspace
/// first, then memory, one at a time, without the judge's grade (revealed, with the indices of the disagreements, only
/// once the grades are submitted, for review). Grades are stored in the sealed sample files and can
/// be changed freely, with no κ shown, until the owner submits them (<see cref="Submit"/>): that writes each
/// workspace's <c>judge-agreement.csv</c> (ids and grades only, the input of the <c>kappa</c> command), computes κ over
/// all items (the gate) and per tool (reported) and locks the grades — a gate decision made on grades that could still
/// be tuned towards the threshold would be worthless.
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
            var submitted = this.IsSubmitted(items.Select(i => i.Item).ToList());
            return new GradingItem(index, items.Count, item.Tool, item.Query, item.Candidate, item.HumanGrade, submitted ? item.LlmGrade : null);
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

            if (this.IsSubmitted(items.Select(i => i.Item).ToList()))
            {
                throw new InvalidOperationException("The grades were submitted and are locked.");
            }

            var tool = items[index].Item.Tool;
            var protection = this.protection();
            var updated = items
                .Where(i => i.Item.Tool == tool)
                .Select(i => i.Index == index ? i.Item with { HumanGrade = grade } : i.Item)
                .ToList();
            KappaSampleFile.Write(this.workspaces[tool], protection, updated);
            return Summarise(items.Select(i => i.Index == index ? i.Item with { HumanGrade = grade } : i.Item).ToList(), submitted: false);
        }
    }

    /// <summary>
    /// The owner's "done": writes the agreement files, computes κ and locks the grades. Idempotent once submitted;
    /// throws <see cref="InvalidOperationException"/> while items are still ungraded.
    /// </summary>
    public GradingProgress Submit()
    {
        lock (this.sync)
        {
            var items = this.Load().Select(i => i.Item).ToList();
            if (items.Count == 0 || items.Any(i => i.HumanGrade is null))
            {
                throw new InvalidOperationException("Grade every item before submitting.");
            }

            foreach (var group in items.GroupBy(i => i.Tool, StringComparer.Ordinal))
            {
                File.WriteAllLines(
                    this.AgreementPath(group.Key),
                    group.Select(i => string.Create(CultureInfo.InvariantCulture, $"{i.CandidateId},{i.LlmGrade},{i.HumanGrade}")));
            }

            return Summarise(items, submitted: true);
        }
    }

    /// <summary>Current progress (κ once the grades are submitted).</summary>
    public GradingProgress Progress()
    {
        lock (this.sync)
        {
            var items = this.Load().Select(i => i.Item).ToList();
            return Summarise(items, this.IsSubmitted(items));
        }
    }

    private string AgreementPath(string tool) => Path.Combine(this.workspaces[tool], "judge-agreement.csv");

    // Submitted = a complete sample whose agreement files exist (a stale or pushed file never locks an unfinished sample).
    private bool IsSubmitted(List<KappaItem> items) =>
        items.Count > 0 && items.All(i => i.HumanGrade is not null)
        && items.Select(i => i.Tool).Distinct(StringComparer.Ordinal).All(tool => File.Exists(this.AgreementPath(tool)));

    private List<(int Index, KappaItem Item)> Load()
    {
        var protection = this.protection();
        return Tools
            .Where(this.workspaces.ContainsKey)
            .SelectMany(tool => KappaSampleFile.Read(this.workspaces[tool], protection))
            .Select((item, i) => (i, item))
            .ToList();
    }

    private static GradingProgress Summarise(List<KappaItem> items, bool submitted)
    {
        var graded = items.Count(i => i.HumanGrade is not null);
        return submitted && items.Count > 0 && graded == items.Count
            ? new GradingProgress(
                items.Count,
                graded,
                Kappa(items),
                Kappa(items.Where(i => i.Tool == Tools[0]).ToList()),
                Kappa(items.Where(i => i.Tool == Tools[1]).ToList()),
                Submitted: true,
                Disagreements: items.Select((item, index) => (item, index)).Where(p => p.item.HumanGrade != p.item.LlmGrade).Select(p => p.index).ToList())
            : new GradingProgress(items.Count, graded, null, null, null);
    }

    private static double? Kappa(List<KappaItem> items) =>
        items.Count < 2 ? null : EvalStatistics.CohensKappa(items.Select(i => i.LlmGrade).ToList(), items.Select(i => i.HumanGrade!.Value).ToList());
}
