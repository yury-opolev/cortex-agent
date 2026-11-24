using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>One pooled candidate shown to the judge (raw text; never written out).</summary>
public sealed record JudgeCandidate(string Id, string Text);

/// <summary>The judge's answer (strict JSON): a grade per item alias.</summary>
public sealed record JudgeAnswer(Dictionary<string, int>? Grades);

/// <summary>
/// The relevance judge (a model of another family than the query writer, §4.2): grades pooled candidates 0–2 in
/// batches, as strict JSON keyed by short item aliases. An answer that misses an item, adds one or uses another
/// grade is asked again; a batch that never gets a valid answer is split in halves down to single items. Graded
/// event/message-level labels result (one grade per message or memory).
/// </summary>
public sealed class RelevanceJudge
{
    /// <summary>
    /// The grading guidance of the default judge (the replaceable part of the system prompt; the answer format is
    /// always appended). A calibrated variant replaces it via <see cref="LabellingOptions.JudgeRubric"/>.
    /// </summary>
    public const string DefaultRubric = """
        For each item decide how well it answers the query:
        2 = the item directly answers the query or contains exactly the information asked for;
        1 = the item is related and partly answers it (part of the answer, one of several things asked for, or context needed to answer);
        0 = not relevant.
        Judge every item on its own text only and do not assume information that is not there. Items and queries may be in any language.
        """;

    private const string AnswerFormat = """Return ONLY a JSON object {"grades": {"<item id>": 0, 1 or 2}} with every item id exactly once.""";

    // The line break of the rubric literal (the source file's), so the default prompt stays byte-identical to the one-literal version.
    private static readonly string LineBreak = DefaultRubric.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private readonly LabellingLlm llm;
    private readonly string model;
    private readonly int batchSize;
    private readonly string system;

    /// <param name="llm">The pinned client.</param>
    /// <param name="model">The judge model.</param>
    /// <param name="batchSize">Candidates per call.</param>
    /// <param name="tool"><c>history-eval</c> (messages) or <c>memory-eval</c> (memories).</param>
    /// <param name="rubric">The grading guidance; null or blank is <see cref="DefaultRubric"/>.</param>
    public RelevanceJudge(LabellingLlm llm, string model, int batchSize, string tool, string? rubric = null)
    {
        this.llm = llm;
        this.model = model;
        this.batchSize = batchSize;
        var subject = string.Equals(tool, "memory-eval", StringComparison.Ordinal)
            ? "You judge search results over a person's curated memories (notes their assistant stored)."
            : "You judge search results over a person's chat history with their assistant; every item is one stored message.";
        var guidance = string.IsNullOrWhiteSpace(rubric) ? DefaultRubric : rubric.TrimEnd();
        this.system = subject + "\n" + guidance + LineBreak + AnswerFormat;
    }

    /// <summary>
    /// Provenance of a rubric: <c>default</c> for null, blank or <see cref="DefaultRubric"/>, otherwise <c>sha256:</c> and
    /// the first 12 hex digits of the SHA-256 of its UTF-8 text (line breaks normalised to LF, trailing white space trimmed,
    /// so the same rubric edited on Windows or Linux keeps its id).
    /// </summary>
    public static string RubricId(string? rubric)
    {
        if (string.IsNullOrWhiteSpace(rubric))
        {
            return "default";
        }

        var text = rubric.ReplaceLineEndings("\n").TrimEnd();
        if (string.Equals(text, DefaultRubric.ReplaceLineEndings("\n").TrimEnd(), StringComparison.Ordinal))
        {
            return "default";
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
    }

    /// <summary>Grade per candidate id, or null when some candidate could not be graded.</summary>
    public async Task<IReadOnlyDictionary<string, int>?> GradeAsync(string query, IReadOnlyList<JudgeCandidate> candidates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var grades = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var batch in candidates.Chunk(this.batchSize))
        {
            if (!await this.GradeBatchAsync(query, batch, grades, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }

        return grades;
    }

    private async Task<bool> GradeBatchAsync(string query, JudgeCandidate[] batch, Dictionary<string, int> grades, CancellationToken cancellationToken)
    {
        var aliases = batch.Select((c, i) => (Alias: "c" + (i + 1).ToString(CultureInfo.InvariantCulture), Candidate: c)).ToList();
        var prompt = new StringBuilder();
        prompt.AppendLine(CultureInfo.InvariantCulture, $"Query: {query}");
        prompt.AppendLine();
        prompt.AppendLine("Items:");
        foreach (var (alias, candidate) in aliases)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"[{alias}] {candidate.Text}");
        }

        var expected = aliases.Select(a => a.Alias).ToHashSet(StringComparer.Ordinal);
        var answer = await this.llm.CompleteJsonAsync<JudgeAnswer>(
            this.model,
            this.system,
            prompt.ToString(),
            400 + (20 * batch.Length),
            a => a.Grades is { } g && g.Count == expected.Count && g.Keys.All(expected.Contains) && g.Values.All(v => v is >= 0 and <= 2),
            cancellationToken).ConfigureAwait(false);
        if (answer?.Grades is { } valid)
        {
            foreach (var (alias, candidate) in aliases)
            {
                grades[candidate.Id] = valid[alias];
            }

            return true;
        }

        if (batch.Length == 1)
        {
            return false;
        }

        var half = batch.Length / 2;
        return await this.GradeBatchAsync(query, batch[..half], grades, cancellationToken).ConfigureAwait(false)
            && await this.GradeBatchAsync(query, batch[half..], grades, cancellationToken).ConfigureAwait(false);
    }
}
