namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Voice speaker identification methods (Phase 3) exposed by the agent.
/// The Bridge runs the verification gate alongside the audio capture path
/// and pulls the authoritative per-tenant voiceprint state from Agent.Host
/// via these methods. Agent.Host pushes invalidations through
/// <see cref="IAgentHubClient.OnVoiceprintInvalidated"/>.
/// Part of the composed <see cref="IAgentHub"/> surface — these methods share the
/// single SignalR hub connection and route by method name.
/// </summary>
public interface IVoiceIdHub
{
    /// <summary>Fetch the current voiceprint snapshot for the given tenant. Returns null when no record exists.</summary>
    Task<VoiceprintSnapshotDto?> GetVoiceprint(string tenantId);

    /// <summary>Admin action from the Bridge Web UI: flip the feature flag.</summary>
    Task SetVoiceFeatureEnabled(string tenantId, bool enabled);

    /// <summary>Admin action from the Bridge Web UI: wipe the voiceprint and move state to Declined.</summary>
    Task ResetVoiceEnrollment(string tenantId);

    /// <summary>Admin action from the Bridge Web UI: set the per-tenant cosine threshold override; pass null to use the default.</summary>
    Task SetVoiceThresholdOverride(string tenantId, float? threshold);

    /// <summary>
    /// Admin / slash-command action: start enrollment for this tenant.
    /// Transitions Unknown/Declined to Enrolling; returns an error string when
    /// invoked from any other state.
    /// </summary>
    Task<string?> StartVoiceEnrollment(string tenantId);

    /// <summary>
    /// Bridge pushes the finished enrollment voiceprint — computed Bridge-side via
    /// the shared embedder during the wizard — for the Agent to store. Transitions
    /// the tenant to Enrolled. No raw audio crosses the hub, only the vector.
    /// </summary>
    Task SubmitVoiceprint(string tenantId, float[] embedding, string modelId);
}
