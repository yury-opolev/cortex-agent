namespace Cortex.Contained.Agent.Host.Llm.Providers.Copilot;

/// <summary>
/// Selects the <see cref="CopilotEndpoint"/> for a model purely from its provider-reported
/// endpoint metadata (<see cref="Contracts.Config.LlmModelDefinition.SupportedEndpoints"/>).
/// <para>
/// Selection is deterministic with a fixed priority — <c>Responses</c> &gt; <c>Messages</c> &gt;
/// <c>ChatCompletions</c> — applied regardless of the order the endpoints appear in. Matching is
/// exact and ordinal-ignore-case; websocket-only variants such as <c>ws:/responses</c> are not
/// treated as HTTP support. Missing, null, empty, or unrecognized metadata falls back to
/// <see cref="CopilotEndpoint.ChatCompletions"/>. No model-name heuristics are used.
/// </para>
/// </summary>
internal static class CopilotEndpointResolver
{
    /// <summary>
    /// Resolves the endpoint for <paramref name="endpoints"/> using the fixed priority
    /// Responses &gt; Messages &gt; ChatCompletions. Returns <see cref="CopilotEndpoint.ChatCompletions"/>
    /// when <paramref name="endpoints"/> is <see langword="null"/>, empty, or contains no recognized value.
    /// </summary>
    public static CopilotEndpoint Resolve(IReadOnlyList<string?>? endpoints)
    {
        endpoints ??= [];

        if (Contains(endpoints, "/responses") || Contains(endpoints, "/v1/responses"))
        {
            return CopilotEndpoint.Responses;
        }

        if (Contains(endpoints, "/v1/messages"))
        {
            return CopilotEndpoint.Messages;
        }

        return CopilotEndpoint.ChatCompletions;
    }

    private static bool Contains(IReadOnlyList<string?> endpoints, string endpoint)
    {
        foreach (var candidate in endpoints)
        {
            if (string.Equals(candidate, endpoint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
