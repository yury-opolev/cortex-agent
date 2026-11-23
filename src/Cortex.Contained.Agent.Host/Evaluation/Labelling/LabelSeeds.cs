using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Agent.Host.Memory.Evaluation;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>A stored message offered to the history seed sampler (raw text; never written out).</summary>
public sealed record SourceMessage(long Id, string ChannelId, string Role, DateTimeOffset Timestamp, string Content);

/// <summary>
/// Deterministic, stratified seed samples for the query writer (§4.2). History: strata of channel × month × length,
/// round-robin across strata in hash order, each message assigned the next category of the quota that fits it
/// (identifier only for identifier-like text, enumeration only with ≥ 2 nearby messages of the same channel).
/// Memory: <see cref="MemoryQuerySeeds"/> (stratified by tag), with extra memories for <c>multi</c> and a topic hint
/// for abstention. Real recall requests (the co-primary stratum) take up to <c>realShare</c> of the seeds, spread
/// over months.
/// </summary>
public static class LabelSeeds
{
    /// <summary>Shortest message (characters) a history query is written from.</summary>
    public const int MinContentChars = 40;

    /// <summary>History category shares of the synthetic seeds (the rest goes to <c>lookup</c>).</summary>
    public static IReadOnlyList<(string Category, double Share)> HistoryQuotas { get; } =
    [
        ("lookup", 0.20), ("paraphrase", 0.20), ("cross_language", 0.10), ("temporal", 0.15),
        ("identifier", 0.10), ("enumeration", 0.15), ("abstention", 0.10),
    ];

    private static readonly TimeSpan EnumerationWindow = TimeSpan.FromDays(3);

    /// <summary>History seeds: <paramref name="count"/> in total, real requests first (up to the share), then synthetic.</summary>
    public static IReadOnlyList<LabelSeed> History(
        IReadOnlyList<SourceMessage> messages, IReadOnlyList<LabelSeed> real, int count, double realShare, int seed = 20261121)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(real);
        var chosenReal = TakeReal(real, (int)Math.Floor(count * realShare), seed);
        var synthetic = count - chosenReal.Count;
        var remaining = HistoryQuotas.ToDictionary(q => q.Category, q => (int)Math.Floor(q.Share * synthetic), StringComparer.Ordinal);
        remaining["lookup"] += synthetic - remaining.Values.Sum();

        var eligible = messages.Where(IsEligible).ToList();
        var byChannel = eligible.GroupBy(m => m.ChannelId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Timestamp).ThenBy(m => m.Id).ToList(), StringComparer.Ordinal);
        var used = new HashSet<long>();
        var seeds = new List<LabelSeed>(chosenReal);
        var index = 0;
        foreach (var message in RoundRobin(eligible, seed))
        {
            if (remaining.Values.Sum() == 0)
            {
                break;
            }

            if (used.Contains(message.Id))
            {
                continue;
            }

            var window = Window(byChannel[message.ChannelId], message, used);
            var category = HistoryQuotas.Select((q, i) => HistoryQuotas[(i + index) % HistoryQuotas.Count].Category)
                .FirstOrDefault(c => remaining[c] > 0 && c switch
                {
                    "identifier" => QueryIntent.HasExactIntent(message.Content),
                    "enumeration" => window.Count >= 3,
                    _ => true,
                });
            if (category is null)
            {
                continue;
            }

            var sources = category == "enumeration" ? window : [message];
            foreach (var source in sources)
            {
                used.Add(source.Id);
            }

            remaining[category]--;
            index++;
            seeds.Add(new LabelSeed
            {
                SeedId = string.Empty,
                Category = category,
                SourceIds = sources.Select(s => s.Id.ToString(CultureInfo.InvariantCulture)).ToList(),
                SourceIsGold = category != "abstention",
            });
        }

        return Number(seeds);
    }

    /// <summary>Memory seeds: real requests first (up to the share), then <see cref="MemoryQuerySeeds"/> for the rest.</summary>
    public static IReadOnlyList<LabelSeed> Memory(
        IReadOnlyList<MemorySeedSource> memories, IReadOnlyList<LabelSeed> real, int count, double realShare, int seed = 20261121)
    {
        ArgumentNullException.ThrowIfNull(memories);
        ArgumentNullException.ThrowIfNull(real);
        var chosenReal = TakeReal(real, (int)Math.Floor(count * realShare), seed);
        var seeds = new List<LabelSeed>(chosenReal);
        var synthetic = count - chosenReal.Count;
        if (synthetic > 0 && memories.Count > 0)
        {
            var hints = memories.OrderBy(m => Rank(seed + 1, m.MemoryId), StringComparer.Ordinal).ToList();
            var hint = 0;
            foreach (var picked in MemoryQuerySeeds.Select(memories, synthetic, seed))
            {
                IReadOnlyList<string> sources = picked.Category switch
                {
                    "abstention" => [hints[hint++ % hints.Count].MemoryId],
                    "multi" => Related(memories, picked, seed),
                    _ => [picked.MemoryId!],
                };
                seeds.Add(new LabelSeed { SeedId = string.Empty, Category = picked.Category, SourceIds = sources, SourceIsGold = picked.Category != "abstention" });
            }
        }

        return Number(seeds);
    }

    /// <summary>Up to <paramref name="max"/> real requests, round-robin across months in hash order.</summary>
    public static IReadOnlyList<LabelSeed> TakeReal(IReadOnlyList<LabelSeed> real, int max, int seed = 20261121)
    {
        ArgumentNullException.ThrowIfNull(real);
        var months = real
            .GroupBy(r => r.RequestTime?.ToString("yyyy-MM", CultureInfo.InvariantCulture) ?? string.Empty, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Queue<LabelSeed>(g.OrderBy(r => Rank(seed, r.RequestMessageId ?? string.Empty), StringComparer.Ordinal)))
            .ToList();
        var chosen = new List<LabelSeed>();
        while (chosen.Count < max && months.Any(m => m.Count > 0))
        {
            foreach (var month in months.Where(m => m.Count > 0))
            {
                if (chosen.Count < max)
                {
                    chosen.Add(month.Dequeue() with { Category = "real", Stratum = "real" });
                }
            }
        }

        return chosen;
    }

    /// <summary>True for user and assistant messages long enough to write a query from.</summary>
    public static bool IsEligible(SourceMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Role is "user" or "assistant" && message.Content.Trim().Length >= MinContentChars;
    }

    private static List<SourceMessage> RoundRobin(List<SourceMessage> messages, int seed)
    {
        var strata = messages
            .GroupBy(m => string.Create(CultureInfo.InvariantCulture, $"{m.ChannelId}|{m.Timestamp:yyyy-MM}|{LengthBucket(m.Content)}"), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Queue<SourceMessage>(g.OrderBy(m => Rank(seed, m.Id.ToString(CultureInfo.InvariantCulture)), StringComparer.Ordinal)))
            .ToList();
        var ordered = new List<SourceMessage>(messages.Count);
        while (strata.Any(s => s.Count > 0))
        {
            foreach (var stratum in strata.Where(s => s.Count > 0))
            {
                ordered.Add(stratum.Dequeue());
            }
        }

        return ordered;
    }

    private static int LengthBucket(string content) => content.Length switch
    {
        < 200 => 0,
        < 800 => 1,
        _ => 2,
    };

    // The message and up to four unused messages of its channel within three days, nearest first, in time order.
    private static List<SourceMessage> Window(List<SourceMessage> channel, SourceMessage message, HashSet<long> used) =>
        channel
            .Where(m => m.Id != message.Id && !used.Contains(m.Id) && (m.Timestamp - message.Timestamp).Duration() <= EnumerationWindow)
            .OrderBy(m => (m.Timestamp - message.Timestamp).Duration())
            .Take(4)
            .Append(message)
            .OrderBy(m => m.Timestamp)
            .ThenBy(m => m.Id)
            .ToList();

    private static List<string> Related(IReadOnlyList<MemorySeedSource> memories, MemoryQuerySeed picked, int seed)
    {
        var tag = picked.Tags.Count > 0 ? picked.Tags[0] : null;
        return memories
            .Where(m => m.MemoryId != picked.MemoryId && (tag is null || m.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(m => Rank(seed, picked.MemoryId + "|" + m.MemoryId), StringComparer.Ordinal)
            .Take(3)
            .Select(m => m.MemoryId)
            .Prepend(picked.MemoryId!)
            .ToList();
    }

    private static List<LabelSeed> Number(List<LabelSeed> seeds)
    {
        var width = Math.Max(4, seeds.Count.ToString(CultureInfo.InvariantCulture).Length);
        return seeds.Select((s, i) => s with { SeedId = "s" + (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0') }).ToList();
    }

    private static string Rank(int seed, string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString(CultureInfo.InvariantCulture) + ":" + id)));
}
