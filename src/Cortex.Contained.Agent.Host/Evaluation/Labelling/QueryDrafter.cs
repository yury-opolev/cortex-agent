using System.Globalization;
using System.Text;
using Cortex.Contained.Agent.Host.History.Evaluation;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>One stored message or memory shown to a model (raw text; never written out).</summary>
public sealed record MaterialItem(string Id, string Text, string? Channel = null, DateTimeOffset? Time = null, string? Role = null, string? Title = null)
{
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>What the writer sees for one seed: the snippet items, or a real request with its preceding turns.</summary>
public sealed record SeedMaterial(IReadOnlyList<MaterialItem> Items, string? Request = null)
{
    /// <summary>Real requests: the turns before the request, oldest first (<c>role: text</c>).</summary>
    public IReadOnlyList<string> Context { get; init; } = [];
}

/// <summary>The writer's answer (strict JSON).</summary>
public sealed record DraftAnswer
{
    public string? Text { get; init; }

    public string? Category { get; init; }

    public EvalFilters? GoldFilters { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>
/// The query writer (a model of another family than the judge, §4.2): drafts one query per seed in the seed's
/// category, never copying the snippet's wording, with the oracle filters (history) or tags (memory). Every draft
/// is checked (<see cref="DraftChecks"/>, including the paraphrase guard for paraphrase queries) and written again
/// with the reason when it fails, up to <see cref="LabellingOptions.MaxDraftAttempts"/> times.
/// </summary>
public sealed class QueryDrafter
{
    /// <summary>Categories a real history request can be classified as.</summary>
    public static readonly IReadOnlyList<string> RealHistoryCategories = ["lookup", "temporal", "identifier", "enumeration"];

    /// <summary>Categories a real memory request can be classified as.</summary>
    public static readonly IReadOnlyList<string> RealMemoryCategories = ["lookup", "identifier", "tag_filtered", "multi"];

    private const string Rules = """
        Rules:
        - Never copy wording from the snippet: no run of four or more of its words (an identifier may be reused when asked).
        - The query must stand alone: do not mention a snippet, a message or "the text above".
        - One sentence, under 200 characters, written the way the owner would type or say it.
        """;

    private static readonly Dictionary<string, string> Instructions = new(StringComparer.Ordinal)
    {
        ["lookup"] = "Write a question whose answer is in the snippet, in the snippet's language.",
        ["paraphrase"] = "Write a question whose answer is in the snippet but that shares NO content word with it: use synonyms and describe things indirectly.",
        ["cross_language"] = "Write a question whose answer is in the snippet, in another language than the snippet (English if the snippet is not in English, otherwise Danish or Russian).",
        ["temporal"] = "Write a question that asks about the snippet's content by when it happened (a period such as 'last spring' or 'in March'); set goldFilters.since and goldFilters.until to the period the question names, which must contain the message time.",
        ["identifier"] = "Write a question that uses the exact identifier from the snippet (a code, number with letters, file name or similar) as written.",
        ["enumeration"] = "Write a question that asks for all of several things mentioned across these messages (e.g. 'which … did we …'), so that at least two of the messages are needed for a complete answer.",
        ["abstention"] = "The snippet only shows the topic area. Write a plausible, specific question in this area whose answer is NOT in the snippet and is unlikely to have been discussed at all.",
        ["tag_filtered"] = "Write a question whose answer is in the memory and set tags to the memory's tags an ideal searcher would filter by (a subset of its tags).",
        ["multi"] = "Write a question that is only answered by several of these memories together, so that at least two of them are needed.",
    };

    private readonly LabellingLlm llm;
    private readonly string model;
    private readonly bool memory;
    private readonly int maxAttempts;
    private readonly int maxChars;
    private readonly TimeProvider time;

    /// <param name="llm">The pinned client.</param>
    /// <param name="model">The query writer model.</param>
    /// <param name="tool"><c>history-eval</c> or <c>memory-eval</c>.</param>
    /// <param name="maxAttempts">Drafts per seed.</param>
    /// <param name="maxChars">Characters per snippet item.</param>
    /// <param name="time">The clock (relative periods such as 'last spring' are read against it).</param>
    public QueryDrafter(LabellingLlm llm, string model, string tool, int maxAttempts, int maxChars, TimeProvider? time = null)
    {
        this.llm = llm;
        this.model = model;
        this.memory = string.Equals(tool, "memory-eval", StringComparison.Ordinal);
        this.maxAttempts = maxAttempts;
        this.maxChars = maxChars;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>A checked draft for <paramref name="seed"/>, or null when every attempt failed.</summary>
    /// <param name="seed">The seed.</param>
    /// <param name="material">Its snippet or request.</param>
    /// <param name="paraphraseOk">The paraphrase guard: query text and gold texts → passes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<DraftAnswer?> DraftAsync(LabelSeed seed, SeedMaterial material, Func<string, IReadOnlyList<string>, bool> paraphraseOk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(material);
        var prompt = this.Prompt(seed, material);
        string? problem = null;
        for (var attempt = 0; attempt < this.maxAttempts; attempt++)
        {
            var user = problem is null ? prompt : prompt + "\n\nYour previous query was rejected: " + problem + " Write a different one.";
            var answer = await this.llm.CompleteJsonAsync<DraftAnswer>(this.model, this.System(), user, 800, a => !string.IsNullOrWhiteSpace(a.Text), cancellationToken).ConfigureAwait(false);
            if (answer is null)
            {
                return null;
            }

            answer = this.Normalise(seed, answer);
            problem = DraftChecks.Problem(seed, material, answer, paraphraseOk);
            if (problem is null)
            {
                return answer;
            }
        }

        return null;
    }

    private DraftAnswer Normalise(LabelSeed seed, DraftAnswer answer)
    {
        var allowed = this.memory ? RealMemoryCategories : RealHistoryCategories;
        var category = seed.Stratum == "real"
            ? (allowed.Contains(answer.Category ?? string.Empty, StringComparer.Ordinal) ? answer.Category! : "lookup")
            : seed.Category;
        return answer with { Text = answer.Text!.Trim(), Category = category };
    }

    private string System() => this.memory
        ? "You write test queries for evaluating search over a person's curated memories (short notes their assistant stored). " +
          "You get one or more memories (the snippet) and the kind of query to write: the question the owner would ask to find that information later.\n" + Rules +
          "\n- tags: only for tag_filtered queries, otherwise null.\nReturn ONLY a JSON object: {\"text\": string, \"category\": string, \"tags\": [string] or null}."
        : "You write test queries for evaluating search over a person's own chat history with their assistant. " +
          "You get one or more stored messages (the snippet) and the kind of query to write: the question the owner would ask to find that information later.\n" + Rules +
          "\n- goldFilters are the filters an ideal searcher would use: channel only when the query names the conversation, since/until (ISO 8601, UTC) only when the query names a time.\n" +
          "Return ONLY a JSON object: {\"text\": string, \"category\": string, \"goldFilters\": {\"channel\": string or null, \"since\": string or null, \"until\": string or null}}.";

    private string Prompt(LabelSeed seed, SeedMaterial material)
    {
        var text = new StringBuilder();
        if (seed.Stratum == "real")
        {
            var categories = string.Join(", ", this.memory ? RealMemoryCategories : RealHistoryCategories);
            text.AppendLine("The owner really asked the request below. Rewrite it as a standalone search request with the same intent and in the same language, resolving references with the context only and adding no facts.");
            text.AppendLine(CultureInfo.InvariantCulture, $"Set category to one of: {categories}.");
            if (material.Context.Count > 0)
            {
                text.AppendLine("Context (oldest first):");
                foreach (var turn in material.Context)
                {
                    text.AppendLine(this.Clip(turn));
                }
            }

            text.AppendLine(CultureInfo.InvariantCulture, $"Request ({seed.RequestTime:yyyy-MM-dd HH:mm} UTC): {this.Clip(material.Request ?? string.Empty)}");
            return text.ToString();
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"Current time (UTC): {this.time.GetUtcNow():yyyy-MM-dd HH:mm}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Kind of query: {seed.Category}. {Instructions[seed.Category]}");
        text.AppendLine("Snippet:");
        for (var i = 0; i < material.Items.Count; i++)
        {
            var item = material.Items[i];
            var header = this.memory
                ? $"[{i + 1}] title: {item.Title ?? "-"} · tags: {(item.Tags.Count == 0 ? "-" : string.Join(", ", item.Tags))}"
                : string.Create(CultureInfo.InvariantCulture, $"[{i + 1}] {item.Time:yyyy-MM-dd HH:mm} UTC · channel {item.Channel} · {item.Role}");
            text.AppendLine(header);
            text.AppendLine(this.Clip(item.Text));
        }

        return text.ToString();
    }

    private string Clip(string text) => text.Length <= this.maxChars ? text : string.Concat(text.AsSpan(0, this.maxChars), "…");
}

/// <summary>Checks of a draft before it is accepted (reasons hold no text).</summary>
public static class DraftChecks
{
    /// <summary>Longest run of snippet words a query may repeat.</summary>
    public const int MaxSharedRun = 3;

    /// <summary>Why <paramref name="answer"/> is not usable for <paramref name="seed"/>, or null when it is.</summary>
    public static string? Problem(LabelSeed seed, SeedMaterial material, DraftAnswer answer, Func<string, IReadOnlyList<string>, bool> paraphraseOk)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(paraphraseOk);
        var text = answer.Text ?? string.Empty;
        if (text.Length is 0 or > 300)
        {
            return "the query is empty or longer than 300 characters.";
        }

        var sources = material.Items.Select(i => i.Text).ToList();
        if (seed.Stratum != "real" && answer.Category != "identifier" && sources.Any(s => SharesRun(text, s, MaxSharedRun + 1)))
        {
            return "it repeats four or more consecutive words of the snippet.";
        }

        switch (answer.Category)
        {
            case "paraphrase" when !paraphraseOk(text, sources):
                return "it shares words with the snippet (paraphrase guard: Jaccard above 0.2 or a shared rare word).";
            case "identifier" when !QueryIntent.HasExactIntent(text):
                return "it does not contain the identifier as written.";
            case "temporal" when seed.Stratum != "real" && !Contains(answer.GoldFilters, material.Items[0].Time):
                return "goldFilters.since/until must name a period that contains the message time.";
            case "tag_filtered" when answer.Tags is not { Count: > 0 } tags
                || (seed.Stratum != "real" && !tags.All(t => material.Items.SelectMany(i => i.Tags).Contains(t, StringComparer.OrdinalIgnoreCase))):
                return "tags must be a non-empty subset of the memory's tags.";
            default:
                return null;
        }
    }

    /// <summary>True when <paramref name="query"/> repeats <paramref name="run"/> consecutive folded words of <paramref name="source"/>.</summary>
    public static bool SharesRun(string query, string source, int run)
    {
        var words = KeywordTokenizer.Tokenize(TextFolder.Fold(query));
        if (words.Count < run)
        {
            return false;
        }

        var grams = Grams(KeywordTokenizer.Tokenize(TextFolder.Fold(source)), run);
        return Grams(words, run).Any(grams.Contains);
    }

    private static HashSet<string> Grams(IReadOnlyList<string> words, int run) =>
        Enumerable.Range(0, Math.Max(0, words.Count - run + 1)).Select(i => string.Join(' ', words.Skip(i).Take(run))).ToHashSet(StringComparer.Ordinal);

    private static bool Contains(EvalFilters? filters, DateTimeOffset? time) =>
        filters is not null && time is { } t && (filters.Since is not null || filters.Until is not null)
        && (filters.Since is null || filters.Since <= t) && (filters.Until is null || t < filters.Until);
}
