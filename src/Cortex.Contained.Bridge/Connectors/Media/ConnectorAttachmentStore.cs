using System.Collections.Concurrent;
using System.Security.Cryptography;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Connectors.Media;

/// <summary>
/// Holds attachment bytes that are too large to travel inside a connector frame, handing out
/// opaque handles in their place. Handles are the only thing that crosses the wire, so a
/// connector never supplies — and the Bridge never dereferences — a location.
/// </summary>
/// <remarks>
/// Security properties this type is responsible for:
/// <list type="bullet">
/// <item><b>Unguessable.</b> Handles carry 128 bits of cryptographic randomness, so they cannot
/// be enumerated or predicted.</item>
/// <item><b>Channel-scoped.</b> A handle only resolves for the channel it was issued to.
/// Presenting another channel's handle is indistinguishable from presenting a nonexistent one.</item>
/// <item><b>Single-use.</b> <see cref="Consume"/> removes the entry, so a leaked handle cannot be
/// replayed.</item>
/// <item><b>Expiring and quota-bounded.</b> Entries expire on a TTL and each channel has a byte
/// quota, so a connector cannot pin unbounded memory by uploading and never referencing.</item>
/// </list>
/// <para>
/// Storage is in-memory and therefore deliberately non-durable: attachments are a
/// seconds-to-minutes staging area between upload and reference, and persisting user media to
/// disk would create a new data-at-rest obligation for no benefit. Handles do not survive a
/// Bridge restart, which surfaces to the connector as a plain <c>attachment_not_found</c>.
/// </para>
/// </remarks>
public sealed partial class ConnectorAttachmentStore : IConnectorAttachmentIssuer, IConnectorAttachmentResolver, IDisposable
{
    /// <summary>Bytes of entropy in a handle. 16 bytes is 128 bits.</summary>
    internal const int HandleEntropyBytes = 16;

    /// <summary>Prefix on every issued handle, so the value is recognisable in a payload.</summary>
    internal const string HandlePrefix = "att_";

    /// <summary>How often expired entries are swept. Resolution also checks expiry lazily.</summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly ConnectorMediaPolicy policy;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ConnectorAttachmentStore> logger;
    private readonly ITimer sweepTimer;
    private readonly Lock quotaLock = new();
    private bool disposed;

    /// <summary>Initialises a new <see cref="ConnectorAttachmentStore"/>.</summary>
    /// <param name="policy">Effective media policy supplying the TTL and per-channel quota.</param>
    /// <param name="timeProvider">Time source for expiry and sweeping.</param>
    /// <param name="logger">Logger.</param>
    public ConnectorAttachmentStore(
        ConnectorMediaPolicy policy,
        TimeProvider timeProvider,
        ILogger<ConnectorAttachmentStore> logger)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.policy = policy;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.sweepTimer = timeProvider.CreateTimer(_ => this.Sweep(), null, SweepInterval, SweepInterval);
    }

    /// <summary>Number of live entries. Intended for tests and diagnostics.</summary>
    public int Count => this.entries.Count;

    /// <summary>
    /// Creates a config-driven store. Convenience for composition roots that hold raw settings.
    /// </summary>
    /// <param name="settings">The connector settings carrying media policy and frame limits.</param>
    /// <param name="timeProvider">Time source for expiry and sweeping.</param>
    /// <param name="logger">Logger.</param>
    public static ConnectorAttachmentStore FromSettings(
        ConnectorSettingsConfig settings,
        TimeProvider timeProvider,
        ILogger<ConnectorAttachmentStore> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new ConnectorAttachmentStore(
            ConnectorMediaPolicy.From(settings.Media, settings.Limits.MaxFrameBytes),
            timeProvider,
            logger);
    }

    /// <inheritdoc />
    public string? Issue(string channelId, ConnectorAttachmentContent content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentNullException.ThrowIfNull(content);

        if (!this.policy.Enabled)
        {
            return null;
        }

        if (content.Data.LongLength == 0 || content.Data.LongLength > this.policy.MaxAttachmentBytes)
        {
            return null;
        }

        // SECURITY: nothing enters the store without passing the same checks the wire path
        // applies, so a handle can never be a way around the allow-list.
        if (!this.policy.IsMimeTypeAllowed(content.MimeType)
            || !ImageContentSniffer.MatchesDeclaredType(content.Data, content.MimeType))
        {
            return null;
        }

        var now = this.timeProvider.GetUtcNow();
        var entry = new Entry(channelId, content, now + this.policy.HandleTtl);

        // The quota check and the insert must be atomic against each other, or concurrent uploads
        // could each observe headroom that only one of them can actually have.
        lock (this.quotaLock)
        {
            this.RemoveExpiredLocked(now);

            var used = this.LiveBytesForChannelLocked(channelId, now);
            if (used + content.Data.LongLength > this.policy.MaxStoredBytesPerConnector)
            {
                this.LogQuotaExceeded(channelId, this.policy.MaxStoredBytesPerConnector);
                return null;
            }

            var handle = CreateHandle();
            this.entries[handle] = entry;
            return handle;
        }
    }

    /// <inheritdoc />
    public ConnectorAttachmentContent? Resolve(string handle, string channelId) =>
        this.Consume(handle, channelId);

    /// <summary>
    /// Removes and returns the content behind <paramref name="handle"/>, or null when it is
    /// unknown, expired, already consumed, or was issued to a different channel.
    /// </summary>
    /// <param name="handle">The presented handle.</param>
    /// <param name="channelId">The channel presenting it.</param>
    public ConnectorAttachmentContent? Consume(string handle, string channelId)
    {
        if (string.IsNullOrEmpty(handle) || string.IsNullOrEmpty(channelId))
        {
            return null;
        }

        if (!this.entries.TryGetValue(handle, out var entry))
        {
            return null;
        }

        // SECURITY: check ownership WITHOUT removing. Removing first and restoring on mismatch
        // would let another connector delete an attachment it cannot read.
        if (!string.Equals(entry.ChannelId, channelId, StringComparison.Ordinal))
        {
            this.LogCrossChannelAccess(channelId);
            return null;
        }

        if (entry.ExpiresAt <= this.timeProvider.GetUtcNow())
        {
            this.entries.TryRemove(handle, out _);
            return null;
        }

        // Single-use: only the caller that wins the removal gets the content, so a leaked handle
        // cannot be replayed and two racing readers cannot both succeed.
        return this.entries.TryRemove(handle, out var removed) ? removed.Content : null;
    }

    /// <summary>Drops every entry belonging to <paramref name="channelId"/>.</summary>
    /// <param name="channelId">The channel whose staged attachments should be discarded.</param>
    public void EvictChannel(string channelId)
    {
        if (string.IsNullOrEmpty(channelId))
        {
            return;
        }

        foreach (var (handle, entry) in this.entries)
        {
            if (string.Equals(entry.ChannelId, channelId, StringComparison.Ordinal))
            {
                this.entries.TryRemove(handle, out _);
            }
        }
    }

    /// <summary>Total live bytes currently held for <paramref name="channelId"/>.</summary>
    /// <param name="channelId">The channel to measure.</param>
    public long LiveBytesForChannel(string channelId)
    {
        lock (this.quotaLock)
        {
            return this.LiveBytesForChannelLocked(channelId, this.timeProvider.GetUtcNow());
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.sweepTimer.Dispose();
        this.entries.Clear();
    }

    /// <summary>
    /// Generates an unguessable handle. Base64-URL over 128 bits of cryptographic randomness,
    /// using the same alphabet the connector token generator uses so the value stays safe in a
    /// URL path segment and in a log line.
    /// </summary>
    internal static string CreateHandle()
    {
        var bytes = RandomNumberGenerator.GetBytes(HandleEntropyBytes);
        var encoded = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        return HandlePrefix + encoded;
    }

    private void Sweep()
    {
        var now = this.timeProvider.GetUtcNow();
        lock (this.quotaLock)
        {
            this.RemoveExpiredLocked(now);
        }
    }

    private void RemoveExpiredLocked(DateTimeOffset now)
    {
        foreach (var (handle, entry) in this.entries)
        {
            if (entry.ExpiresAt <= now)
            {
                this.entries.TryRemove(handle, out _);
            }
        }
    }

    private long LiveBytesForChannelLocked(string channelId, DateTimeOffset now)
    {
        var total = 0L;
        foreach (var entry in this.entries.Values)
        {
            if (entry.ExpiresAt > now && string.Equals(entry.ChannelId, channelId, StringComparison.Ordinal))
            {
                total += entry.Content.Data.LongLength;
            }
        }

        return total;
    }

    /// <summary>
    /// Logged without the handle: it is a bearer capability, and naming it would put a usable
    /// credential in the log. The channel id is enough to investigate.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {ChannelId} presented an attachment handle issued to a different channel; refused.")]
    private partial void LogCrossChannelAccess(string channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {ChannelId} exceeded its attachment storage quota of {MaxStoredBytes} bytes; upload refused.")]
    private partial void LogQuotaExceeded(string channelId, long maxStoredBytes);

    private sealed record Entry(string ChannelId, ConnectorAttachmentContent Content, DateTimeOffset ExpiresAt);
}
