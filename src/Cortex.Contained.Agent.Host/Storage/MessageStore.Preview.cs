using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>Read-only purge preview: what a purge of a scope would delete (Recall page confirmation).</summary>
public sealed partial class MessageStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DateTimeOffset>> ReadPurgeTimestampsAsync(HistoryPurgeScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var (where, bind) = PurgeFilter(scope, "Timestamp");
        var timestamps = new List<DateTimeOffset>();
        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT Timestamp FROM Messages WHERE {where} ORDER BY Timestamp;";
        bind(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            timestamps.Add(SqliteDateTimeText.Parse(reader.GetString(0)));
        }

        return timestamps;
    }
}
