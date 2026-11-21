namespace Cortex.Contained.Agent.Host.History.Evaluation;

/// <summary>One line returned to the reader (agent) by a system under evaluation.</summary>
public sealed record ReturnedLine(long MessageId, int Words);

/// <summary>One returned hit: the lines shown plus the located segment's member ids.</summary>
public sealed record ReturnedHit(IReadOnlyList<long> MemberIds, IReadOnlyList<ReturnedLine> Lines);

/// <summary>Per-query metrics at one reading budget (§14.2).</summary>
/// <param name="LocatorHit">A gold id is a member of a hit that started within the budget.</param>
/// <param name="ExcerptHit">A gold id is among the lines read within the budget.</param>
/// <param name="LinesToFirstGold">Lines read until the first gold line, or null.</param>
/// <param name="WordsRead">Words read before the budget stopped the reader.</param>
public sealed record BudgetScore(bool LocatorHit, bool ExcerptHit, int? LinesToFirstGold, int WordsRead);

/// <summary>Equal-reading-budget scoring of returned results against gold <c>Messages.Id</c>s.</summary>
public static class ReadingBudgetMetrics
{
    /// <summary>Characters of a line the agent sees (the <c>history_search</c> line cap).</summary>
    public const int LineChars = 200;

    /// <summary>Whitespace word count of the part of <paramref name="text"/> a reader sees.</summary>
    public static int Words(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var visible = text.Length > LineChars ? text[..LineChars] : text;
        return visible.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>Reads <paramref name="hits"/> in order until <paramref name="wordBudget"/> words are used.</summary>
    public static BudgetScore Score(IReadOnlyList<ReturnedHit> hits, IReadOnlySet<long> gold, int wordBudget)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(gold);

        var words = 0;
        var lines = 0;
        var locator = false;
        var excerpt = false;
        int? firstGold = null;
        foreach (var hit in hits)
        {
            if (words >= wordBudget)
            {
                break;
            }

            locator |= hit.MemberIds.Any(gold.Contains);
            foreach (var line in hit.Lines)
            {
                if (words + line.Words > wordBudget)
                {
                    return new BudgetScore(locator, excerpt, firstGold, words);
                }

                words += line.Words;
                lines++;
                if (gold.Contains(line.MessageId))
                {
                    excerpt = true;
                    firstGold ??= lines;
                }
            }
        }

        return new BudgetScore(locator, excerpt, firstGold, words);
    }
}
