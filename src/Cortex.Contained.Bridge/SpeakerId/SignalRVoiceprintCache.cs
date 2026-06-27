namespace Cortex.Contained.Bridge.SpeakerId;

using System.Collections.Concurrent;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Speech.SpeakerId;
using Microsoft.Extensions.Logging;

/// <summary>
/// Bridge-side <see cref="IVoiceprintStore"/> backed by SignalR calls to
/// Agent.Host. Reads are cache-first and resolved through the per-tenant
/// hub client obtained from the injected <see cref="ITenantRouterForCache"/>;
/// writes are not supported on this side (Agent.Host owns the SQLite store).
/// Cache entries are evicted on
/// <see cref="IAgentHubClient.OnVoiceprintInvalidated"/> push.
/// </summary>
public sealed partial class SignalRVoiceprintCache : IVoiceprintStore
{
    private readonly ITenantRouterForCache router;
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<string, VoiceprintRecord?> cache = new(StringComparer.Ordinal);

    public SignalRVoiceprintCache(ITenantRouterForCache router, ILogger<SignalRVoiceprintCache> logger)
    {
        this.router = router;
        this.logger = logger;
    }

    public async ValueTask<VoiceprintRecord?> GetAsync(string tenantId, CancellationToken cancellationToken)
    {
        if (this.cache.TryGetValue(tenantId, out var cached))
        {
            return cached;
        }

        var client = this.router.GetClient(tenantId);
        if (client is null)
        {
            // Tenant has no active hub connection — treat as no voiceprint.
            // Do NOT cache (so a later connection produces a fresh read).
            return null;
        }

        try
        {
            var dto = await client.GetVoiceprintAsync(tenantId, cancellationToken).ConfigureAwait(false);
            var record = dto is null ? null : FromDto(dto);
            this.cache[tenantId] = record;
            return record;
        }
#pragma warning disable CA1031 // Intentional: a hub failure must not silence the user.
        catch (Exception ex)
        {
            this.LogFetchFailed(tenantId, ex.Message);
            return null;
        }
#pragma warning restore CA1031
    }

    public ValueTask UpsertAsync(VoiceprintRecord record, CancellationToken cancellationToken)
        => throw new NotSupportedException("Bridge does not own the voiceprint store; admin actions go through HubClient methods.");

    public ValueTask DeleteAsync(string tenantId, CancellationToken cancellationToken)
        => throw new NotSupportedException("Bridge does not own the voiceprint store; admin actions go through HubClient methods.");

    /// <summary>
    /// Invalidate the cache entry for <paramref name="tenantId"/>. Called by
    /// the Bridge's <see cref="IAgentHubClient.OnVoiceprintInvalidated"/>
    /// implementation when the agent reports a state transition.
    /// </summary>
    public void Invalidate(string tenantId) => this.cache.TryRemove(tenantId, out _);

    /// <summary>
    /// Clear every cached entry. Wired to the Bridge's hub reconnect event so
    /// any invalidation pushes we missed while disconnected do not leave the
    /// gate seeing stale (or negative-cached) voiceprint state.
    /// </summary>
    public void InvalidateAll() => this.cache.Clear();

    private static VoiceprintRecord FromDto(VoiceprintSnapshotDto dto)
        => new(
            TenantId: dto.TenantId,
            State: Enum.TryParse<VoiceEnrollmentState>(dto.StateName, out var s) ? s : VoiceEnrollmentState.Unknown,
            Embedding: dto.Embedding,
            EmbeddingDim: dto.EmbeddingDim,
            ModelId: dto.ModelId,
            SampleCount: 0,
            // Timestamps are not part of the wire DTO — the Bridge-side gate
            // does not read them. Use default so consumers that accidentally
            // read these see an obvious sentinel rather than a fake "now".
            CreatedAt: default,
            ConfirmedAt: null,
            DeclinedAt: null,
            ThresholdOverride: dto.ThresholdOverride,
            FeatureEnabled: dto.FeatureEnabled);

    [LoggerMessage(Level = LogLevel.Warning, Message = "voiceprint-cache: hub fetch failed tenant={TenantId} error={Error}")]
    private partial void LogFetchFailed(string tenantId, string error);
}

/// <summary>
/// Test seam for per-tenant hub-client resolution. Production registers
/// <see cref="TenantRouterCacheAdapter"/> which wraps the real
/// <see cref="Tenants.TenantRouter"/>. Tests provide a fake.
/// </summary>
public interface ITenantRouterForCache
{
    IVoiceprintHubClient? GetClient(string tenantId);
}

/// <summary>
/// Minimal hub-client surface the cache needs. Production wraps
/// <see cref="Hub.HubClient"/>; tests provide fakes.
/// </summary>
public interface IVoiceprintHubClient
{
    Task<VoiceprintSnapshotDto?> GetVoiceprintAsync(string tenantId, CancellationToken cancellationToken);
}

/// <summary>Production adapter over <see cref="Tenants.TenantRouter"/>.</summary>
public sealed class TenantRouterCacheAdapter : ITenantRouterForCache
{
    private readonly Tenants.TenantRouter router;

    public TenantRouterCacheAdapter(Tenants.TenantRouter router) { this.router = router; }

    public IVoiceprintHubClient? GetClient(string tenantId)
    {
        var hubClient = this.router.GetClient(tenantId);
        return hubClient is null ? null : new RealClientWrapper(hubClient);
    }

    private sealed class RealClientWrapper : IVoiceprintHubClient
    {
        private readonly Hub.HubClient real;

        public RealClientWrapper(Hub.HubClient real) { this.real = real; }

        public Task<VoiceprintSnapshotDto?> GetVoiceprintAsync(string tenantId, CancellationToken ct)
            => this.real.GetVoiceprintAsync(tenantId, ct);
    }
}
