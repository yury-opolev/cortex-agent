namespace Cortex.Contained.Agent.Host.Hubs;

using Cortex.Contained.Agent.Host.SpeakerId;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Speech.SpeakerId;

public sealed partial class AgentHub
{
    private const string FeatureDisabledMessage =
        "Speaker identification is disabled — no ONNX model is loaded on the agent.";

    /// <inheritdoc />
    public async Task<VoiceprintSnapshotDto?> GetVoiceprint(string tenantId)
    {
        var record = await this.voiceprintStore.GetAsync(tenantId, this.Context.ConnectionAborted).ConfigureAwait(false);
        return record is null ? null : ToDto(record);
    }

    /// <inheritdoc />
    public async Task SetVoiceFeatureEnabled(string tenantId, bool enabled)
    {
        if (this.enrollmentOrchestrator is null)
        {
            throw new InvalidOperationException(FeatureDisabledMessage);
        }
        var existing = await this.voiceprintStore.GetAsync(tenantId, this.Context.ConnectionAborted).ConfigureAwait(false);
        var record = existing is null
            ? NewRecord(tenantId, featureEnabled: enabled)
            : existing with { FeatureEnabled = enabled };
        await this.enrollmentOrchestrator.WriteFromAdminAsync(record, this.Context.ConnectionAborted).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ResetVoiceEnrollment(string tenantId)
    {
        if (this.enrollmentOrchestrator is null)
        {
            throw new InvalidOperationException(FeatureDisabledMessage);
        }
        var existing = await this.voiceprintStore.GetAsync(tenantId, this.Context.ConnectionAborted).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var declined = new VoiceprintRecord(
            TenantId: tenantId,
            State: VoiceEnrollmentState.Declined,
            Embedding: null,
            EmbeddingDim: 0,
            ModelId: null,
            SampleCount: 0,
            CreatedAt: existing?.CreatedAt ?? now,
            ConfirmedAt: null,
            DeclinedAt: now,
            ThresholdOverride: existing?.ThresholdOverride,
            FeatureEnabled: existing?.FeatureEnabled ?? true);
        await this.enrollmentOrchestrator.WriteFromAdminAsync(declined, this.Context.ConnectionAborted).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetVoiceThresholdOverride(string tenantId, float? threshold)
    {
        if (this.enrollmentOrchestrator is null)
        {
            throw new InvalidOperationException(FeatureDisabledMessage);
        }
        var existing = await this.voiceprintStore.GetAsync(tenantId, this.Context.ConnectionAborted).ConfigureAwait(false);
        var record = existing is null
            ? NewRecord(tenantId, thresholdOverride: threshold)
            : existing with { ThresholdOverride = threshold };
        await this.enrollmentOrchestrator.WriteFromAdminAsync(record, this.Context.ConnectionAborted).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> StartVoiceEnrollment(string tenantId)
    {
        if (this.enrollmentOrchestrator is null)
        {
            return FeatureDisabledMessage;
        }
        var outcome = await this.enrollmentOrchestrator.TryStartAsync(tenantId, this.Context.ConnectionAborted).ConfigureAwait(false);
        return outcome switch
        {
            EnrollmentOutcome.Transitioned => null,
            EnrollmentOutcome.InvalidState i => $"Cannot start enrollment from state {i.Current}: {i.Reason}",
            _ => "Unexpected enrollment outcome.",
        };
    }

    /// <inheritdoc />
    public async Task SubmitVoiceprint(string tenantId, float[] embedding, string modelId)
    {
        if (this.enrollmentOrchestrator is null)
        {
            throw new InvalidOperationException(FeatureDisabledMessage);
        }
        await this.enrollmentOrchestrator.WriteVoiceprintAsync(tenantId, embedding, modelId, this.Context.ConnectionAborted).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task UpdateSpeakerIdConfig(SpeakerIdConfig config)
    {
        this.speakerIdSettingsStore.SetEnabled(config.Enabled);
        return Task.CompletedTask;
    }

    private static VoiceprintRecord NewRecord(string tenantId, bool featureEnabled = true, float? thresholdOverride = null)
        => new(
            TenantId: tenantId,
            State: VoiceEnrollmentState.Unknown,
            Embedding: null,
            EmbeddingDim: 0,
            ModelId: null,
            SampleCount: 0,
            CreatedAt: DateTimeOffset.UtcNow,
            ConfirmedAt: null,
            DeclinedAt: null,
            ThresholdOverride: thresholdOverride,
            FeatureEnabled: featureEnabled);

    private static VoiceprintSnapshotDto ToDto(VoiceprintRecord record)
        => new(
            TenantId: record.TenantId,
            StateName: record.State.ToString(),
            Embedding: record.Embedding,
            EmbeddingDim: record.EmbeddingDim,
            ModelId: record.ModelId,
            ThresholdOverride: record.ThresholdOverride,
            FeatureEnabled: record.FeatureEnabled);
}
