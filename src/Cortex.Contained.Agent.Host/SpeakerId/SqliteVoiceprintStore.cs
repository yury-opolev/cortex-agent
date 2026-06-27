namespace Cortex.Contained.Agent.Host.SpeakerId;

using System.Buffers.Binary;
using System.Globalization;
using Cortex.Contained.Speech.SpeakerId;
using Microsoft.Data.Sqlite;

/// <summary>
/// SQLite-backed <see cref="IVoiceprintStore"/>. One database file per agent
/// container; tenants are still keyed by <c>tenant_id</c> so the schema is
/// safe under any future multi-tenant container model.
/// </summary>
public sealed class SqliteVoiceprintStore : IVoiceprintStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;

    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private bool disposed;

    public SqliteVoiceprintStore(string dataPath)
    {
        var dir = Path.Combine(dataPath, "voiceprints");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "voiceprints.db");

        this.connection = new SqliteConnection($"Data Source={dbPath}");
        this.connection.Open();

        ExecuteNonQuery("PRAGMA journal_mode=WAL");
        EnsureSchema();
    }

    public async ValueTask<VoiceprintRecord?> GetAsync(string tenantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await this.writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = this.connection.CreateCommand();
            cmd.CommandText = """
                SELECT state, embedding, embedding_dim, model_id, sample_count,
                       created_at, confirmed_at, declined_at,
                       threshold_override, feature_enabled
                FROM voiceprints
                WHERE tenant_id = $tenantId
                """;
            cmd.Parameters.AddWithValue("$tenantId", tenantId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new VoiceprintRecord(
                TenantId: tenantId,
                State: (VoiceEnrollmentState)reader.GetInt32(0),
                Embedding: ReadEmbedding(reader, 1),
                EmbeddingDim: reader.GetInt32(2),
                ModelId: reader.IsDBNull(3) ? null : reader.GetString(3),
                SampleCount: reader.GetInt32(4),
                CreatedAt: ParseDto(reader.GetString(5)),
                ConfirmedAt: reader.IsDBNull(6) ? null : ParseDto(reader.GetString(6)),
                DeclinedAt: reader.IsDBNull(7) ? null : ParseDto(reader.GetString(7)),
                ThresholdOverride: reader.IsDBNull(8) ? null : (float?)reader.GetDouble(8),
                FeatureEnabled: reader.GetInt32(9) != 0);
        }
        finally
        {
            this.writeLock.Release();
        }
    }

    public async ValueTask UpsertAsync(VoiceprintRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await this.writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = this.connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO voiceprints
                    (tenant_id, state, embedding, embedding_dim, model_id,
                     sample_count, created_at, confirmed_at, declined_at,
                     threshold_override, feature_enabled)
                VALUES
                    ($tenantId, $state, $embedding, $embeddingDim, $modelId,
                     $sampleCount, $createdAt, $confirmedAt, $declinedAt,
                     $thresholdOverride, $featureEnabled)
                """;
            cmd.Parameters.AddWithValue("$tenantId", record.TenantId);
            cmd.Parameters.AddWithValue("$state", (int)record.State);
            cmd.Parameters.AddWithValue("$embedding", record.Embedding is null
                ? (object)DBNull.Value
                : SerialiseEmbedding(record.Embedding));
            cmd.Parameters.AddWithValue("$embeddingDim", record.EmbeddingDim);
            cmd.Parameters.AddWithValue("$modelId", record.ModelId ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$sampleCount", record.SampleCount);
            cmd.Parameters.AddWithValue("$createdAt", FormatDto(record.CreatedAt));
            cmd.Parameters.AddWithValue("$confirmedAt", record.ConfirmedAt is null
                ? (object)DBNull.Value
                : FormatDto(record.ConfirmedAt.Value));
            cmd.Parameters.AddWithValue("$declinedAt", record.DeclinedAt is null
                ? (object)DBNull.Value
                : FormatDto(record.DeclinedAt.Value));
            cmd.Parameters.AddWithValue("$thresholdOverride", record.ThresholdOverride is null
                ? (object)DBNull.Value
                : (double)record.ThresholdOverride.Value);
            cmd.Parameters.AddWithValue("$featureEnabled", record.FeatureEnabled ? 1 : 0);

            cmd.ExecuteNonQuery();
        }
        finally
        {
            this.writeLock.Release();
        }
    }

    public async ValueTask DeleteAsync(string tenantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await this.writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = this.connection.CreateCommand();
            cmd.CommandText = "DELETE FROM voiceprints WHERE tenant_id = $tenantId";
            cmd.Parameters.AddWithValue("$tenantId", tenantId);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            this.writeLock.Release();
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }
        this.disposed = true;
        this.connection.Dispose();
        this.writeLock.Dispose();
    }

    private void EnsureSchema()
    {
        if (GetSchemaVersion() >= CurrentSchemaVersion)
        {
            return;
        }

        ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS voiceprints (
                tenant_id          TEXT PRIMARY KEY,
                state              INTEGER NOT NULL,
                embedding          BLOB,
                embedding_dim      INTEGER NOT NULL DEFAULT 0,
                model_id           TEXT,
                sample_count       INTEGER NOT NULL DEFAULT 0,
                created_at         TEXT NOT NULL,
                confirmed_at       TEXT,
                declined_at        TEXT,
                threshold_override REAL,
                feature_enabled    INTEGER NOT NULL DEFAULT 1
            )
            """);

        ExecuteNonQuery(string.Create(
            CultureInfo.InvariantCulture,
            $"PRAGMA user_version = {CurrentSchemaVersion}"));
    }

    private int GetSchemaVersion()
    {
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void ExecuteNonQuery(string sql)
    {
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static byte[] SerialiseEmbedding(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        for (var i = 0; i < embedding.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), embedding[i]);
        }
        return bytes;
    }

    private static float[]? ReadEmbedding(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }
        var bytes = (byte[])reader.GetValue(ordinal);
        if (bytes.Length == 0 || (bytes.Length % sizeof(float)) != 0)
        {
            return null;
        }
        var floats = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < floats.Length; i++)
        {
            floats[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)));
        }
        return floats;
    }

    private static string FormatDto(DateTimeOffset dto)
        => dto.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDto(string text)
        => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
