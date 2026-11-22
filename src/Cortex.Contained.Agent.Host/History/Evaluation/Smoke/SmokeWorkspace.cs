using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.History.Evaluation.Smoke;

/// <summary>A <c>history_search</c> call as written by the owner (oracle) or by the main model.</summary>
public sealed record SmokeQuerySpec
{
    /// <summary>Query text.</summary>
    public required string Query { get; init; }

    /// <summary><c>relevance</c> or <c>time</c>.</summary>
    public string Order { get; init; } = "relevance";

    /// <summary>Optional lower time bound.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Optional upper time bound.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Optional channel.</summary>
    public string? Channel { get; init; }

    /// <summary>The equivalent recall request.</summary>
    public HistorySearchRequest ToRequest(int limit, string? cursor) => new()
    {
        Query = this.Query,
        Order = string.Equals(this.Order, "time", StringComparison.OrdinalIgnoreCase) ? HistoryOrder.Time : HistoryOrder.Relevance,
        Since = this.Since,
        Until = this.Until,
        Channel = this.Channel,
        Limit = limit,
        Cursor = cursor,
    };
}

/// <summary>One owner-written recall request with gold labels (<c>queries.jsonl</c>).</summary>
public sealed record SmokeQuery
{
    /// <summary>Stable id, e.g. <c>q01</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The natural-language recall request.</summary>
    public required string Request { get; init; }

    /// <summary>The owner's best query (system a).</summary>
    public required SmokeQuerySpec Oracle { get; init; }

    /// <summary>Gold <c>Messages.Id</c>s.</summary>
    public IReadOnlyList<long> Gold { get; init; } = [];

    /// <summary>How the gold items were found: <c>browse</c>, <c>pooled</c> or <c>both</c>.</summary>
    public string GoldFoundBy { get; init; } = "browse";
}

/// <summary>A query written by the main model for one request (<c>model-queries.jsonl</c>).</summary>
public sealed record ModelQuery(string Id, SmokeQuerySpec Spec);

/// <summary>Files of one smoke-evaluation workspace (default <c>/app/state/eval/smoke</c>).</summary>
public sealed class SmokeWorkspace
{
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions indented = new(json) { WriteIndented = true };

    public SmokeWorkspace(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.Directory = directory;
    }

    /// <summary>Workspace root.</summary>
    public string Directory { get; }

    /// <summary>Owner-written queries with gold labels.</summary>
    public string QueriesPath => Path.Combine(this.Directory, "queries.jsonl");

    /// <summary>Model-written queries (system b).</summary>
    public string ModelQueriesPath => Path.Combine(this.Directory, "model-queries.jsonl");

    /// <summary>Owner-written analysis plan (pre-registered).</summary>
    public string AnalysisPlanPath => Path.Combine(this.Directory, "analysis-plan.md");

    /// <summary>Hashes recorded by <c>register</c>.</summary>
    public string PreregistrationPath => Path.Combine(this.Directory, "preregistration.json");

    /// <summary>Run directories (each with a <c>manifest.json</c>; destroyed by overlapping purges).</summary>
    public string RunsDirectory => Path.Combine(this.Directory, "runs");

    /// <summary>Reads the owner's queries.</summary>
    public IReadOnlyList<SmokeQuery> ReadQueries() => ReadJsonLines<SmokeQuery>(this.QueriesPath);

    /// <summary>Reads model-written queries by request id.</summary>
    public IReadOnlyDictionary<string, SmokeQuerySpec> ReadModelQueries() =>
        ReadJsonLines<ModelQuery>(this.ModelQueriesPath).ToDictionary(q => q.Id, q => q.Spec, StringComparer.Ordinal);

    /// <summary>Writes model-written queries.</summary>
    public void WriteModelQueries(IEnumerable<ModelQuery> queries) =>
        File.WriteAllLines(this.ModelQueriesPath, queries.Select(q => JsonSerializer.Serialize(q, json)));

    /// <summary>
    /// Records sha256 of the queries, model queries and analysis plan. Refuses to overwrite an
    /// existing registration: analysis decisions are fixed before the first run.
    /// </summary>
    public void Register()
    {
        if (File.Exists(this.PreregistrationPath))
        {
            throw new InvalidOperationException("Already registered. Delete preregistration.json only to start a new, separately reported evaluation.");
        }

        File.WriteAllText(this.PreregistrationPath, JsonSerializer.Serialize(this.CurrentHashes(), indented));
    }

    /// <summary>Throws unless the registered hashes match the current files.</summary>
    public void VerifyRegistration()
    {
        if (!File.Exists(this.PreregistrationPath))
        {
            throw new InvalidOperationException("Run 'register' before 'run': the queries and analysis plan must be fixed first.");
        }

        var registered = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(this.PreregistrationPath), json) ?? [];
        var changed = this.CurrentHashes()
            .Where(kv => !registered.TryGetValue(kv.Key, out var hash) || !string.Equals(hash, kv.Value, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();
        if (changed.Count > 0)
        {
            throw new InvalidOperationException($"Pre-registered files changed since 'register': {string.Join(", ", changed)}.");
        }
    }

    /// <summary>Writes <paramref name="value"/> as indented JSON.</summary>
    public static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, indented));

    private Dictionary<string, string> CurrentHashes() => new(StringComparer.Ordinal)
    {
        ["queries.jsonl"] = Hash(this.QueriesPath),
        ["model-queries.jsonl"] = Hash(this.ModelQueriesPath),
        ["analysis-plan.md"] = Hash(this.AnalysisPlanPath),
    };

    private static string Hash(string path) =>
        File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : "missing";

    private static List<T> ReadJsonLines<T>(string path) =>
        !File.Exists(path)
            ? []
            : File.ReadLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize<T>(line, json) ?? throw new InvalidDataException($"Bad line in {path}"))
                .ToList();
}
