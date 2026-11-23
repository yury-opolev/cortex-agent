using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.History.Evaluation;

/// <summary>A graded judgment of one message for one query (0 = judged not relevant, 1 = partly, 2 = fully).</summary>
public sealed record EvalJudgment(long MessageId, int Grade);

/// <summary>The filters an ideal searcher would use (oracle filters), compared with model-written ones.</summary>
public sealed record EvalFilters
{
    /// <summary>Channel, or null.</summary>
    public string? Channel { get; init; }

    /// <summary>Inclusive lower bound, or null.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Exclusive upper bound, or null.</summary>
    public DateTimeOffset? Until { get; init; }
}

/// <summary>
/// One evaluation query (§4.2 evaluation protocol). Labels are message-level and graded, so they are
/// independent of segmentation. Lives only in the local workspace (D6): never in the repository.
/// </summary>
public sealed record EvalQuery
{
    /// <summary>Stable id, e.g. <c>e001</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The query text given to the system under test (oracle wording).</summary>
    public required string Text { get; init; }

    /// <summary>One of <see cref="EvalDataset.Categories"/>.</summary>
    public required string Category { get; init; }

    /// <summary><c>synthetic</c> (generated) or <c>real</c> (a real recall request; co-primary stratum).</summary>
    public string Stratum { get; init; } = "synthetic";

    /// <summary>Graded judgments (grade 0 entries record judged non-relevant messages).</summary>
    public IReadOnlyList<EvalJudgment> Judgments { get; init; } = [];

    /// <summary>Oracle filters.</summary>
    public EvalFilters? GoldFilters { get; init; }

    /// <summary>The query as the main model wrote it (text and filters), or null.</summary>
    public EvalModelQuery? ModelQuery { get; init; }

    /// <summary>Model family that generated the query (must differ from <see cref="JudgeModelFamily"/>).</summary>
    public string? QueryModelFamily { get; init; }

    /// <summary>Model family that judged relevance.</summary>
    public string? JudgeModelFamily { get; init; }

    /// <summary>True when nothing relevant exists (abstention category).</summary>
    public bool ExpectAbstain { get; init; }

    /// <summary>Messages relevant at grade ≥ 1.</summary>
    [JsonIgnore]
    public IReadOnlySet<long> Relevant => this.Judgments.Where(j => j.Grade > 0).Select(j => j.MessageId).ToHashSet();
}

/// <summary>A model-written query: text plus the filters the model chose.</summary>
public sealed record EvalModelQuery
{
    /// <summary>Query text.</summary>
    public required string Text { get; init; }

    /// <summary>Filters the model chose.</summary>
    public EvalFilters? Filters { get; init; }
}

/// <summary>Reads and validates <c>dataset.jsonl</c>.</summary>
public static class EvalDataset
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Known query categories.</summary>
    public static IReadOnlyList<string> Categories { get; } =
        ["lookup", "paraphrase", "cross_language", "temporal", "identifier", "enumeration", "abstention"];

    /// <summary>Reads one query per line; blank lines are skipped.</summary>
    public static IReadOnlyList<EvalQuery> Read(string path) => Parse(File.ReadLines(path), path);

    /// <summary>Parses one query per line of <paramref name="source"/>; blank lines are skipped.</summary>
    public static IReadOnlyList<EvalQuery> Parse(IEnumerable<string> lines, string source) =>
        lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<EvalQuery>(line, Json) ?? throw new InvalidDataException($"Bad line in {source}"))
            .ToList();

    /// <summary>Serialises one query as a JSONL line.</summary>
    public static string ToLine(EvalQuery query) => JsonSerializer.Serialize(query, Json);

    /// <summary>Returns problems that make the dataset unusable for a pre-registered run (ids only, never text).</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<EvalQuery> queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var errors = new List<string>();
        foreach (var duplicate in queries.GroupBy(q => q.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"{duplicate.Key}: duplicate id");
        }

        foreach (var query in queries)
        {
            if (!Categories.Contains(query.Category, StringComparer.Ordinal))
            {
                errors.Add($"{query.Id}: unknown category '{query.Category}'");
            }

            if (query.Stratum is not ("synthetic" or "real"))
            {
                errors.Add($"{query.Id}: stratum must be 'synthetic' or 'real'");
            }

            if (query.Judgments.Any(j => j.Grade is < 0 or > 2))
            {
                errors.Add($"{query.Id}: grades must be 0, 1 or 2");
            }

            var relevant = query.Relevant.Count;
            if (query.ExpectAbstain != (query.Category == "abstention") || (query.ExpectAbstain && relevant > 0))
            {
                errors.Add($"{query.Id}: abstention queries (and only they) expect abstention and have no relevant message");
            }

            if (!query.ExpectAbstain && relevant == 0)
            {
                errors.Add($"{query.Id}: no relevant message (grade ≥ 1)");
            }

            if (query.Category == "enumeration" && relevant < 2)
            {
                errors.Add($"{query.Id}: enumeration queries need at least two relevant messages");
            }

            if (query.QueryModelFamily is { } generator && query.JudgeModelFamily is { } judge
                && string.Equals(generator, judge, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{query.Id}: query generation and judging must use different model families");
            }
        }

        return errors;
    }
}
