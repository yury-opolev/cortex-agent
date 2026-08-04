namespace Cortex.Contained.Agent.Host.Llm.Providers.Copilot;

/// <summary>
/// Detects a Copilot rejection that means "this model is not served on the endpoint you used",
/// and names the endpoint to try instead.
/// <para>
/// Endpoint routing comes from metadata the Bridge pushes, which is a snapshot taken at credential
/// push time. Copilot moves models between endpoints (models such as gpt-5.6-sol are
/// Responses-only and answer HTTP 400 on /chat/completions), so a stale snapshot pins every call
/// to an endpoint the model no longer serves. coda refetches GET /models to recover; the Agent
/// Host holds no credentials for that call, so it learns from the rejection instead.
/// </para>
/// <para>
/// Detection is deliberately narrow — the status must be a request-fault status AND the body must
/// name an endpoint/model support problem — so an ordinary bad request (oversized prompt, malformed
/// messages) is never re-sent to a second endpoint.
/// </para>
/// </summary>
internal static class CopilotEndpointMismatch
{
    /// <summary>Bodies that indicate the model is not served on the attempted endpoint.</summary>
    private static readonly string[] MismatchMarkers =
    [
        "model_not_supported", "unsupported_model", "unsupported_endpoint",
        "not supported on this endpoint", "does not support",
        "is not supported", "not supported for this model",
    ];

    /// <summary>Bodies that are ordinary request faults and must never trigger a second endpoint.</summary>
    private static readonly string[] ExclusionMarkers =
    [
        "context_length", "context window", "maximum context", "content_policy",
    ];

    internal static bool IsMismatch(int status, string? body)
    {
        if (status is not (400 or 404))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        foreach (var exclusion in ExclusionMarkers)
        {
            if (body.Contains(exclusion, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        foreach (var marker in MismatchMarkers)
        {
            if (body.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The endpoint to try after <paramref name="attempted"/> was rejected, or
    /// <see langword="null"/> when there is no in-client alternative. Only Responses and Chat
    /// Completions are swappable — Messages models are dispatched to the Anthropic client by the
    /// facade, so this client never has a Messages request to re-shape.
    /// </summary>
    internal static CopilotEndpoint? Alternate(CopilotEndpoint attempted) => attempted switch
    {
        CopilotEndpoint.ChatCompletions => CopilotEndpoint.Responses,
        CopilotEndpoint.Responses => CopilotEndpoint.ChatCompletions,
        _ => null,
    };
}
