using Cortex.Contained.Bridge.Connectors.Security;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Connectors.Pairing;

/// <summary>
/// Real pairing service registered in Phase 2. Implements both <see cref="IConnectorAuthenticator"/>
/// (called by <see cref="ConnectorSession"/>) and <see cref="IConnectorPairingCoordinator"/> (called by
/// the Web UI REST layer). The two sides communicate through in-process
/// <see cref="TaskCompletionSource{TResult}"/> instances — no shared queue, no polling.
/// </summary>
// SECURITY: no [LoggerMessage] template in this class may take a token as a parameter.
// Pairing codes are short-lived and visible in two places by design, but are still
// NOT logged — only channel id and request id are logged.
public sealed partial class ConnectorPairingService : IConnectorAuthenticator, IConnectorPairingCoordinator, IDisposable
{
    private const int RateLimitMaxRequests = 5;
    private const int RateLimitBucketSweepThreshold = 256;

    /// <summary>
    /// Ceiling on concurrently pending pairing requests across every channel id. The per-channel
    /// rate limit alone does not bound this, because a hostile process can pick unlimited distinct
    /// keys — and a flooded approval list is a social-engineering aid, since it buries the
    /// legitimate request the user is actually looking for.
    /// </summary>
    private const int MaxPendingRequests = 16;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PairingCodeExpiry = TimeSpan.FromMinutes(5);

    private readonly ConnectorTokenStore tokenStore;
    private readonly BridgeConfig config;
    private readonly IConnectorRegistry registry;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ConnectorPairingService> logger;
    private readonly Lock syncLock = new();

    // channelId → pending entry
    private readonly Dictionary<string, PendingEntry> pendingByChannelId = new(StringComparer.Ordinal);

    // requestId → pending entry
    private readonly Dictionary<string, PendingEntry> pendingByRequestId = new(StringComparer.Ordinal);

    // channelId → list of request timestamps within the rate-limit window
    private readonly Dictionary<string, List<DateTimeOffset>> rateLimitBuckets = new(StringComparer.Ordinal);

    private bool disposed;

    /// <summary>Initialises a new <see cref="ConnectorPairingService"/>.</summary>
    public ConnectorPairingService(
        ConnectorTokenStore tokenStore,
        BridgeConfig config,
        IConnectorRegistry registry,
        TimeProvider timeProvider,
        ILogger<ConnectorPairingService> logger)
    {
        this.tokenStore = tokenStore;
        this.config = config;
        this.registry = registry;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public ValueTask<ConnectorAuthResult> AuthenticateAsync(ConnectorAuthRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(this.Authenticate(request));
    }

    /// <inheritdoc/>
    public IReadOnlyList<ConnectorPairingRequest> GetPendingRequests()
    {
        var now = this.timeProvider.GetUtcNow();
        lock (this.syncLock)
        {
            return
            [
                .. this.pendingByChannelId.Values
                    .Where(e => e.ExpiresAt > now)
                    .OrderBy(e => e.RequestedAt)
                    .Select(e => e.ToRequest())
            ];
        }
    }

    /// <inheritdoc/>
    public bool Approve(string requestId)
    {
        TaskCompletionSource<ConnectorAuthResult> tcs;
        ConnectorRecord recordToSave;
        string tokenToSend;

        lock (this.syncLock)
        {
            if (!this.pendingByRequestId.TryGetValue(requestId, out var entry))
            {
                return false;
            }

            // The expiry timer normally sweeps the entry away, but approving in the window
            // between the deadline passing and the timer callback running must still fail.
            if (entry.ExpiresAt <= this.timeProvider.GetUtcNow())
            {
                return false;
            }

            var token = ConnectorTokenGenerator.CreateToken();
            var record = new ConnectorRecord
            {
                ChannelId = entry.ChannelId,
                Key = entry.Key,
                InstanceId = entry.InstanceId,
                DisplayName = entry.DisplayName,
                Token = token,
                PairedAt = this.timeProvider.GetUtcNow(),
                LastSeenAt = this.timeProvider.GetUtcNow(),
            };

            tcs = entry.Tcs;
            recordToSave = record;
            tokenToSend = token;

            this.RemoveEntryLocked(entry);
        }

        // Persist outside the lock, then complete TCS.
        this.tokenStore.Save(recordToSave);

        tcs.TrySetResult(ConnectorAuthResult.Approved(tokenToSend));
        this.LogApproved(requestId);
        return true;
    }

    /// <inheritdoc/>
    public bool Deny(string requestId, string reason)
    {
        TaskCompletionSource<ConnectorAuthResult> tcs;

        lock (this.syncLock)
        {
            if (!this.pendingByRequestId.TryGetValue(requestId, out var entry))
            {
                return false;
            }

            tcs = entry.Tcs;
            this.RemoveEntryLocked(entry);
        }

        tcs.TrySetResult(ConnectorAuthResult.Denied(reason));
        this.LogDenied(requestId, reason);
        return true;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ConnectorSummary> GetPairedConnectors() =>
        [.. this.tokenStore.GetAll().Select(ConnectorSummary.FromRecord)];

    /// <inheritdoc/>
    public async Task<bool> RevokeAsync(string channelId)
    {
        var removed = this.tokenStore.Remove(channelId);
        await this.registry.DetachByChannelIdAsync(channelId).ConfigureAwait(false);
        return removed;
    }

    /// <inheritdoc/>
    public async Task<bool> SetEnabledAsync(string channelId, bool enabled)
    {
        var updated = this.tokenStore.SetEnabled(channelId, enabled);
        if (!enabled)
        {
            await this.registry.DetachByChannelIdAsync(channelId).ConfigureAwait(false);
        }

        return updated;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        List<TaskCompletionSource<ConnectorAuthResult>> pending = [];

        lock (this.syncLock)
        {
            foreach (var entry in this.pendingByRequestId.Values)
            {
                entry.ExpiryTimer?.Dispose();
                pending.Add(entry.Tcs);
            }

            this.pendingByChannelId.Clear();
            this.pendingByRequestId.Clear();
        }

        // Complete all outstanding TCSes outside the lock.
        foreach (var tcs in pending)
        {
            tcs.TrySetResult(ConnectorAuthResult.Denied("shutting_down"));
        }
    }

    private ConnectorAuthResult Authenticate(ConnectorAuthRequest request)
    {
        var channelId = ConnectorChannelId.Create(request.Key, request.InstanceId);

        // 1. Look up stored record.
        var record = this.tokenStore.Get(channelId);

        if (record is not null)
        {
            // 2. Disabled connector — refuse without starting a pairing flow.
            if (!record.Enabled)
            {
                this.LogConnectorDisabled(channelId);
                return ConnectorAuthResult.Denied("connector_disabled");
            }

            // 3. Valid token — approve immediately.
            if (ConnectorTokenGenerator.TokensEqual(record.Token, request.Token))
            {
                this.tokenStore.UpdateLastSeen(channelId, this.timeProvider.GetUtcNow());
                return ConnectorAuthResult.Approved(null);
            }

            // 4. Token mismatch — fall through to pairing flow.
        }

        // 5. RequireApproval == false: auto-approve.
        if (!this.config.Connectors.RequireApproval)
        {
            this.LogAutoApproved(channelId);
            var autoToken = ConnectorTokenGenerator.CreateToken();
            var autoRecord = new ConnectorRecord
            {
                ChannelId = channelId,
                Key = request.Key,
                InstanceId = request.InstanceId,
                DisplayName = request.DisplayName,
                Token = autoToken,
                PairedAt = this.timeProvider.GetUtcNow(),
                LastSeenAt = this.timeProvider.GetUtcNow(),
            };
            this.tokenStore.Save(autoRecord);
            return ConnectorAuthResult.Approved(autoToken);
        }

        // 6. Start or join pairing flow.
        var now = this.timeProvider.GetUtcNow();
        PendingEntry entry;
        List<(TaskCompletionSource<ConnectorAuthResult> Tcs, ConnectorAuthResult Result)> toComplete = [];

        lock (this.syncLock)
        {
            // Single-flight: reuse existing unexpired pending request for this channel.
            if (this.pendingByChannelId.TryGetValue(channelId, out var existing) && existing.ExpiresAt > now)
            {
                this.LogJoinedExistingRequest(channelId, existing.RequestId);
                return new ConnectorAuthResult
                {
                    Outcome = ConnectorAuthOutcome.PairingRequired,
                    PairingCode = existing.Code,
                    ExpiresAt = existing.ExpiresAt,
                    PairingCompletion = existing.Tcs.Task,
                };
            }

            // Rate limit: count new requests in the rolling window.
            this.PruneRateLimitBucket(channelId, now);
            if (!this.rateLimitBuckets.TryGetValue(channelId, out var bucket))
            {
                bucket = [];
                this.rateLimitBuckets[channelId] = bucket;
            }

            if (bucket.Count >= RateLimitMaxRequests)
            {
                this.LogRateLimited(channelId);
                return ConnectorAuthResult.Denied("pairing_rate_limited");
            }

            // Count only live requests: an expired entry that the sweep has not reclaimed yet
            // must not consume a slot, or a burst of abandoned attempts would lock the user out.
            var livePending = this.pendingByChannelId.Values.Count(e => e.ExpiresAt > now);
            if (livePending >= MaxPendingRequests)
            {
                this.LogPendingLimitReached(channelId, MaxPendingRequests);
                return ConnectorAuthResult.Denied("pairing_rate_limited");
            }

            bucket.Add(now);

            // Remove any stale entry for this channel.
            if (this.pendingByChannelId.TryGetValue(channelId, out var stale))
            {
                this.RemoveEntryLocked(stale);
                toComplete.Add((stale.Tcs, ConnectorAuthResult.Denied("pairing_expired")));
            }

            // Create new entry.
            var requestId = Guid.NewGuid().ToString("N");
            var code = ConnectorTokenGenerator.CreatePairingCode();
            var expiresAt = now + PairingCodeExpiry;
            var tcs = new TaskCompletionSource<ConnectorAuthResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            entry = new PendingEntry(
                requestId,
                channelId,
                request.Key,
                request.InstanceId,
                request.DisplayName,
                code,
                request.RemoteEndpoint,
                now,
                expiresAt,
                tcs);

            this.pendingByChannelId[channelId] = entry;
            this.pendingByRequestId[requestId] = entry;

            // Schedule expiry using timeProvider so FakeTimeProvider advances it in tests.
            entry.ExpiryTimer = this.timeProvider.CreateTimer(
                _ => this.ExpireEntry(requestId),
                null,
                PairingCodeExpiry,
                Timeout.InfiniteTimeSpan);

            this.LogPairingStarted(channelId, requestId);
        }

        // Complete stale entries outside the lock to avoid deadlock.
        foreach (var (tcs, result) in toComplete)
        {
            tcs.TrySetResult(result);
        }

        return new ConnectorAuthResult
        {
            Outcome = ConnectorAuthOutcome.PairingRequired,
            PairingCode = entry.Code,
            ExpiresAt = entry.ExpiresAt,
            PairingCompletion = entry.Tcs.Task,
        };
    }

    private void ExpireEntry(string requestId)
    {
        TaskCompletionSource<ConnectorAuthResult> tcs;

        lock (this.syncLock)
        {
            if (!this.pendingByRequestId.TryGetValue(requestId, out var entry))
            {
                return;
            }

            tcs = entry.Tcs;
            this.RemoveEntryLocked(entry);
        }

        tcs.TrySetResult(ConnectorAuthResult.Denied("pairing_expired"));
        this.LogExpired(requestId);
    }

    private void RemoveEntryLocked(PendingEntry entry)
    {
        entry.ExpiryTimer?.Dispose();
        this.pendingByChannelId.Remove(entry.ChannelId);
        this.pendingByRequestId.Remove(entry.RequestId);
    }

    private void PruneRateLimitBucket(string channelId, DateTimeOffset now)
    {
        if (this.rateLimitBuckets.TryGetValue(channelId, out var bucket))
        {
            bucket.RemoveAll(t => now - t >= RateLimitWindow);
        }

        // An unbounded dictionary keyed by attacker-chosen channel ids is a slow memory
        // leak, so sweep every fully-expired bucket once the map grows past a sane size.
        if (this.rateLimitBuckets.Count <= RateLimitBucketSweepThreshold)
        {
            return;
        }

        List<string> stale = [];
        foreach (var (id, entries) in this.rateLimitBuckets)
        {
            if (entries.Count == 0 || now - entries[^1] >= RateLimitWindow)
            {
                stale.Add(id);
            }
        }

        foreach (var id in stale)
        {
            if (!string.Equals(id, channelId, StringComparison.Ordinal))
            {
                this.rateLimitBuckets.Remove(id);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector '{ChannelId}' is disabled; attach refused")]
    private partial void LogConnectorDisabled(string channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector '{ChannelId}' auto-approved because RequireApproval is false")]
    private partial void LogAutoApproved(string channelId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Connector '{ChannelId}' joined existing pairing request '{RequestId}'")]
    private partial void LogJoinedExistingRequest(string channelId, string requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector '{ChannelId}' exceeded pairing rate limit")]
    private partial void LogRateLimited(string channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused pairing request for '{ChannelId}': {MaxPendingRequests} requests are already awaiting approval.")]
    private partial void LogPendingLimitReached(string channelId, int maxPendingRequests);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pairing request '{RequestId}' started for connector '{ChannelId}'")]
    private partial void LogPairingStarted(string channelId, string requestId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pairing request '{RequestId}' approved")]
    private partial void LogApproved(string requestId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pairing request '{RequestId}' denied: {Reason}")]
    private partial void LogDenied(string requestId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pairing request '{RequestId}' expired")]
    private partial void LogExpired(string requestId);

    private sealed class PendingEntry
    {
        public PendingEntry(
            string requestId,
            string channelId,
            string key,
            string instanceId,
            string displayName,
            string code,
            string remoteEndpoint,
            DateTimeOffset requestedAt,
            DateTimeOffset expiresAt,
            TaskCompletionSource<ConnectorAuthResult> tcs)
        {
            this.RequestId = requestId;
            this.ChannelId = channelId;
            this.Key = key;
            this.InstanceId = instanceId;
            this.DisplayName = displayName;
            this.Code = code;
            this.RemoteEndpoint = remoteEndpoint;
            this.RequestedAt = requestedAt;
            this.ExpiresAt = expiresAt;
            this.Tcs = tcs;
        }

        public string RequestId { get; }

        public string ChannelId { get; }

        public string Key { get; }

        public string InstanceId { get; }

        public string DisplayName { get; }

        public string Code { get; }

        public string RemoteEndpoint { get; }

        public DateTimeOffset RequestedAt { get; }

        public DateTimeOffset ExpiresAt { get; }

        public TaskCompletionSource<ConnectorAuthResult> Tcs { get; }

        public ITimer? ExpiryTimer { get; set; }

        public ConnectorPairingRequest ToRequest() => new()
        {
            RequestId = this.RequestId,
            ChannelId = this.ChannelId,
            Key = this.Key,
            InstanceId = this.InstanceId,
            DisplayName = this.DisplayName,
            Code = this.Code,
            RemoteEndpoint = this.RemoteEndpoint,
            RequestedAt = this.RequestedAt,
            ExpiresAt = this.ExpiresAt,
        };
    }
}
