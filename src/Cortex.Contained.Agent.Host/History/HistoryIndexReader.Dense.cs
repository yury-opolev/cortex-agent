using System.Text.Json;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>Filters applied inside the dense scan, before any vector is scored (D8).</summary>
/// <param name="ChannelId">Only this channel, or null.</param>
/// <param name="Since">Segments ending at or after this canonical UTC instant, or null.</param>
/// <param name="Until">Segments starting before this canonical UTC instant, or null.</param>
/// <param name="IncludeInternal">Include chunks made only of Internal rows (D9).</param>
public sealed record DenseFilter(string? ChannelId, string? Since, string? Until, bool IncludeInternal);

/// <summary>One scored chunk.</summary>
public sealed record DenseHit(long ChunkId, long SegId, int Ordinal, IReadOnlyList<long> MsgIds, float Score);

/// <summary>
/// Exact dense search (D8, scale-path record): the SQL filter selects the candidate chunks, then every
/// candidate is scored by brute force, so recall is exact under any filter. Above
/// <c>int8Threshold</c> embedded chunks the scan reads the int8 copy and rescores the best
/// <c>k × oversample</c> candidates with the float32 vectors.
/// </summary>
public sealed partial class HistoryIndexReader
{
    /// <summary>Returns the <paramref name="k"/> chunks most similar to <paramref name="query"/>, best first.</summary>
    public IReadOnlyList<DenseHit> SearchDense(float[] query, DenseFilter filter, int k, int int8Threshold, int oversample)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        if (k <= 0)
        {
            return [];
        }

        var unit = VectorMath.Normalize(query);
        var useInt8 = this.ReadChunkCounts().Embedded > int8Threshold;
        var keep = useInt8 ? k * Math.Max(1, oversample) : k;
        var best = new PriorityQueue<(long ChunkId, long SegId, int Ordinal), (float Score, long NegChunkId)>();

        using (var cmd = this.connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT c.ChunkId, c.SegId, c.Ordinal, {(useInt8 ? "c.Q8" : "c.Vec")}
                FROM chunks c JOIN segments s ON s.SegId = c.SegId
                WHERE c.Vec IS NOT NULL
                  AND (@channel IS NULL OR s.ChannelId = @channel)
                  AND (@since IS NULL OR s.EndTs >= @since)
                  AND (@until IS NULL OR s.StartTs < @until)
                  AND (@internal = 1 OR c.AllInternal = 0);
                """;
            cmd.Parameters.AddWithValue("@channel", (object?)filter.ChannelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@since", (object?)filter.Since ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@until", (object?)filter.Until ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@internal", filter.IncludeInternal ? 1 : 0);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var blob = (byte[])reader[3];
                var score = useInt8 ? VectorMath.DotInt8(unit, blob) : VectorMath.Dot(unit, VectorMath.FromBlob(blob));
                Offer(best, keep, (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2)), score);
            }
        }

        var candidates = new List<(long ChunkId, long SegId, int Ordinal, float Score)>();
        while (best.TryDequeue(out var item, out var priority))
        {
            candidates.Add((item.ChunkId, item.SegId, item.Ordinal, priority.Score));
        }

        var details = this.ReadChunkDetails(candidates.Select(c => c.ChunkId).ToList(), withVectors: useInt8);
        return candidates
            .Select(c => new DenseHit(c.ChunkId, c.SegId, c.Ordinal, details[c.ChunkId].MsgIds,
                useInt8 ? VectorMath.Dot(unit, details[c.ChunkId].Vector!) : c.Score))
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.ChunkId)
            .Take(k)
            .ToList();
    }

    /// <summary>The stored float32 vectors of the given chunks (MMR similarity between hits).</summary>
    public IReadOnlyDictionary<long, float[]> ReadChunkVectors(IReadOnlyCollection<long> chunkIds)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        return this.ReadChunkDetails(chunkIds, withVectors: true)
            .Where(kv => kv.Value.Vector is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Vector!);
    }

    private static void Offer(
        PriorityQueue<(long ChunkId, long SegId, int Ordinal), (float Score, long NegChunkId)> best,
        int keep,
        (long ChunkId, long SegId, int Ordinal) item,
        float score)
    {
        // Min-heap on (score, -chunkId): the root is the weakest kept candidate; ties prefer lower ids.
        var priority = (score, -item.ChunkId);
        if (best.Count < keep)
        {
            best.Enqueue(item, priority);
            return;
        }

        if (best.TryPeek(out _, out var weakest) && Comparer<(float, long)>.Default.Compare(priority, weakest) > 0)
        {
            best.DequeueEnqueue(item, priority);
        }
    }

    private Dictionary<long, (IReadOnlyList<long> MsgIds, float[]? Vector)> ReadChunkDetails(IReadOnlyCollection<long> chunkIds, bool withVectors)
    {
        var details = new Dictionary<long, (IReadOnlyList<long>, float[]?)>();
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = "SELECT MsgIds, Vec FROM chunks WHERE ChunkId = @id;";
        var parameter = cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Integer);
        foreach (var id in chunkIds)
        {
            parameter.Value = id;
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                details[id] = (
                    JsonSerializer.Deserialize<List<long>>(reader.GetString(0)) ?? [],
                    withVectors && !reader.IsDBNull(1) ? VectorMath.FromBlob((byte[])reader[1]) : null);
            }
        }

        return details;
    }
}
