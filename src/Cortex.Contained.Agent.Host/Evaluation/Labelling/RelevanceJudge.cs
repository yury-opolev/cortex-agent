using System.Globalization;
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
    private const string Rubric = """
        For each item decide how well it answers the query:
        2 = the item directly answers the query or contains exactly the information asked for;
        1 = the item is related and partly answers it (part of the answer, one of several things asked for, or context needed to answer);
        0 = not relevant.
        Judge every item on its own text only and do not assume information that is not there. Items and queries may be in any language.
        Return ONLY a JSON object {"grades": {"<item id>": 0, 1 or 2}} with every item id exactly once.
        """;

    private readonly LabellingLlm llm;
    private readonly string model;
    private readonly int batchSize;
    private readonly string subject;

    /// <param name="llm">The pinned client.</param>
    /// <param name="model">The judge model.</param>
    /// <param name="batchSize">Candidates per call.</param>
    /// <param name="tool"><c>history-eval</c> (messages) or <c>memory-eval</c> (memories).</param>
    public RelevanceJudge(LabellingLlm llm, string model, int batchSize, string tool)
    {
        this.llm = llm;
        this.model = model;
        this.batchSize = batchSize;
        this.subject = string.Equals(tool, "memory-eval", StringComparison.Ordinal)
            ? "You judge search results over a person's curated memories (notes their assistant stored)."
            : "You judge search results over a person's chat history with their assistant; every item is one stored message.";
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
            this.subject + "\n" + Rubric,
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
