using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.Memory.Evaluation;

/// <summary>A graded judgment of one memory for one query (0 = judged not relevant, 1 = partly, 2 = fully).</summary>
public sealed record MemoryJudgment(string MemoryId, int Grade);

/// <summary>A model-written query: text plus the tag filter the model chose.</summary>
public sealed record MemoryModelQuery
{
    /// <summary>Query text.</summary>
    public required string Text { get; init; }

    /// <summary>Tags the model filtered by, or null.</summary>
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>
/// One memory evaluation query. Labels are graded per memory id. Lives only in the local workspace
/// (D6): never in the repository.
/// </summary>
public sealed record MemoryEvalQuery
{
    /// <summary>Stable id, e.g. <c>m001</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The query text given to the system under test (oracle wording).</summary>
    public required string Text { get; init; }

    /// <summary>One of <see cref="MemoryEvalDataset.Categories"/>.</summary>
    public required string Category { get; init; }

    /// <summary><c>synthetic</c> (generated) or <c>real</c> (a real recall request; co-primary stratum).</summary>
    public string Stratum { get; init; } = "synthetic";

    /// <summary>Graded judgments (grade 0 entries record judged non-relevant memories).</summary>
    public IReadOnlyList<MemoryJudgment> Judgments { get; init; } = [];

    /// <summary>Oracle tag filter, or null.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>The query as the main model wrote it (text and tags), or null.</summary>
    public MemoryModelQuery? ModelQuery { get; init; }

    /// <summary>The seed memory the query was written from (generator support), or null.</summary>
    public string? SourceMemoryId { get; init; }

    /// <summary>Model family that generated the query (must differ from <see cref="JudgeModelFamily"/>).</summary>
    public string? QueryModelFamily { get; init; }

    /// <summary>Model family that judged relevance.</summary>
    public string? JudgeModelFamily { get; init; }

    /// <summary>True when no stored memory answers the query (abstention category).</summary>
    public bool ExpectAbstain { get; init; }

    /// <summary>Memories relevant at grade ≥ 1.</summary>
    [JsonIgnore]
    public IReadOnlySet<string> Relevant => this.Judgments.Where(j => j.Grade > 0).Select(j => j.MemoryId).ToHashSet(StringComparer.Ordinal);
}

/// <summary>Reads and validates the memory evaluation <c>dataset.jsonl</c>.</summary>
public static class MemoryEvalDataset
{
    /// <summary>Gate H for memory needs at least this many queries (roadmap, increment 4).</summary>
    public const int MinimumQueries = 250;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Known query categories: <c>multi</c> needs every relevant memory (scored by set F1).</summary>
    public static IReadOnlyList<string> Categories { get; } =
        ["lookup", "paraphrase", "cross_language", "identifier", "tag_filtered", "multi", "abstention"];

    /// <summary>Reads one query per line; blank lines are skipped.</summary>
    public static IReadOnlyList<MemoryEvalQuery> Read(string path) => Parse(File.ReadLines(path), path);

    /// <summary>Parses one query per line of <paramref name="source"/>; blank lines are skipped.</summary>
    public static IReadOnlyList<MemoryEvalQuery> Parse(IEnumerable<string> lines, string source) =>
        lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<MemoryEvalQuery>(line, Json) ?? throw new InvalidDataException($"Bad line in {source}"))
            .ToList();

    /// <summary>Serialises one query as a JSONL line.</summary>
    public static string ToLine(MemoryEvalQuery query) => JsonSerializer.Serialize(query, Json);

    /// <summary>Problems that make the dataset unusable (ids only, never text).</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<MemoryEvalQuery> queries)
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

            if (query.Judgments.Any(j => j.Grade is < 0 or > 2 || string.IsNullOrWhiteSpace(j.MemoryId)))
            {
                errors.Add($"{query.Id}: judgments need a memory id and a grade of 0, 1 or 2");
            }

            var relevant = query.Relevant.Count;
            if (query.ExpectAbstain != (query.Category == "abstention") || (query.ExpectAbstain && relevant > 0))
            {
                errors.Add($"{query.Id}: abstention queries (and only they) expect abstention and have no relevant memory");
            }

            if (!query.ExpectAbstain && relevant == 0)
            {
                errors.Add($"{query.Id}: no relevant memory (grade ≥ 1)");
            }

            if (query.Category == "multi" && relevant < 2)
            {
                errors.Add($"{query.Id}: multi queries need at least two relevant memories");
            }

            if (query.Category == "tag_filtered" && query.Tags is not { Count: > 0 })
            {
                errors.Add($"{query.Id}: tag_filtered queries need an oracle tag filter");
            }

            if (query.QueryModelFamily is { } generator && query.JudgeModelFamily is { } judge
                && string.Equals(generator, judge, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{query.Id}: query generation and judging must use different model families");
            }
        }

        return errors;
    }

    /// <summary>
    /// <see cref="Validate"/> plus the size required before pre-registration: at least
    /// <see cref="MinimumQueries"/> queries.
    /// </summary>
    public static IReadOnlyList<string> ValidateForRegistration(IReadOnlyList<MemoryEvalQuery> queries)
    {
        var errors = Validate(queries).ToList();
        if (queries.Count < MinimumQueries)
        {
            errors.Add($"dataset: {queries.Count} queries; Gate H for memory needs at least {MinimumQueries}");
        }

        return errors;
    }
}
