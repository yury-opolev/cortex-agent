using System.Text.Json;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>A stored embedding: float32 vector BLOB plus its int8 copy.</summary>
public sealed record StoredVector(byte[] Vec, byte[] Q8);

/// <summary>A chunk waiting for its vector (the embedding job queue is <c>chunks WHERE Vec IS NULL</c>).</summary>
/// <param name="SegmentMsgIds">Members of the whole segment: the chunk text is recomputed from them.</param>
public sealed record PendingChunk(long ChunkId, long SegId, string ChannelId, int Ordinal, string TextHash, IReadOnlyList<long> SegmentMsgIds, bool IsOpen, int Attempts);

/// <summary>Embedding progress of one generation.</summary>
public sealed record ChunkCounts(long Total, long Embedded, long Pending, long Failed, DateTimeOffset? OldestPendingQueuedAt);

/// <summary>A chunk row as stored (tests and diagnostics).</summary>
public sealed record StoredChunk(string ChannelId, long SegFirstMsgId, int Ordinal, IReadOnlyList<long> MsgIds, bool AllInternal, string TextHash, bool IsOpen, bool HasVector);

/// <summary>Chunk and vector storage of a generation (§4.2 increment 3).</summary>
public sealed partial class HistoryIndexDatabase
{
    /// <summary>Embedding attempts after which a chunk is reported as failed and no longer retried.</summary>
    public const int MaxEmbeddingAttempts = 3;

    /// <summary>
    /// Chunks whose vector may be computed now. A channel's last (open) segment waits until
    /// <paramref name="openMessages"/> messages arrived since it was queued or its job is older than
    /// <paramref name="openDelay"/>, so a live conversation is not re-embedded on every message.
    /// </summary>
    public IReadOnlyList<PendingChunk> ReadPendingChunks(int max, DateTimeOffset now, TimeSpan openDelay, int openMessages)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        using var cmd = this.writer.CreateCommand();
        cmd.CommandText = """
            SELECT c.ChunkId, c.SegId, c.ChannelId, c.Ordinal, c.TextHash, s.MsgIds, c.IsOpen, c.Attempts
            FROM chunks c JOIN segments s ON s.SegId = c.SegId
            WHERE c.Vec IS NULL AND c.Attempts < @maxAttempts
              AND (c.IsOpen = 0 OR c.QueuedAt <= @cutoff OR s.MsgCount - c.QueuedMsgCount >= @openMessages)
            ORDER BY c.IsOpen, c.ChunkId
            LIMIT @max;
            """;
        cmd.Parameters.AddWithValue("@maxAttempts", MaxEmbeddingAttempts);
        cmd.Parameters.AddWithValue("@cutoff", SqliteDateTimeText.Format(now - openDelay));
        cmd.Parameters.AddWithValue("@openMessages", openMessages);
        cmd.Parameters.AddWithValue("@max", max);
        var pending = new List<PendingChunk>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            pending.Add(new PendingChunk(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                JsonSerializer.Deserialize<List<long>>(reader.GetString(5)) ?? [],
                reader.GetInt64(6) == 1,
                reader.GetInt32(7)));
        }

        return pending;
    }

    /// <summary>
    /// Stores vectors in one transaction. Each is written only to chunks that still have the same
    /// <c>TextHash</c> (document revision fence) and no vector yet; chunks elsewhere with the same text
    /// share it. Returns the number of chunk rows updated.
    /// </summary>
    public int PublishVectors(IReadOnlyList<(string TextHash, float[] Vector)> vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        ObjectDisposedException.ThrowIf(this.disposed, this);
        using var transaction = this.writer.BeginTransaction();
        var updated = 0;
        foreach (var (hash, vector) in vectors)
        {
            var unit = VectorMath.Normalize(vector);
            using var cmd = this.writer.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "UPDATE chunks SET Vec = @vec, Q8 = @q8, QueuedAt = NULL WHERE TextHash = @hash AND Vec IS NULL;";
            cmd.Parameters.AddWithValue("@vec", VectorMath.ToBlob(unit));
            cmd.Parameters.AddWithValue("@q8", VectorMath.QuantizeInt8(unit));
            cmd.Parameters.AddWithValue("@hash", hash);
            updated += cmd.ExecuteNonQuery();
        }

        transaction.Commit();
        return updated;
    }

    /// <summary>Counts a failed embedding attempt for each chunk.</summary>
    public void RecordEmbeddingFailure(IEnumerable<long> chunkIds)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        using var transaction = this.writer.BeginTransaction();
        foreach (var id in chunkIds)
        {
            this.Execute(transaction, "UPDATE chunks SET Attempts = Attempts + 1 WHERE ChunkId = @id AND Vec IS NULL;", ("@id", id));
        }

        transaction.Commit();
    }

    /// <summary>Embedding progress.</summary>
    public ChunkCounts ReadChunkCounts() => ReadChunkCounts(this.writer);

    internal static ChunkCounts ReadChunkCounts(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*),
                   COALESCE(SUM(Vec IS NOT NULL), 0),
                   COALESCE(SUM(Vec IS NULL AND Attempts < {MaxEmbeddingAttempts}), 0),
                   COALESCE(SUM(Vec IS NULL AND Attempts >= {MaxEmbeddingAttempts}), 0),
                   MIN(CASE WHEN Vec IS NULL THEN QueuedAt END)
            FROM chunks;
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new ChunkCounts(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : SqliteDateTimeText.Parse(reader.GetString(4)));
    }

    // Called by Apply inside the batch transaction, before the channel's segments are deleted.
    private ChannelChunkState ReadChannelChunkState(SqliteTransaction transaction, string channelId)
    {
        var state = new ChannelChunkState();
        using var cmd = this.writer.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT SegFirstMsgId, Ordinal, TextHash, Vec, Q8, QueuedAt, QueuedMsgCount FROM chunks WHERE ChannelId = @c;";
        cmd.Parameters.AddWithValue("@c", channelId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(3))
            {
                state.Vectors[reader.GetString(2)] = new StoredVector((byte[])reader[3], (byte[])reader[4]);
            }
            else if (!reader.IsDBNull(5))
            {
                state.Queued[(reader.GetInt64(0), reader.GetInt32(1))] = (reader.GetString(5), reader.GetInt32(6));
            }
        }

        return state;
    }

    private void InsertChunks(SqliteTransaction transaction, long segId, HistorySegment segment, bool isOpen, ChannelChunkState state, IndexBatch batch)
    {
        foreach (var chunk in segment.Chunks)
        {
            var vector = state.Vectors.GetValueOrDefault(chunk.TextHash);
            var queued = state.Queued.TryGetValue((segment.FirstMsgId, chunk.Ordinal), out var previous)
                ? previous
                : (SqliteDateTimeText.Format(batch.Now ?? DateTimeOffset.UtcNow), segment.MsgCount);
            using var cmd = this.writer.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO chunks (SegId, ChannelId, SegFirstMsgId, Ordinal, MsgIds, Words, AllInternal, TextHash, IsOpen, QueuedAt, QueuedMsgCount, Vec, Q8)
                VALUES (@seg, @channel, @first, @ordinal, @ids, @words, @internal, @hash, @open, @queuedAt, @queuedCount, @vec, @q8);
                """;
            cmd.Parameters.AddWithValue("@seg", segId);
            cmd.Parameters.AddWithValue("@channel", segment.ChannelId);
            cmd.Parameters.AddWithValue("@first", segment.FirstMsgId);
            cmd.Parameters.AddWithValue("@ordinal", chunk.Ordinal);
            cmd.Parameters.AddWithValue("@ids", JsonSerializer.Serialize(chunk.MsgIds));
            cmd.Parameters.AddWithValue("@words", chunk.Words);
            cmd.Parameters.AddWithValue("@internal", chunk.AllInternal ? 1 : 0);
            cmd.Parameters.AddWithValue("@hash", chunk.TextHash);
            cmd.Parameters.AddWithValue("@open", isOpen ? 1 : 0);
            cmd.Parameters.AddWithValue("@queuedAt", vector is null ? queued.Item1 : DBNull.Value);
            cmd.Parameters.AddWithValue("@queuedCount", vector is null ? queued.Item2 : 0);
            cmd.Parameters.AddWithValue("@vec", (object?)vector?.Vec ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@q8", (object?)vector?.Q8 ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private sealed class ChannelChunkState
    {
        public Dictionary<string, StoredVector> Vectors { get; } = new(StringComparer.Ordinal);

        public Dictionary<(long SegFirstMsgId, int Ordinal), (string QueuedAt, int QueuedMsgCount)> Queued { get; } = [];
    }
}

/// <summary>Chunk reads on a query connection.</summary>
public sealed partial class HistoryIndexReader
{
    /// <summary>Embedding progress.</summary>
    public ChunkCounts ReadChunkCounts() => HistoryIndexDatabase.ReadChunkCounts(this.connection);

    /// <summary>All chunks, optionally of one channel, in a segment-id independent order (tests and diagnostics).</summary>
    public IReadOnlyList<StoredChunk> ReadChunks(string? channelId = null)
    {
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = """
            SELECT ChannelId, SegFirstMsgId, Ordinal, MsgIds, AllInternal, TextHash, IsOpen, Vec IS NOT NULL
            FROM chunks WHERE @channel IS NULL OR ChannelId = @channel
            ORDER BY ChannelId, SegFirstMsgId, Ordinal;
            """;
        cmd.Parameters.AddWithValue("@channel", (object?)channelId ?? DBNull.Value);
        var chunks = new List<StoredChunk>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            chunks.Add(new StoredChunk(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                JsonSerializer.Deserialize<List<long>>(reader.GetString(3)) ?? [],
                reader.GetInt64(4) == 1,
                reader.GetString(5),
                reader.GetInt64(6) == 1,
                reader.GetInt64(7) == 1));
        }

        return chunks;
    }

    /// <summary>The stored float32 vector of the first chunk with <paramref name="textHash"/>, or null.</summary>
    public float[]? ReadVector(string textHash)
    {
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = "SELECT Vec FROM chunks WHERE TextHash = @hash AND Vec IS NOT NULL LIMIT 1;";
        cmd.Parameters.AddWithValue("@hash", textHash);
        return cmd.ExecuteScalar() is byte[] blob ? VectorMath.FromBlob(blob) : null;
    }
}
