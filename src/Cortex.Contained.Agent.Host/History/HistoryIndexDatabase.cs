using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// One index generation file (<c>history-index.&lt;gen&gt;.db</c>, §9.1): a contentless FTS5 table
/// with <c>secure-delete</c>, the segment metadata table and <c>projector_state</c>. Holds the
/// generation's single writer connection; queries use <see cref="OpenReader"/>.
/// Not thread-safe: the projector serializes writers.
/// </summary>
public sealed partial class HistoryIndexDatabase : IDisposable
{
    private readonly SqliteConnection writer;
    private readonly string? password;
    private readonly ILogger? logger;
    private bool disposed;

    private HistoryIndexDatabase(string path, SqliteConnection writer, string? password, ILogger? logger)
    {
        this.Path = path;
        this.writer = writer;
        this.password = password;
        this.logger = logger;
    }

    /// <summary>A reader held longer than this logs a warning when it is disposed (see <see cref="OpenReader"/>).</summary>
    internal TimeSpan ReaderWarnAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Full path of the generation file.</summary>
    public string Path { get; }

    /// <summary>
    /// Creates a new generation file with the given initial state, encrypted when <paramref name="password"/> is set.
    /// The file is built under a temporary name (schema and state in one transaction) and renamed into place,
    /// so a crash never leaves a state-less file under the generation name.
    /// </summary>
    public static HistoryIndexDatabase Create(string path, ProjectorState state, string? password = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var temp = path + CreatingSuffix;
        DeleteWithSidecars(temp);
        try
        {
            using (var creator = OpenWriter(temp, SqliteOpenMode.ReadWriteCreate, password))
            {
                WriteSchemaAndState(creator, state);
                using var checkpoint = creator.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
            }

            DeleteSidecars(temp);
            File.Move(temp, path, overwrite: false);
        }
        catch
        {
            DeleteWithSidecars(temp);
            throw;
        }

        return Open(path, password, logger);
    }

    /// <summary>Suffix of a generation file that is still being created (never a servable generation).</summary>
    internal const string CreatingSuffix = ".creating";

    /// <summary>Unlinks <paramref name="path"/> and its SQLite sidecar files, ignoring missing ones.</summary>
    internal static void DeleteWithSidecars(string path)
    {
        File.Delete(path);
        DeleteSidecars(path);
    }

    private static void DeleteSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            File.Delete(path + suffix);
        }
    }

    private static void WriteSchemaAndState(SqliteConnection connection, ProjectorState state)
    {
        using var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            CREATE TABLE segments (
              SegId        INTEGER PRIMARY KEY,
              ChannelId    TEXT NOT NULL,
              StartTs      TEXT NOT NULL, EndTs TEXT NOT NULL,
              LocalDate    TEXT NOT NULL,
              FirstMsgId   INTEGER NOT NULL, LastMsgId INTEGER NOT NULL,
              MsgIds       TEXT NOT NULL,
              MsgCount     INTEGER NOT NULL, Words INTEGER NOT NULL,
              HasInternal  INTEGER NOT NULL, HasRecallEcho INTEGER NOT NULL,
              Fingerprint  TEXT NOT NULL,
              UNIQUE(ChannelId, StartTs, FirstMsgId)
            );
            CREATE INDEX ix_seg_channel_time ON segments(ChannelId, StartTs);
            CREATE VIRTUAL TABLE segments_fts USING fts5(
              body, stem,
              content='', contentless_delete=1,
              tokenize='{HistoryConfigFingerprint.TokenizerSpec}',
              prefix='{HistoryConfigFingerprint.PrefixSpec}'
            );
            INSERT INTO segments_fts(segments_fts, rank) VALUES('secure-delete', 1);
            CREATE VIRTUAL TABLE segments_vocab USING fts5vocab(segments_fts, 'row');
            CREATE VIRTUAL TABLE segments_vocab_col USING fts5vocab(segments_fts, 'col');
            CREATE VIRTUAL TABLE segments_tri USING fts5(
              body,
              content='', contentless_delete=1,
              tokenize='{HistoryConfigFingerprint.TrigramTokenizerSpec}'
            );
            INSERT INTO segments_tri(segments_tri, rank) VALUES('secure-delete', 1);
            CREATE TABLE chunks (
              ChunkId        INTEGER PRIMARY KEY,
              SegId          INTEGER NOT NULL,
              ChannelId      TEXT NOT NULL,
              SegFirstMsgId  INTEGER NOT NULL,
              Ordinal        INTEGER NOT NULL,
              MsgIds         TEXT NOT NULL,
              Words          INTEGER NOT NULL,
              AllInternal    INTEGER NOT NULL,
              TextHash       TEXT NOT NULL,
              IsOpen         INTEGER NOT NULL,
              QueuedAt       TEXT NULL,
              QueuedMsgCount INTEGER NOT NULL DEFAULT 0,
              Attempts       INTEGER NOT NULL DEFAULT 0,
              Vec            BLOB NULL,
              UNIQUE(SegId, Ordinal)
            );
            CREATE INDEX ix_chunks_seg ON chunks(SegId);
            CREATE INDEX ix_chunks_hash ON chunks(TextHash);
            -- Vec is a key column only so the partial-index predicate does not force a lookup into the vector rows.
            CREATE INDEX ix_chunks_unembedded ON chunks(Attempts, QueuedAt, Vec) WHERE Vec IS NULL;
            CREATE TABLE chunk_q8 (
              ChunkId INTEGER PRIMARY KEY,
              Q8      BLOB NOT NULL
            );
            CREATE TRIGGER chunks_q8_cleanup AFTER DELETE ON chunks
            BEGIN
              DELETE FROM chunk_q8 WHERE ChunkId = OLD.ChunkId;
            END;
            CREATE TABLE projector_state (
              Id INTEGER PRIMARY KEY CHECK (Id=1),
              Generation INTEGER NOT NULL,
              CheckpointJournalSeq INTEGER NOT NULL,
              SourceInstanceId TEXT NOT NULL, SourceRestoreGeneration INTEGER NOT NULL,
              ConfigFingerprint TEXT NOT NULL,
              BuildComplete INTEGER NOT NULL,
              CoverageJson TEXT NOT NULL,
              AppliedPurgeGeneration INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE build_progress (ChannelId TEXT PRIMARY KEY);
            INSERT INTO projector_state VALUES (1, @gen, @seq, @instance, @restore, @fingerprint, @complete, @coverage, @applied);
            """;
        cmd.Parameters.AddWithValue("@gen", state.Generation);
        cmd.Parameters.AddWithValue("@seq", state.CheckpointJournalSeq);
        cmd.Parameters.AddWithValue("@instance", state.SourceInstanceId);
        cmd.Parameters.AddWithValue("@restore", state.SourceRestoreGeneration);
        cmd.Parameters.AddWithValue("@fingerprint", state.ConfigFingerprint);
        cmd.Parameters.AddWithValue("@complete", state.BuildComplete ? 1 : 0);
        cmd.Parameters.AddWithValue("@coverage", state.CoverageJson);
        cmd.Parameters.AddWithValue("@applied", state.AppliedPurgeGeneration);
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>Opens an existing generation file (throws <see cref="SqliteException"/> code 26 when the key does not fit).</summary>
    public static HistoryIndexDatabase Open(string path, string? password = null, ILogger? logger = null) =>
        new(path, OpenWriter(path, SqliteOpenMode.ReadWrite, password), password, logger);

    /// <summary>Reads <c>projector_state</c>.</summary>
    public ProjectorState ReadState() => ReadState(this.writer);

    /// <summary>Channels already written by an in-progress build.</summary>
    public IReadOnlySet<string> ReadBuildProgress()
    {
        var channels = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = this.writer.CreateCommand();
        cmd.CommandText = "SELECT ChannelId FROM build_progress;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            channels.Add(reader.GetString(0));
        }

        return channels;
    }

    /// <summary>Channels that currently have segments.</summary>
    public IReadOnlyList<string> ReadIndexedChannels()
    {
        var channels = new List<string>();
        using var cmd = this.writer.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT ChannelId FROM segments ORDER BY ChannelId;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            channels.Add(reader.GetString(0));
        }

        return channels;
    }

    /// <summary>Applies <paramref name="batch"/> in one index transaction.</summary>
    public void Apply(IndexBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ObjectDisposedException.ThrowIf(this.disposed, this);
        using var transaction = this.writer.BeginTransaction();

        foreach (var (channelId, segments) in batch.Channels)
        {
            // Vectors of unchanged chunk texts and the debounce state of pending ones survive the replacement.
            var chunkState = this.ReadChannelChunkState(transaction, channelId);
            this.Execute(transaction, "DELETE FROM chunks WHERE ChannelId = @c;", ("@c", channelId));
            this.Execute(transaction, "DELETE FROM segments_fts WHERE rowid IN (SELECT SegId FROM segments WHERE ChannelId = @c);", ("@c", channelId));
            this.Execute(transaction, "DELETE FROM segments_tri WHERE rowid IN (SELECT SegId FROM segments WHERE ChannelId = @c);", ("@c", channelId));
            this.Execute(transaction, "DELETE FROM segments WHERE ChannelId = @c;", ("@c", channelId));
            for (var i = 0; i < segments.Count; i++)
            {
                var segId = this.Insert(transaction, segments[i]);
                this.InsertChunks(transaction, segId, segments[i], isOpen: i == segments.Count - 1, chunkState, batch);
            }
        }

        foreach (var channelId in batch.BuildProgress)
        {
            this.Execute(transaction, "INSERT OR IGNORE INTO build_progress (ChannelId) VALUES (@c);", ("@c", channelId));
        }

        if (batch.MarkBuildComplete)
        {
            this.Execute(transaction, "DELETE FROM build_progress;");
        }

        var coverage = JsonSerializer.Serialize(this.ReadCoverage(transaction));
        this.Execute(
            transaction,
            "UPDATE projector_state SET CoverageJson = @coverage, CheckpointJournalSeq = COALESCE(@seq, CheckpointJournalSeq), BuildComplete = MAX(BuildComplete, @complete), AppliedPurgeGeneration = MAX(AppliedPurgeGeneration, COALESCE(@applied, 0));",
            ("@coverage", coverage),
            ("@seq", batch.CheckpointSeq is { } seq ? seq : DBNull.Value),
            ("@complete", batch.MarkBuildComplete ? 1 : 0),
            ("@applied", batch.AppliedPurgeGeneration is { } applied ? applied : DBNull.Value));

        transaction.Commit();
    }

    /// <summary>Merges FTS b-trees (also purges secure-deleted entries further).</summary>
    public void Optimize() => this.Execute(null, "INSERT INTO segments_fts(segments_fts) VALUES('optimize');");

    /// <summary>Runs <c>PRAGMA wal_checkpoint(TRUNCATE)</c>.</summary>
    public WalCheckpointResult CheckpointTruncate()
    {
        using var cmd = this.writer.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new WalCheckpointResult(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    /// <summary>Runs <c>VACUUM</c>.</summary>
    public void Vacuum() => this.Execute(null, "VACUUM;");

    /// <summary>
    /// Opens a read-only, unpooled connection for queries (no handle outlives disposal).
    /// <para>
    /// Invariant: the reader holds one WAL read transaction until it is disposed, which pins the WAL
    /// against checkpointing (and so against the erasure step's <c>wal_checkpoint(TRUNCATE)</c>). Use it
    /// for a single request under the <see cref="HistoryAccessGate"/> read lease and never cache or store it.
    /// A reader held longer than <see cref="ReaderWarnAfter"/> logs a warning when disposed.
    /// </para>
    /// </summary>
    public HistoryIndexReader OpenReader()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = this.Path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            Password = this.password,
        }.ToString());
        connection.Open();
        try
        {
            // One read transaction for the reader's whole life: state, keyword, dense scan and detail reads
            // see a single WAL snapshot even while the projector re-projects chunk and segment rows.
            using var begin = connection.CreateCommand();
            begin.CommandText = "BEGIN; SELECT 1 FROM projector_state LIMIT 1;";
            begin.ExecuteNonQuery();
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return new HistoryIndexReader(connection, this.ReaderWarnAfter, held => this.LogLongLivedReader(held.TotalSeconds));
    }

    private void LogLongLivedReader(double seconds)
    {
        if (this.logger is { } log)
        {
            LogLongLivedReader(log, seconds, System.IO.Path.GetFileName(this.Path));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A history index reader on {File} was held for {Seconds:F1}s; it pins the WAL snapshot and blocks checkpoints until disposed")]
    private static partial void LogLongLivedReader(ILogger logger, double seconds, string file);

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.writer.Dispose();
    }

    internal static ProjectorState ReadState(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Generation, CheckpointJournalSeq, SourceInstanceId, SourceRestoreGeneration, ConfigFingerprint, BuildComplete, CoverageJson, AppliedPurgeGeneration FROM projector_state WHERE Id = 1;";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new ProjectorState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetInt64(5) == 1,
            reader.GetString(6))
        {
            AppliedPurgeGeneration = reader.GetInt64(7),
        };
    }

    private static SqliteConnection OpenWriter(string path, SqliteOpenMode mode, string? password)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            Password = password,
        }.ToString());
        try
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA secure_delete=ON;";
            cmd.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            // A wrong key (or a plaintext file opened with one) must not leave a handle that blocks the unlink.
            connection.Dispose();
            throw;
        }
    }

    private long Insert(SqliteTransaction transaction, HistorySegment segment)
    {
        using var cmd = this.writer.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO segments (ChannelId, StartTs, EndTs, LocalDate, FirstMsgId, LastMsgId, MsgIds, MsgCount, Words, HasInternal, HasRecallEcho, Fingerprint)
            VALUES (@channel, @start, @end, @date, @first, @last, @ids, @count, @words, @internal, @echo, @fingerprint)
            RETURNING SegId;
            """;
        cmd.Parameters.AddWithValue("@channel", segment.ChannelId);
        cmd.Parameters.AddWithValue("@start", segment.StartTs);
        cmd.Parameters.AddWithValue("@end", segment.EndTs);
        cmd.Parameters.AddWithValue("@date", segment.LocalDate);
        cmd.Parameters.AddWithValue("@first", segment.FirstMsgId);
        cmd.Parameters.AddWithValue("@last", segment.LastMsgId);
        cmd.Parameters.AddWithValue("@ids", JsonSerializer.Serialize(segment.MsgIds));
        cmd.Parameters.AddWithValue("@count", segment.MsgCount);
        cmd.Parameters.AddWithValue("@words", segment.Words);
        cmd.Parameters.AddWithValue("@internal", segment.HasInternal ? 1 : 0);
        cmd.Parameters.AddWithValue("@echo", segment.HasRecallEcho ? 1 : 0);
        cmd.Parameters.AddWithValue("@fingerprint", segment.Fingerprint);
        var segId = (long)cmd.ExecuteScalar()!;

        this.Execute(
            transaction,
            "INSERT INTO segments_fts(rowid, body, stem) VALUES (@id, @body, @stem);",
            ("@id", segId),
            ("@body", segment.Body),
            ("@stem", (object?)segment.StemBody ?? DBNull.Value));
        if (segment.TrigramBody is { } trigram)
        {
            this.Execute(transaction, "INSERT INTO segments_tri(rowid, body) VALUES (@id, @body);", ("@id", segId), ("@body", trigram));
        }

        return segId;
    }

    private List<ChannelCoverage> ReadCoverage(SqliteTransaction transaction)
    {
        var coverage = new List<ChannelCoverage>();
        using var cmd = this.writer.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT ChannelId, MIN(StartTs), MAX(EndTs) FROM segments GROUP BY ChannelId ORDER BY ChannelId;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            coverage.Add(new ChannelCoverage(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return coverage;
    }

    private void Execute(SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = this.writer.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        cmd.ExecuteNonQuery();
    }
}

/// <summary>Read-only queries over one generation file.</summary>
public sealed partial class HistoryIndexReader : IDisposable
{
    private const string SegmentColumns =
        "s.SegId, s.ChannelId, s.StartTs, s.EndTs, s.LocalDate, s.FirstMsgId, s.LastMsgId, s.MsgIds, s.HasInternal, s.HasRecallEcho, s.Fingerprint";

    private readonly SqliteConnection connection;
    private readonly TimeSpan warnAfter;
    private readonly Action<TimeSpan>? onLongLived;
    private readonly long openedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    private ChunkCounts? chunkCounts;
    private bool disposed;

    internal HistoryIndexReader(SqliteConnection connection, TimeSpan warnAfter = default, Action<TimeSpan>? onLongLived = null)
    {
        this.connection = connection;
        this.warnAfter = warnAfter;
        this.onLongLived = onLongLived;
    }

    /// <summary>Reads <c>projector_state</c>.</summary>
    public ProjectorState ReadState() => HistoryIndexDatabase.ReadState(this.connection);

    /// <summary>Per-channel indexed ranges from <c>CoverageJson</c>.</summary>
    public IReadOnlyList<ChannelCoverage> ReadCoverage() =>
        JsonSerializer.Deserialize<List<ChannelCoverage>>(this.ReadState().CoverageJson) ?? [];

    /// <summary>Number of segments containing <paramref name="term"/> (from <c>segments_vocab</c>).</summary>
    public long DocumentFrequency(string term)
    {
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = "SELECT doc FROM segments_vocab_col WHERE term = @term AND col = 'body';";
        cmd.Parameters.AddWithValue("@term", term);
        return cmd.ExecuteScalar() is long doc ? doc : 0;
    }

    /// <summary>Total number of segments.</summary>
    public long SegmentCount()
    {
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM segments;";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Segments matching <paramref name="match"/> by BM25 (best first).</summary>
    public IReadOnlyList<IndexedSegment> SearchRelevance(string match, SegmentFilter filter, int limit, int offset) =>
        this.SearchRelevance(match, filter, limit, offset, HistoryKeywordVariant.Plain);

    /// <summary>Segments by id (dense candidates); ids that no longer exist are skipped.</summary>
    public IReadOnlyList<IndexedSegment> ReadSegmentsById(IReadOnlyCollection<long> segIds)
    {
        ArgumentNullException.ThrowIfNull(segIds);
        var segments = new List<IndexedSegment>();
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = $"SELECT {SegmentColumns}, 0.0 FROM segments s WHERE s.SegId = @id;";
        var parameter = cmd.Parameters.Add("@id", SqliteType.Integer);
        foreach (var id in segIds.Distinct())
        {
            parameter.Value = id;
            segments.AddRange(Read(cmd));
        }

        return segments;
    }

    /// <summary>Segments matching <paramref name="match"/> in the keyword table of <paramref name="table"/>, by BM25.</summary>
    public IReadOnlyList<IndexedSegment> SearchRelevance(string match, SegmentFilter filter, int limit, int offset, HistoryKeywordVariant table)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var fts = table == HistoryKeywordVariant.Trigram ? "segments_tri" : "segments_fts";
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SegmentColumns}, bm25({fts}) AS score
            FROM {fts} JOIN segments s ON s.SegId = {fts}.rowid
            WHERE {fts} MATCH @match
              AND (@channel IS NULL OR s.ChannelId = @channel)
              AND (@since IS NULL OR s.EndTs >= @since)
              AND (@until IS NULL OR s.StartTs < @until)
            ORDER BY score, s.SegId
            LIMIT @limit OFFSET @offset;
            """;
        Bind(cmd, match, filter);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);
        return Read(cmd);
    }

    /// <summary>Segments matching <paramref name="match"/> ordered by <c>(StartTs, SegId)</c>, after a keyset position.</summary>
    public IReadOnlyList<IndexedSegment> SearchTimeOrder(string match, SegmentFilter filter, int limit, (string StartTs, long SegId)? after)
    {
        ArgumentNullException.ThrowIfNull(filter);
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SegmentColumns}, 0.0 AS score
            FROM segments_fts JOIN segments s ON s.SegId = segments_fts.rowid
            WHERE segments_fts MATCH @match
              AND (@channel IS NULL OR s.ChannelId = @channel)
              AND (@since IS NULL OR s.EndTs >= @since)
              AND (@until IS NULL OR s.StartTs < @until)
              AND (@afterTs IS NULL OR s.StartTs > @afterTs OR (s.StartTs = @afterTs AND s.SegId > @afterId))
            ORDER BY s.StartTs, s.SegId
            LIMIT @limit;
            """;
        Bind(cmd, match, filter);
        cmd.Parameters.AddWithValue("@afterTs", after is { } a ? a.StartTs : DBNull.Value);
        cmd.Parameters.AddWithValue("@afterId", after is { } b ? b.SegId : 0L);
        cmd.Parameters.AddWithValue("@limit", limit);
        return Read(cmd);
    }

    /// <summary>All segments, optionally of one channel, ordered by channel and time (tests and diagnostics).</summary>
    public IReadOnlyList<IndexedSegment> ReadSegments(string? channelId = null)
    {
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = $"SELECT {SegmentColumns}, 0.0 FROM segments s WHERE @channel IS NULL OR s.ChannelId = @channel ORDER BY s.ChannelId, s.StartTs, s.FirstMsgId;";
        cmd.Parameters.AddWithValue("@channel", (object?)channelId ?? DBNull.Value);
        return Read(cmd);
    }

    /// <summary>
    /// Every posting as <c>(term, channel, first message id)</c> from an <c>fts5vocab</c> instance
    /// table — segment-id independent, so incremental and rebuilt indexes compare equal.
    /// </summary>
    public IReadOnlyList<string> ReadPostings()
    {
        using (var create = this.connection.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE IF NOT EXISTS temp.segments_instances USING fts5vocab(main, segments_fts, instance);";
            create.ExecuteNonQuery();
        }

        var postings = new List<string>();
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = """
            SELECT i.term, s.ChannelId, s.FirstMsgId, i.offset
            FROM temp.segments_instances i JOIN segments s ON s.SegId = i.doc
            ORDER BY s.ChannelId, s.FirstMsgId, i.term, i.offset;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            postings.Add(string.Create(CultureInfo.InvariantCulture, $"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetInt64(2)}|{reader.GetInt64(3)}"));
        }

        return postings;
    }

    /// <summary>Distinct indexed terms (row vocabulary).</summary>
    public IReadOnlyList<string> ReadTerms()
    {
        var terms = new List<string>();
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = "SELECT term FROM segments_vocab ORDER BY term;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            terms.Add(reader.GetString(0));
        }

        return terms;
    }

    /// <summary>Message and word count of every segment (smoke-evaluation histogram).</summary>
    public IReadOnlyList<(int Messages, int Words)> ReadSegmentSizes()
    {
        var sizes = new List<(int, int)>();
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = "SELECT MsgCount, Words FROM segments;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            sizes.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }

        return sizes;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.connection.Dispose();
        var held = System.Diagnostics.Stopwatch.GetElapsedTime(this.openedAt);
        if (this.onLongLived is not null && held > this.warnAfter)
        {
            this.onLongLived(held);
        }
    }

    private static void Bind(SqliteCommand cmd, string match, SegmentFilter filter)
    {
        cmd.Parameters.AddWithValue("@match", match);
        cmd.Parameters.AddWithValue("@channel", (object?)filter.ChannelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@since", (object?)filter.Since ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@until", (object?)filter.Until ?? DBNull.Value);
    }

    private static List<IndexedSegment> Read(SqliteCommand cmd)
    {
        var segments = new List<IndexedSegment>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            segments.Add(new IndexedSegment(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                JsonSerializer.Deserialize<List<long>>(reader.GetString(7)) ?? [],
                reader.GetInt64(8) == 1,
                reader.GetInt64(9) == 1,
                reader.GetString(10),
                reader.GetDouble(11)));
        }

        return segments;
    }
}
