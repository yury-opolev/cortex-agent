namespace Cortex.Contained.Agent.Host.SpeakerId;

using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Speech.SpeakerId;

/// <summary>
/// Shared helpers for the voice-enrollment tool family. Centralises the
/// conversation-prefix check and tenant extraction so each tool can stay
/// focused on its specific orchestrator call.
/// </summary>
internal static class VoiceEnrollmentToolHelpers
{
    public const string VoiceConversationPrefix = "discord-voice-";

    /// <summary>
    /// Extracts the tenant id from a voice conversation id. Returns false
    /// (with a populated <paramref name="errorResult"/>) if the conversation
    /// isn't voice-scoped.
    /// </summary>
    public static bool TryGetTenantId(ToolExecutionContext context, string toolName, out string tenantId, out AgentToolResult errorResult)
    {
        if (!context.ConversationId.StartsWith(VoiceConversationPrefix, StringComparison.Ordinal))
        {
            tenantId = string.Empty;
            errorResult = AgentToolResult.Fail($"{toolName} is only available in voice conversations.");
            return false;
        }

        tenantId = context.ConversationId[VoiceConversationPrefix.Length..];
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            errorResult = AgentToolResult.Fail($"{toolName}: conversation id lacks a tenant suffix.");
            return false;
        }

        errorResult = null!;
        return true;
    }

    /// <summary>
    /// Maps an <see cref="EnrollmentOutcome"/> to an <see cref="AgentToolResult"/>.
    /// Transitions and confirmations are "success"; invalid-state and errors are
    /// surfaced as failures with a structured JSON body the LLM can react to.
    /// </summary>
    public static AgentToolResult OutcomeToResult(EnrollmentOutcome outcome)
    {
        return outcome switch
        {
            EnrollmentOutcome.Transitioned t => AgentToolResult.Ok(JsonSerializer.Serialize(new
                {
                    transitioned = true,
                    from = t.From.ToString(),
                    to = t.To.ToString(),
                    guidance = t.Guidance,
                })),
            EnrollmentOutcome.Confirmation c => AgentToolResult.Ok(JsonSerializer.Serialize(new
                {
                    result = c.Result.ToString().ToLowerInvariant(),
                    score = MathRound(c.Score),
                    matches_achieved = c.MatchesAchieved,
                    matches_required = c.MatchesRequired,
                    failures_in_a_row = c.FailuresInARow,
                    failures_allowed = c.FailuresAllowed,
                    new_state = c.NewState.ToString(),
                })),
            EnrollmentOutcome.InvalidState i => AgentToolResult.Fail($"invalid_state: current={i.Current}; {i.Reason}"),
            EnrollmentOutcome.InsufficientSamples ns => AgentToolResult.Fail($"insufficient_samples: required={ns.Required} available={ns.Available}. Ask the user to speak more."),
            EnrollmentOutcome.Errored e => AgentToolResult.Fail($"error: {e.Reason}"),
            _ => AgentToolResult.Fail("Unknown enrollment outcome."),
        };
    }

    private static string MathRound(float v)
        => v.ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>The set of voice-enrollment tool names that must be filtered to voice conversations.</summary>
    public static readonly string[] AllToolNames =
    [
        "start_voice_enrollment",
        "decline_voice_enrollment",
        "cancel_voice_enrollment",
        "request_voice_reenrollment",
        "confirm_voice_reenrollment",
        "forget_voice_enrollment",
    ];

    /// <summary>
    /// Returns the set of tools that should be visible to the LLM given the
    /// tenant's current enrollment state. Matches the spec's tool-exposure
    /// matrix exactly.
    /// </summary>
    public static IReadOnlySet<string> ToolsForState(VoiceEnrollmentState state) => state switch
    {
        VoiceEnrollmentState.Unknown => new HashSet<string>(StringComparer.Ordinal)
        {
            "start_voice_enrollment",
            "decline_voice_enrollment",
        },
        VoiceEnrollmentState.Declined => new HashSet<string>(StringComparer.Ordinal)
        {
            "start_voice_enrollment",
        },
        VoiceEnrollmentState.Enrolling => new HashSet<string>(StringComparer.Ordinal)
        {
            "cancel_voice_enrollment",
        },
        VoiceEnrollmentState.Confirming => new HashSet<string>(StringComparer.Ordinal)
        {
            "cancel_voice_enrollment",
        },
        VoiceEnrollmentState.Enrolled => new HashSet<string>(StringComparer.Ordinal)
        {
            "request_voice_reenrollment",
            "forget_voice_enrollment",
        },
        VoiceEnrollmentState.PendingReenroll => new HashSet<string>(StringComparer.Ordinal)
        {
            "confirm_voice_reenrollment",
            "cancel_voice_enrollment",
        },
        _ => new HashSet<string>(StringComparer.Ordinal),
    };
}
