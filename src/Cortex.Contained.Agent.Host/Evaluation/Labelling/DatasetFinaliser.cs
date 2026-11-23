using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>A judged query after the pre-declared finalisation rules.</summary>
public sealed record LabelledQuery(DraftedQuery Draft, string Category, bool ExpectAbstain, IReadOnlyDictionary<string, int> Grades)
{
    /// <summary>Candidates graded ≥ 1.</summary>
    public IReadOnlyList<string> Relevant => this.Grades.Where(g => g.Value > 0).Select(g => g.Key).Order(StringComparer.Ordinal).ToList();
}

/// <summary>What finalisation did (counts only).</summary>
public sealed record FinaliseReport(int Judged, int Kept, int Dropped, IReadOnlyDictionary<string, int> Recategorised);

/// <summary>
/// Turns judged drafts into dataset queries with rules fixed before any system is compared (they are written into
/// the analysis plan): a synthetic query is dropped when the judge found its source irrelevant (every gold source
/// graded 0, so for enumeration/multi none of its sources graded ≥ 1); a real request with no relevant item becomes an
/// abstention query; an abstention query with a relevant item becomes <c>lookup</c>;
/// enumeration/multi with fewer than two relevant items, paraphrase failing the guard against its judged gold and
/// tag_filtered without tags become <c>lookup</c>. Every judged candidate (grade 0 included) stays a judgment, so
/// judged@10 can be reported.
/// </summary>
public static class DatasetFinaliser
{
    /// <summary>Applies the rules; <paramref name="paraphraseOk"/> checks a paraphrase against its relevant items.</summary>
    public static (IReadOnlyList<LabelledQuery> Queries, FinaliseReport Report) Finalise(
        IEnumerable<(DraftedQuery Draft, IReadOnlyDictionary<string, int> Grades)> judged, Func<DraftedQuery, IReadOnlyList<string>, bool> paraphraseOk)
    {
        ArgumentNullException.ThrowIfNull(judged);
        ArgumentNullException.ThrowIfNull(paraphraseOk);
        var kept = new List<LabelledQuery>();
        var moved = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var (draft, grades) in judged)
        {
            total++;
            var relevant = grades.Where(g => g.Value > 0).Select(g => g.Key).Order(StringComparer.Ordinal).ToList();
            var category = draft.Category;
            if (category != "abstention" && draft.Stratum != "real" && !SourceRelevant(draft, grades, relevant))
            {
                continue;
            }

            if (category == "abstention" || relevant.Count == 0)
            {
                category = relevant.Count == 0 ? "abstention" : "lookup";
            }
            else if ((category is "enumeration" or "multi" && relevant.Count < 2)
                || (category == "paraphrase" && !paraphraseOk(draft, relevant))
                || (category == "tag_filtered" && draft.Tags is not { Count: > 0 }))
            {
                category = "lookup";
            }

            if (category != draft.Category)
            {
                var key = draft.Category + "→" + category;
                moved[key] = moved.GetValueOrDefault(key) + 1;
            }

            kept.Add(new LabelledQuery(draft, category, category == "abstention", grades));
        }

        return (kept, new FinaliseReport(total, kept.Count, total - kept.Count, moved));
    }

    // L8: a synthetic query lives only while the judge found (some of) its source relevant; without a source, any relevant item will do.
    private static bool SourceRelevant(DraftedQuery draft, IReadOnlyDictionary<string, int> grades, List<string> relevant) =>
        draft.GoldSourceIds.Count > 0 ? draft.GoldSourceIds.Any(id => grades.GetValueOrDefault(id) > 0) : relevant.Count > 0;

    /// <summary>The history dataset line of <paramref name="query"/>.</summary>
    public static EvalQuery ToHistory(LabelledQuery query, string queryFamily, string judgeFamily)
    {
        ArgumentNullException.ThrowIfNull(query);
        var draft = query.Draft;
        return new EvalQuery
        {
            Id = draft.Id,
            Text = draft.Text,
            Category = query.Category,
            Stratum = draft.Stratum,
            Judgments = query.Grades
                .Select(g => new EvalJudgment(long.Parse(g.Key, CultureInfo.InvariantCulture), g.Value))
                .OrderBy(j => j.MessageId)
                .ToList(),
            GoldFilters = draft.GoldFilters,
            ModelQuery = draft.ModelText is null ? null : new EvalModelQuery { Text = draft.ModelText, Filters = draft.ModelFilters },
            QueryModelFamily = queryFamily,
            JudgeModelFamily = judgeFamily,
            ExpectAbstain = query.ExpectAbstain,
        };
    }

    /// <summary>The memory dataset line of <paramref name="query"/>.</summary>
    public static MemoryEvalQuery ToMemory(LabelledQuery query, string queryFamily, string judgeFamily)
    {
        ArgumentNullException.ThrowIfNull(query);
        var draft = query.Draft;
        return new MemoryEvalQuery
        {
            Id = draft.Id,
            Text = draft.Text,
            Category = query.Category,
            Stratum = draft.Stratum,
            Judgments = query.Grades.Select(g => new MemoryJudgment(g.Key, g.Value)).OrderBy(j => j.MemoryId, StringComparer.Ordinal).ToList(),
            Tags = query.Category == "tag_filtered" ? draft.Tags : null,
            SourceMemoryId = draft.GoldSourceIds.Count > 0 ? draft.GoldSourceIds[0] : null,
            ModelQuery = draft.ModelText is null ? null : new MemoryModelQuery { Text = draft.ModelText, Tags = draft.ModelTags },
            QueryModelFamily = queryFamily,
            JudgeModelFamily = judgeFamily,
            ExpectAbstain = query.ExpectAbstain,
        };
    }
}

/// <summary>One judged (query, candidate) pair picked for the owner's hand grading.</summary>
public sealed record KappaPick(string QueryId, string CandidateId, int LlmGrade);

/// <summary>
/// The κ sample: <c>count</c> judged pairs stratified by the judge's grade (equal thirds for 0, 1 and 2, topped up
/// from the other grades when one is scarce), at most one pair per query while possible, returned in a deterministic hash order that is independent of the grade
/// (the owner grades blind: no grade blocks).
/// </summary>
public static class KappaSampler
{
    public static IReadOnlyList<KappaPick> Select(IReadOnlyList<LabelledQuery> queries, int count, int seed = 20261121)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var pairs = queries
            .SelectMany(q => q.Grades.Select(g => new KappaPick(q.Draft.Id, g.Key, g.Value)))
            .OrderBy(p => Rank(seed, p.QueryId + "|" + p.CandidateId), StringComparer.Ordinal)
            .ToList();
        var chosen = new List<KappaPick>();
        var usedQueries = new HashSet<string>(StringComparer.Ordinal);
        for (var grade = 0; grade <= 2; grade++)
        {
            var quota = (count / 3) + (grade < count % 3 ? 1 : 0);
            chosen.AddRange(Take(pairs.Where(p => p.LlmGrade == grade), quota, chosen, usedQueries));
        }

        chosen.AddRange(Take(pairs, count - chosen.Count, chosen, usedQueries));

        // The picks were collected grade by grade: shuffle them, or the owner would grade in blocks of the judge's grade.
        return chosen.OrderBy(p => Rank(seed + 1, p.QueryId + "|" + p.CandidateId), StringComparer.Ordinal).ToList();
    }

    private static List<KappaPick> Take(IEnumerable<KappaPick> pool, int wanted, List<KappaPick> chosen, HashSet<string> usedQueries)
    {
        var taken = new List<KappaPick>();
        var candidates = pool.Where(p => !chosen.Contains(p)).ToList();
        foreach (var distinct in new[] { true, false })
        {
            foreach (var pick in candidates)
            {
                if (taken.Count >= wanted)
                {
                    return taken;
                }

                if (!taken.Contains(pick) && (!distinct || !usedQueries.Contains(pick.QueryId)))
                {
                    taken.Add(pick);
                    usedQueries.Add(pick.QueryId);
                }
            }
        }

        return taken;
    }

    private static string Rank(int seed, string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString(CultureInfo.InvariantCulture) + ":" + id)));
}
