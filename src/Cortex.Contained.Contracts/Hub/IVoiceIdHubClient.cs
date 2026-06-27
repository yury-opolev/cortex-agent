namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Voice speaker identification callbacks the agent pushes to the Bridge.
/// Part of the composed <see cref="IAgentHubClient"/> surface — these callbacks
/// share the single SignalR hub connection and route by method name.
/// </summary>
public interface IVoiceIdHubClient
{
    /// <summary>
    /// The agent detected the voice gender from a personality description.
    /// The Bridge updates the tenant's <c>VoiceGender</c> setting accordingly.
    /// </summary>
    Task OnVoiceGenderDetected(string gender);

    /// <summary>
    /// The agent has written a new voiceprint record for this tenant (a state
    /// transition, threshold change, or feature toggle). The Bridge evicts its
    /// cached snapshot so the next verification call refetches via
    /// <see cref="IAgentHub.GetVoiceprint"/>.
    /// </summary>
    Task OnVoiceprintInvalidated(string tenantId);

    /// <summary>
    /// Pushed by Agent.Host on enrollment state changes:
    /// captured sample count increments, wizard state transitions, or wizard
    /// aborts. Bridges consuming this event surface progress to the user.
    /// </summary>
    /// <param name="tenantId">The tenant whose enrollment state changed.</param>
    /// <param name="stateName">The new <c>VoiceEnrollmentState</c> name (e.g. "Enrolling", "Confirming", "Enrolled", "Unknown").</param>
    /// <param name="capturedSamples">Number of samples accepted so far in this enrollment session.</param>
    /// <param name="requiredSamples">Total samples required (typically 3 + 2 for confirm).</param>
    Task OnVoiceEnrollmentProgress(string tenantId, string stateName, int capturedSamples, int requiredSamples);
}
