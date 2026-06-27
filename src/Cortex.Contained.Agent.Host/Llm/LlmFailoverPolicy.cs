namespace Cortex.Contained.Agent.Host.Llm;

/// <summary>
/// Decides whether a provider failure is eligible to fail over to the next
/// configured provider. Pure — unit-tested in isolation. Conservative by
/// design: only fail over what is plausibly *provider-side* (transport, 5xx,
/// auth/entitlement, rate-limit). Request-fault errors (bad/oversized request,
/// content policy) would fail identically on any provider, so failing over
/// just wastes latency/cost and masks real bugs — those return false.
/// </summary>
public static class LlmFailoverPolicy
{
    // Request-fault markers — the request itself is bad; another provider
    // won't help. Checked regardless of status (some providers 200-wrap, or
    // use odd codes), so a doomed request never burns the fallback chain.
    private static readonly string[] TerminalBodyMarkers =
    [
        "context_length", "maximum context", "context window exceeded",
        "content_policy", "invalid_request",
    ];

    // Provider-side identity/entitlement/auth markers — exactly the 2026-05-19
    // Copilot "permission_denied / 403" outage. Another provider may work.
    private static readonly string[] AuthBodyMarkers =
    [
        "permission_denied", "forbidden", "unauthorized", "invalid_api_key",
    ];

    /// <param name="httpStatus">HTTP status, or null for a transport failure.</param>
    /// <param name="errorBody">Response/error body, if any.</param>
    /// <param name="transportException">True if the call threw before a
    /// response (HttpRequestException / timeout / socket).</param>
    public static bool ShouldFailover(int? httpStatus, string? errorBody, bool transportException)
    {
        if (transportException)
        {
            return true;
        }

        var body = errorBody ?? string.Empty;

        // 1. Request-fault body → terminal, never fail over (even if 5xx-wrapped).
        if (ContainsAny(body, TerminalBodyMarkers))
        {
            return false;
        }

        // 2. Explicit request-fault statuses → terminal.
        if (httpStatus is 400 or 422)
        {
            return false;
        }

        // 3. Provider-side: any 5xx (covers the 500-wrapping-403 outage).
        if (httpStatus is >= 500 and <= 599)
        {
            return true;
        }

        // 4. Provider-side: auth / rate-limit statuses.
        if (httpStatus is 401 or 403 or 429)
        {
            return true;
        }

        // 5. Provider-side auth/entitlement signalled only in the body.
        if (ContainsAny(body, AuthBodyMarkers))
        {
            return true;
        }

        // 6. Anything else (other 4xx, unknown) — conservative: don't fail over.
        return false;
    }

    /// <summary>
    /// Decides whether a failure is worth retrying the SAME provider after a short backoff.
    /// Narrower than <see cref="ShouldFailover"/>: a transient blip (transport/timeout, any
    /// 5xx, or a 429 rate-limit) clears on retry, but a bad request or an auth/permission
    /// failure (401/403) fails identically on retry — those are left to failover or
    /// surfacing instead of burning latency on doomed retries.
    /// </summary>
    public static bool ShouldRetrySameProvider(int? httpStatus, string? errorBody, bool transportException)
    {
        if (transportException)
        {
            return true;
        }

        var body = errorBody ?? string.Empty;

        // Request-fault body → terminal, never retry (even if 5xx-wrapped).
        if (ContainsAny(body, TerminalBodyMarkers))
        {
            return false;
        }

        // Explicit request-fault statuses → terminal.
        if (httpStatus is 400 or 422)
        {
            return false;
        }

        // Transient provider-side: any 5xx, or a rate-limit (retry with backoff).
        return httpStatus is (>= 500 and <= 599) or 429;
    }

    private static bool ContainsAny(string haystack, string[] needles)
    {
        foreach (var n in needles)
        {
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
