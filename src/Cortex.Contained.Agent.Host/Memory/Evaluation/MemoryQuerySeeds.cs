using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cortex.Contained.Agent.Host.Memory.Evaluation;

/// <summary>One stored memory offered to the query generator.</summary>
public sealed record MemorySeedSource(string MemoryId, string? Title, IReadOnlyList<string> Tags, string Content);

/// <summary>
/// One query-authoring seed: a memory (or none, for abstention) and the category the author should write
/// a query for. Seeds hold raw memory text and stay in the local workspace (D6).
/// </summary>
public sealed record MemoryQuerySeed(string SeedId, string Category, string? MemoryId, string? Title, IReadOnlyList<string> Tags, string? Content);

/// <summary>
/// Generator support for the ≥ 250-query memory dataset: a deterministic sample of memories, stratified by
/// their first tag (round-robin over strata, hash order within one), each with a suggested category that
/// fits it (identifier only for content with an identifier-like token, tag_filtered only for tagged
/// memories), plus about 10% abstention seeds without a memory.
/// </summary>
public static class MemoryQuerySeeds
{
    private static readonly string[] Rotation = ["lookup", "paraphrase", "cross_language", "identifier", "tag_filtered", "multi"];

    /// <summary>Selects <paramref name="count"/> seeds (fewer when the corpus is smaller), stable for a given seed.</summary>
    public static IReadOnlyList<MemoryQuerySeed> Select(IReadOnlyList<MemorySeedSource> memories, int count, int seed = 20261121)
    {
        ArgumentNullException.ThrowIfNull(memories);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var abstentions = count >= 10 ? count / 10 : 0;
        var wanted = Math.Min(count - abstentions, memories.Count);

        var strata = memories
            .GroupBy(m => m.Tags.Count > 0 ? m.Tags[0].ToLowerInvariant() : "untagged", StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Queue<MemorySeedSource>(g.OrderBy(m => Rank(seed, m.MemoryId), StringComparer.Ordinal)))
            .ToList();
        var chosen = new List<MemorySeedSource>();
        while (chosen.Count < wanted)
        {
            foreach (var stratum in strata.Where(s => s.Count > 0))
            {
                if (chosen.Count < wanted)
                {
                    chosen.Add(stratum.Dequeue());
                }
            }
        }

        var width = Math.Max(3, (wanted + abstentions).ToString(CultureInfo.InvariantCulture).Length);
        var seeds = chosen
            .Select((m, i) => new MemoryQuerySeed(Id(i, width), CategoryFor(m, i), m.MemoryId, m.Title, m.Tags, m.Content))
            .ToList();
        for (var i = 0; i < abstentions; i++)
        {
            seeds.Add(new MemoryQuerySeed(Id(wanted + i, width), "abstention", null, null, [], null));
        }

        return seeds;
    }

    private static string Id(int index, int width) => "s" + (index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

    private static string CategoryFor(MemorySeedSource memory, int index)
    {
        for (var step = 0; step < Rotation.Length; step++)
        {
            var category = Rotation[(index + step) % Rotation.Length];
            var fits = category switch
            {
                "identifier" => QueryIntent.HasExactIntent(memory.Content) || QueryIntent.HasExactIntent(memory.Title),
                "tag_filtered" => memory.Tags.Count > 0,
                _ => true,
            };
            if (fits)
            {
                return category;
            }
        }

        return "lookup";
    }

    private static string Rank(int seed, string memoryId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString(CultureInfo.InvariantCulture) + ":" + memoryId)));
}
