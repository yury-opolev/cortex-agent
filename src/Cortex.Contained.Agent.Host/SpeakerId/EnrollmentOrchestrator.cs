namespace Cortex.Contained.Agent.Host.SpeakerId;

using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Speech.SpeakerId;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

/// <summary>
/// Owns per-tenant voice-enrollment state and the state machine.
/// Code (not the LLM) is the authority on every transition.
/// </summary>
public sealed partial class EnrollmentOrchestrator : IConversationToolGate
{
    /// <summary>Default number of enrollment utterances captured during the wizard.</summary>
    public const int DefaultSamplesRequired = 3;

    /// <summary>Number of successful confirmation matches required to commit enrollment.</summary>
    public const int DefaultMatchesRequired = 2;

    /// <summary>Consecutive confirmation failures that abort the wizard.</summary>
    public const int DefaultFailuresAllowed = 3;

    private readonly IVoiceprintStore store;
    private readonly BridgeClientAccessor? bridgeClientAccessor;
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<string, VoiceEnrollmentState> stateCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> featureEnabledCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> tenantWriteLocks = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;

    public EnrollmentOrchestrator(
        IVoiceprintStore store,
        ILogger<EnrollmentOrchestrator>? logger = null,
        TimeProvider? timeProvider = null,
        BridgeClientAccessor? bridgeClientAccessor = null)
    {
        this.store = store;
        this.bridgeClientAccessor = bridgeClientAccessor;
        this.logger = (ILogger?)logger ?? NullLogger<EnrollmentOrchestrator>.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Public so wiring code can adjust capacity if needed.</summary>
    public int SamplesRequired { get; init; } = DefaultSamplesRequired;

    public int MatchesRequired { get; init; } = DefaultMatchesRequired;

    public int FailuresAllowed { get; init; } = DefaultFailuresAllowed;

    // ── State inspection ────────────────────────────────────────────

    public async ValueTask<VoiceEnrollmentState> GetStateAsync(string tenantId, CancellationToken cancellationToken)
    {
        // Cache-first per spec: the verification gate hot path goes through
        // GetStateAsync on every committed utterance. Hitting SQLite there
        // funnels through the writer lock and is unnecessary because all
        // writes flow through WriteRecordAsync which keeps the cache fresh.
        // Phase 3 caveat: out-of-band writes (Web UI, second process) won't
        // invalidate this cache. The Bridge-side SignalR store will need to
        // push invalidations or we'll need to TTL-expire entries.
        if (this.stateCache.TryGetValue(tenantId, out var cached))
        {
            return cached;
        }

        var record = await this.store.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var state = record?.State ?? VoiceEnrollmentState.Unknown;
        this.stateCache[tenantId] = state;
        this.featureEnabledCache[tenantId] = record?.FeatureEnabled ?? true;
        return state;
    }

    /// <summary>
    /// Synchronous best-effort state lookup using the in-memory cache. Used by
    /// the tool-registry filter where async access isn't possible.
    /// Returns <see cref="VoiceEnrollmentState.Unknown"/> when there's no
    /// cached value yet (treated as "no enrollment" for filtering — equivalent
    /// to a fresh tenant).
    /// </summary>
    public VoiceEnrollmentState GetCachedState(string tenantId)
        => this.stateCache.TryGetValue(tenantId, out var s) ? s : VoiceEnrollmentState.Unknown;

    /// <summary>
    /// Synchronous best-effort feature-flag lookup. Defaults to <c>true</c>
    /// (feature on) when no record has been loaded yet — matches the spec
    /// behaviour that a fresh tenant has the feature on by default.
    /// </summary>
    public bool IsFeatureEnabled(string tenantId)
        => !this.featureEnabledCache.TryGetValue(tenantId, out var enabled) || enabled;

    /// <inheritdoc/>
    public IReadOnlySet<string> GetHiddenTools(string? conversationId)
    {
        if (string.IsNullOrEmpty(conversationId)
            || !conversationId.StartsWith(VoiceEnrollmentToolHelpers.VoiceConversationPrefix, StringComparison.Ordinal))
        {
            // Non-voice conversations: hide every enrollment tool.
            return new HashSet<string>(VoiceEnrollmentToolHelpers.AllToolNames, StringComparer.Ordinal);
        }

        var tenantId = conversationId[VoiceEnrollmentToolHelpers.VoiceConversationPrefix.Length..];
        var state = this.GetCachedState(tenantId);
        var allowed = VoiceEnrollmentToolHelpers.ToolsForState(state);

        var hidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in VoiceEnrollmentToolHelpers.AllToolNames)
        {
            if (!allowed.Contains(name))
            {
                hidden.Add(name);
            }
        }
        return hidden;
    }

    // ── State transitions ───────────────────────────────────────────

    public async ValueTask<EnrollmentOutcome> TryStartAsync(string tenantId, CancellationToken cancellationToken)
    {
        using var _ = await this.AcquireTenantLockAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var record = await this.store.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var current = record?.State ?? VoiceEnrollmentState.Unknown;
        if (current is not (VoiceEnrollmentState.Unknown or VoiceEnrollmentState.Declined))
        {
            return new EnrollmentOutcome.InvalidState(current, "start_voice_enrollment is only valid from Unknown or Declined.");
        }

        await this.WriteStateAsync(tenantId, record, VoiceEnrollmentState.Enrolling, cancellationToken).ConfigureAwait(false);
        await this.PushProgressAsync(tenantId, VoiceEnrollmentState.Enrolling, 0, this.SamplesRequired).ConfigureAwait(false);

        return new EnrollmentOutcome.Transitioned(
            current,
            VoiceEnrollmentState.Enrolling,
            Guidance: $"Ask the user to say a few sentences. {this.SamplesRequired} samples required.");
    }

    public async ValueTask<EnrollmentOutcome> TryDeclineAsync(string tenantId, CancellationToken cancellationToken)
    {
        using var _ = await this.AcquireTenantLockAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var record = await this.store.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var current = record?.State ?? VoiceEnrollmentState.Unknown;
        if (current is not VoiceEnrollmentState.Unknown)
        {
            return new EnrollmentOutcome.InvalidState(current, "decline_voice_enrollment is only valid from Unknown.");
        }

        var now = this.timeProvider.GetUtcNow();
        await this.WriteRecordAsync(
            new VoiceprintRecord(
                tenantId,
                VoiceEnrollmentState.Declined,
                Embedding: null,
                EmbeddingDim: 0,
                ModelId: null,
                SampleCount: 0,
                CreatedAt: record?.CreatedAt ?? now,
                ConfirmedAt: null,
                DeclinedAt: now,
                ThresholdOverride: record?.ThresholdOverride,
                FeatureEnabled: record?.FeatureEnabled ?? true),
            cancellationToken).ConfigureAwait(false);
        await this.PushProgressAsync(tenantId, VoiceEnrollmentState.Declined, 0, this.SamplesRequired).ConfigureAwait(false);

        return new EnrollmentOutcome.Transitioned(current, VoiceEnrollmentState.Declined);
    }

    public async ValueTask<EnrollmentOutcome> TryCancelAsync(string tenantId, CancellationToken cancellationToken)
    {
        using var _ = await this.AcquireTenantLockAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var record = await this.store.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var current = record?.State ?? VoiceEnrollmentState.Unknown;
        switch (current)
        {
            case VoiceEnrollmentState.Enrolling:
            case VoiceEnrollmentState.Confirming:
                await this.WriteStateAsync(tenantId, record, VoiceEnrollmentState.Unknown, cancellationToken).ConfigureAwait(false);
                await this.PushProgressAsync(tenantId, VoiceEnrollmentState.Unknown, 0, this.SamplesRequired).ConfigureAwait(false);
                return new EnrollmentOutcome.Transitioned(current, VoiceEnrollmentState.Unknown);
            case VoiceEnrollmentState.PendingReenroll:
                await this.WriteStateAsync(tenantId, record, VoiceEnrollmentState.Enrolled, cancellationToken).ConfigureAwait(false);
                await this.PushProgressAsync(tenantId, VoiceEnrollmentState.Enrolled, 0, this.SamplesRequired).ConfigureAwait(false);
                return new EnrollmentOutcome.Transitioned(current, VoiceEnrollmentState.Enrolled);
            default:
                return new EnrollmentOutcome.InvalidState(current, "cancel_voice_enrollment is only valid mid-wizard or pending-reenroll.");
        }
    }

    public async ValueTask<EnrollmentOutcome> TryRequestReenrollAsync(string tenantId, CancellationToken cancellationToken)
    {
        using var _ = await this.AcquireTenantLockAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var record = await this.store.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var current = record?.State ?? VoiceEnrollmentState.Unknown;
        if (current is not VoiceEnrollmentState.Enrolled)
        {
            return new EnrollmentOutcome.InvalidState(current, "request_voice_reenrollment is only valid from Enrolled.");
        }

        await this.WriteStateAsync(tenantId, record, VoiceEnrollmentState.PendingReenroll, cancellationToken).ConfigureAwait(false);
        await this.PushProgressAsync(tenantId, VoiceEnrollmentState.PendingReenroll, 0, this.SamplesRequired).ConfigureAwait(false);
        return new EnrollmentOutcome.Transitioned(
            current,
            VoiceEnrollmentState.PendingReenroll,
            Guidance: "Ask the user to confirm they want to replace their voiceprint. Existing voiceprint stays active until they confirm.");
    }

    public async ValueTask<EnrollmentOutcome> TryConfirmReenrollAsync(string tenantId, CancellationToken cancellationToken)
    {
        using var _ = await this.AcquireTenantLockAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var record = await this.store.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var current = record?.State ?? VoiceEnrollmentState.Unknown;
        if (current is not VoiceEnrollmentState.PendingReenroll)
        {
            return new EnrollmentOutcome.InvalidState(current, "confirm_voice_reenrollment is only valid from PendingReenroll.");
        }

        // Wipe voiceprint and move to Enrolling.
        var now = this.timeProvider.GetUtcNow();
        await this.WriteRecordAsync(
            new VoiceprintRecord(
                tenantId,
                VoiceEnrollmentState.Enrolling,
                Embedding: null,
                EmbeddingDim: 0,
                ModelId: null,
                SampleCount: 0,
                CreatedAt: record?.CreatedAt ?? now,
                ConfirmedAt: null,
                DeclinedAt: null,
                ThresholdOverride: record?.ThresholdOverride,
                FeatureEnabled: record?.FeatureEnabled ?? true),
            cancellationToken).ConfigureAwait(false);
        await this.PushProgressAsync(tenantId, VoiceEnrollmentState.Enrolling, 0, this.SamplesRequired).ConfigureAwait(false);

        return new EnrollmentOutcome.Transitioned(
            current,
            VoiceEnrollmentState.Enrolling,
            Guidance: $"Voiceprint wiped. Ask the user to say a few sentences. {this.SamplesRequired} samples required.");
    }

    public async ValueTask<EnrollmentOutcome> TryForgetAsync(string tenantId, CancellationToken cancellationToken)
    {
        using var _ = await this.AcquireTenantLockAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var record = await this.store.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var current = record?.State ?? VoiceEnrollmentState.Unknown;
        if (current is not VoiceEnrollmentState.Enrolled)
        {
            return new EnrollmentOutcome.InvalidState(current, "forget_voice_enrollment is only valid from Enrolled.");
        }

        var now = this.timeProvider.GetUtcNow();
        await this.WriteRecordAsync(
            new VoiceprintRecord(
                tenantId,
                VoiceEnrollmentState.Declined,
                Embedding: null,
                EmbeddingDim: 0,
                ModelId: null,
                SampleCount: 0,
                CreatedAt: record?.CreatedAt ?? now,
                ConfirmedAt: null,
                DeclinedAt: now,
                ThresholdOverride: record?.ThresholdOverride,
                FeatureEnabled: record?.FeatureEnabled ?? true),
            cancellationToken).ConfigureAwait(false);
        await this.PushProgressAsync(tenantId, VoiceEnrollmentState.Declined, 0, this.SamplesRequired).ConfigureAwait(false);

        return new EnrollmentOutcome.Transitioned(current, VoiceEnrollmentState.Declined);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private async ValueTask WriteStateAsync(string tenantId, VoiceprintRecord? existing, VoiceEnrollmentState newState, CancellationToken cancellationToken)
    {
        var now = this.timeProvider.GetUtcNow();
        // Carry forward the existing embedding only for states where the voiceprint
        // remains the authoritative gate target:
        //   - PendingReenroll: gate stays active until user confirms reenroll.
        //   - Enrolled: reached only by cancel-from-PendingReenroll here; voiceprint is unchanged.
        // Other targets (Enrolling, Confirming, Unknown) wipe the embedding.
        var keepEmbedding = newState is VoiceEnrollmentState.PendingReenroll or VoiceEnrollmentState.Enrolled;
        await this.WriteRecordAsync(
            new VoiceprintRecord(
                tenantId,
                newState,
                Embedding: keepEmbedding ? existing?.Embedding : null,
                EmbeddingDim: keepEmbedding ? existing?.EmbeddingDim ?? 0 : 0,
                ModelId: keepEmbedding ? existing?.ModelId : null,
                SampleCount: keepEmbedding ? existing?.SampleCount ?? 0 : 0,
                CreatedAt: existing?.CreatedAt ?? now,
                ConfirmedAt: keepEmbedding ? existing?.ConfirmedAt : null,
                DeclinedAt: existing?.DeclinedAt,
                ThresholdOverride: existing?.ThresholdOverride,
                FeatureEnabled: existing?.FeatureEnabled ?? true),
            cancellationToken).ConfigureAwait(false);
        this.stateCache[tenantId] = newState;
    }

    private async ValueTask WriteRecordAsync(VoiceprintRecord record, CancellationToken cancellationToken)
    {
        await this.store.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        this.stateCache[record.TenantId] = record.State;
        this.featureEnabledCache[record.TenantId] = record.FeatureEnabled;
        await this.PushInvalidationAsync(record.TenantId).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores a finished voiceprint pushed by the Bridge (computed Bridge-side
    /// during the enrollment wizard) and transitions the tenant to Enrolled.
    /// Preserves the existing record's threshold override and feature flag.
    /// Acquires the per-tenant transition lock so this cannot interleave with a
    /// concurrent admin write or wizard transition.
    /// </summary>
    public async ValueTask WriteVoiceprintAsync(string tenantId, float[] embedding, string modelId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        if (embedding.Length == 0)
        {
            throw new ArgumentException("Voiceprint embedding must be non-empty.", nameof(embedding));
        }

        using var handle = await this.AcquireTenantLockAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var existing = await this.store.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var now = this.timeProvider.GetUtcNow();
        await this.WriteRecordAsync(
            new VoiceprintRecord(
                tenantId,
                VoiceEnrollmentState.Enrolled,
                Embedding: embedding,
                EmbeddingDim: embedding.Length,
                ModelId: modelId,
                SampleCount: this.SamplesRequired,
                CreatedAt: existing?.CreatedAt ?? now,
                ConfirmedAt: now,
                DeclinedAt: null,
                ThresholdOverride: existing?.ThresholdOverride,
                FeatureEnabled: existing?.FeatureEnabled ?? true),
            cancellationToken).ConfigureAwait(false);
        await this.PushProgressAsync(tenantId, VoiceEnrollmentState.Enrolled, this.SamplesRequired, this.SamplesRequired).ConfigureAwait(false);
    }

    /// <summary>
    /// Admin-facing write used by the SignalR hub (Web UI actions like
    /// toggle-feature, force-forget, set-threshold-override). Acquires the
    /// per-tenant transition lock so concurrent wizard transitions cannot
    /// interleave with admin overrides.
    /// </summary>
    public async ValueTask WriteFromAdminAsync(VoiceprintRecord record, CancellationToken cancellationToken)
    {
        using var handle = await this.AcquireTenantLockAsync(record.TenantId, cancellationToken).ConfigureAwait(false);
        await this.WriteRecordAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PushInvalidationAsync(string tenantId)
    {
        var client = this.bridgeClientAccessor?.Client;
        if (client is null)
        {
            return;
        }
        try
        {
            await client.OnVoiceprintInvalidated(tenantId).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Intentional: invalidation failures must not break state transitions.
        catch (Exception ex)
        {
            this.LogInvalidationPushFailed(tenantId, ex.Message);
        }
#pragma warning restore CA1031
    }

    private async ValueTask PushProgressAsync(string tenantId, VoiceEnrollmentState state, int captured, int required)
    {
        var client = this.bridgeClientAccessor?.Client;
        if (client is null)
        {
            return;
        }
        try
        {
            await client.OnVoiceEnrollmentProgress(tenantId, state.ToString(), captured, required).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception ex) { this.LogProgressPushFailed(tenantId, ex.Message); }
#pragma warning restore CA1031
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "enrollment-progress-push failed tenant={TenantId} error={Error}")]
    private partial void LogProgressPushFailed(string tenantId, string error);

    /// <summary>
    /// Acquires the per-tenant transition lock so concurrent invocations of
    /// orchestrator methods serialise read-modify-write on the same tenant.
    /// Spec: "the orchestrator also serialises wizard state transitions per
    /// tenant so two parallel sessions (Discord + host-side) cannot both run
    /// the wizard at once."
    /// </summary>
    private async ValueTask<TenantLockHandle> AcquireTenantLockAsync(string tenantId, CancellationToken cancellationToken)
    {
        var sem = this.tenantWriteLocks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new TenantLockHandle(sem);
    }

    private readonly struct TenantLockHandle(SemaphoreSlim sem) : IDisposable
    {
        public void Dispose() => sem.Release();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "voice-enroll: invalidation push failed tenant={TenantId} error={Error}")]
    private partial void LogInvalidationPushFailed(string tenantId, string error);
}
