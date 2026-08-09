using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Connectors.Security;

/// <summary>
/// Persists all paired connectors in ONE JSON blob under a single secret id in the
/// DPAPI-backed store. A <see cref="Dictionary{TKey,TValue}"/> keyed by channel id is
/// serialised and stored; the whole blob is rewritten on every mutation.
/// </summary>
/// <remarks>
/// <c>SecretManager</c> cannot enumerate its keys, so all records must live in
/// a single blob rather than one secret per connector.
/// All public members are thread-safe and guarded by a <see cref="Lock"/>.
/// </remarks>
// SECURITY: no [LoggerMessage] template in this class may take a token as a parameter.
// A JsonException message can quote the decrypted payload (i.e. token material).
// Only ex.GetType().Name is logged when parsing fails — mirroring McpTokenStore.Get.
public sealed partial class ConnectorTokenStore
{
    /// <summary>The secret id under which the connector registry blob is stored.</summary>
    public const string SecretId = "connectors/registry";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IConnectorSecretStore secretStore;
    private readonly ILogger<ConnectorTokenStore> logger;
    private readonly Lock syncLock = new();

    /// <summary>Initialises a new <see cref="ConnectorTokenStore"/>.</summary>
    public ConnectorTokenStore(IConnectorSecretStore secretStore, ILogger<ConnectorTokenStore> logger)
    {
        this.secretStore = secretStore;
        this.logger = logger;
    }

    /// <summary>Returns all paired connector records.</summary>
    public IReadOnlyList<ConnectorRecord> GetAll()
    {
        lock (this.syncLock)
        {
            return [.. this.LoadRegistry().Values];
        }
    }

    /// <summary>Returns the record for <paramref name="channelId"/>, or null when not found.</summary>
    public ConnectorRecord? Get(string channelId)
    {
        lock (this.syncLock)
        {
            var registry = this.LoadRegistry();
            return registry.TryGetValue(channelId, out var record) ? record : null;
        }
    }

    /// <summary>Saves (inserts or updates) a connector record.</summary>
    public void Save(ConnectorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (this.syncLock)
        {
            var registry = this.LoadRegistry();
            registry[record.ChannelId] = record;
            this.PersistRegistry(registry);
        }

        this.LogSaved(record.ChannelId);
    }

    /// <summary>Removes the record for <paramref name="channelId"/>. Returns true when a record was removed.</summary>
    public bool Remove(string channelId)
    {
        bool removed;
        lock (this.syncLock)
        {
            var registry = this.LoadRegistry();
            removed = registry.Remove(channelId);
            if (removed)
            {
                this.PersistRegistry(registry);
            }
        }

        if (removed)
        {
            this.LogRemoved(channelId);
        }

        return removed;
    }

    /// <summary>Updates the last-seen timestamp for <paramref name="channelId"/>. No-op when absent.</summary>
    public void UpdateLastSeen(string channelId, DateTimeOffset seenAt)
    {
        lock (this.syncLock)
        {
            var registry = this.LoadRegistry();
            if (!registry.TryGetValue(channelId, out var existing))
            {
                return;
            }

            registry[channelId] = existing with { LastSeenAt = seenAt };
            this.PersistRegistry(registry);
        }
    }

    /// <summary>
    /// Enables or disables the record for <paramref name="channelId"/>.
    /// Returns true when a record was updated.
    /// </summary>
    public bool SetEnabled(string channelId, bool enabled)
    {
        lock (this.syncLock)
        {
            var registry = this.LoadRegistry();
            if (!registry.TryGetValue(channelId, out var existing))
            {
                return false;
            }

            var updated = existing.Enabled != enabled;
            if (updated)
            {
                registry[channelId] = existing with { Enabled = enabled };
                this.PersistRegistry(registry);
            }

            return updated;
        }
    }

    /// <summary>
    /// Returns the enabled connector whose token equals <paramref name="token"/>, or null when
    /// no enabled connector matches.
    /// </summary>
    /// <param name="token">The bearer token presented by a connector.</param>
    /// <remarks>
    /// SECURITY: every record is compared even after a match is found. Returning early would
    /// make the lookup's duration depend on the matching connector's position in the registry,
    /// which is an oracle a local process could use to learn about other connectors. The
    /// per-token comparison itself is already constant-time via
    /// <see cref="ConnectorTokenGenerator.TokensEqual"/>.
    /// </remarks>
    public ConnectorRecord? FindByToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        ConnectorRecord? match = null;

        lock (this.syncLock)
        {
            foreach (var record in this.LoadRegistry().Values)
            {
                // Deliberately unconditional and without break — see the remarks above.
                if (ConnectorTokenGenerator.TokensEqual(record.Token, token) && record.Enabled)
                {
                    match = record;
                }
            }
        }

        return match;
    }

    private Dictionary<string, ConnectorRecord> LoadRegistry()
    {
        var blob = this.secretStore.GetSecret(SecretId);
        if (string.IsNullOrEmpty(blob))
        {
            return new Dictionary<string, ConnectorRecord>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ConnectorRecord>>(blob, JsonOptions)
                ?? new Dictionary<string, ConnectorRecord>(StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            // SECURITY: content-free — only the exception TYPE. A malformed blob's JsonException
            // message can include a snippet of the (decrypted) stored payload, i.e. token material.
            this.LogParseFailed(ex.GetType().Name);
            return new Dictionary<string, ConnectorRecord>(StringComparer.Ordinal);
        }
    }

    private void PersistRegistry(Dictionary<string, ConnectorRecord> registry)
    {
        var blob = JsonSerializer.Serialize(registry, JsonOptions);
        this.secretStore.SetSecret(SecretId, blob);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connector record saved: {ChannelId}")]
    private partial void LogSaved(string channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connector record removed: {ChannelId}")]
    private partial void LogRemoved(string channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector registry blob could not be parsed: {Error}")]
    private partial void LogParseFailed(string error);
}
