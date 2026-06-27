namespace Cortex.Contained.Agent.Host.SpeakerId;

using Cortex.Contained.Agent.Host.Tools;

internal sealed class StartVoiceEnrollmentTool(EnrollmentOrchestrator orchestrator) : IAgentTool
{
    public string Name => "start_voice_enrollment";
    public string Description => "Begin voice-identification enrollment. Valid from Unknown or Declined. The orchestrator will start capturing the user's next utterances as voice samples; tell the user to say a few sentences after calling this.";
    public string ParametersSchema => """{ "type": "object", "properties": {} }""";

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!VoiceEnrollmentToolHelpers.TryGetTenantId(context, this.Name, out var tenantId, out var error))
        {
            return error;
        }
        var outcome = await orchestrator.TryStartAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return VoiceEnrollmentToolHelpers.OutcomeToResult(outcome);
    }
}

internal sealed class DeclineVoiceEnrollmentTool(EnrollmentOrchestrator orchestrator) : IAgentTool
{
    public string Name => "decline_voice_enrollment";
    public string Description => "Record that the user has declined voice-identification enrollment. Valid from Unknown only. After calling this, the bot will not proactively re-offer enrollment.";
    public string ParametersSchema => """{ "type": "object", "properties": {} }""";

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!VoiceEnrollmentToolHelpers.TryGetTenantId(context, this.Name, out var tenantId, out var error))
        {
            return error;
        }
        var outcome = await orchestrator.TryDeclineAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return VoiceEnrollmentToolHelpers.OutcomeToResult(outcome);
    }
}

internal sealed class CancelVoiceEnrollmentTool(EnrollmentOrchestrator orchestrator) : IAgentTool
{
    public string Name => "cancel_voice_enrollment";
    public string Description => "Abort an in-progress enrollment or reenrollment-pending state. From Enrolling/Confirming returns to Unknown; from PendingReenroll restores Enrolled (voiceprint untouched).";
    public string ParametersSchema => """{ "type": "object", "properties": {} }""";

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!VoiceEnrollmentToolHelpers.TryGetTenantId(context, this.Name, out var tenantId, out var error))
        {
            return error;
        }
        var outcome = await orchestrator.TryCancelAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return VoiceEnrollmentToolHelpers.OutcomeToResult(outcome);
    }
}

internal sealed class RequestVoiceReenrollmentTool(EnrollmentOrchestrator orchestrator) : IAgentTool
{
    public string Name => "request_voice_reenrollment";
    public string Description => "Ask the user to confirm they want to replace their existing voiceprint. Valid from Enrolled only. Existing voiceprint stays active until the user confirms via confirm_voice_reenrollment.";
    public string ParametersSchema => """{ "type": "object", "properties": {} }""";

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!VoiceEnrollmentToolHelpers.TryGetTenantId(context, this.Name, out var tenantId, out var error))
        {
            return error;
        }
        var outcome = await orchestrator.TryRequestReenrollAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return VoiceEnrollmentToolHelpers.OutcomeToResult(outcome);
    }
}

internal sealed class ConfirmVoiceReenrollmentTool(EnrollmentOrchestrator orchestrator) : IAgentTool
{
    public string Name => "confirm_voice_reenrollment";
    public string Description => "User has explicitly confirmed they want to replace their voiceprint. Wipes the existing voiceprint and starts a fresh capture (Enrolling). Only call after request_voice_reenrollment and explicit yes from the user.";
    public string ParametersSchema => """{ "type": "object", "properties": {} }""";

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!VoiceEnrollmentToolHelpers.TryGetTenantId(context, this.Name, out var tenantId, out var error))
        {
            return error;
        }
        var outcome = await orchestrator.TryConfirmReenrollAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return VoiceEnrollmentToolHelpers.OutcomeToResult(outcome);
    }
}

internal sealed class ForgetVoiceEnrollmentTool(EnrollmentOrchestrator orchestrator) : IAgentTool
{
    public string Name => "forget_voice_enrollment";
    public string Description => "Wipe the user's voiceprint and stop the verification gate (transitions to Declined). Use only when the user explicitly asks to disable voice identification.";
    public string ParametersSchema => """{ "type": "object", "properties": {} }""";

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!VoiceEnrollmentToolHelpers.TryGetTenantId(context, this.Name, out var tenantId, out var error))
        {
            return error;
        }
        var outcome = await orchestrator.TryForgetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return VoiceEnrollmentToolHelpers.OutcomeToResult(outcome);
    }
}
