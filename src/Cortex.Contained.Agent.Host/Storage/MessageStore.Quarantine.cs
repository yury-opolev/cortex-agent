using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Memory;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>D10 quarantine tables in <c>messages.db</c> (windows are written by the purge transaction).</summary>
public sealed partial class MessageStore : IMemoryQuarantineSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<QuarantineWindow>> ReadQuarantineWindowsAsync(CancellationToken cancellationToken)
    {
        var windows = new List<QuarantineWindow>();
        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT FromTs, ToTs FROM MemoryQuarantineWindows ORDER BY FromTs;";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            windows.Add(new QuarantineWindow(SqliteDateTimeText.Parse(reader.GetString(0)), SqliteDateTimeText.Parse(reader.GetString(1))));
        }

        return windows;
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> ReadKeptMemoryIdsAsync(CancellationToken cancellationToken)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MemoryId FROM MemoryQuarantineDecisions WHERE Decision = 'kept';";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            kept.Add(reader.GetString(0));
        }

        return kept;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QuarantineWindowRecord>> ReadQuarantineWindowRecordsAsync(CancellationToken cancellationToken)
    {
        var records = new List<QuarantineWindowRecord>();
        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ErasureId, FromTs, ToTs, CreatedAt, ChannelId FROM MemoryQuarantineWindows ORDER BY FromTs;";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new QuarantineWindowRecord(
                reader.GetString(0),
                new QuarantineWindow(SqliteDateTimeText.Parse(reader.GetString(1)), SqliteDateTimeText.Parse(reader.GetString(2))),
                SqliteDateTimeText.Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return records;
    }

    /// <inheritdoc />
    public Task RecordKeptMemoriesAsync(IReadOnlyCollection<string> memoryIds, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memoryIds);
        return this.WriteAsync(
            async transaction =>
            {
                foreach (var memoryId in memoryIds)
                {
                    await using var cmd = this.WriterCommand(transaction, """
                        INSERT INTO MemoryQuarantineDecisions (MemoryId, Decision, DecidedAt) VALUES (@id, 'kept', @at)
                        ON CONFLICT(MemoryId) DO UPDATE SET Decision = 'kept', DecidedAt = excluded.DecidedAt;
                        """);
                    cmd.Parameters.AddWithValue("@id", memoryId);
                    cmd.Parameters.AddWithValue("@at", SqliteDateTimeText.Format(at));
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task RecordKeptMemoryAsync(string memoryId, DateTimeOffset at, CancellationToken cancellationToken) =>
        this.RecordKeptMemoriesAsync([memoryId], at, cancellationToken);
}
