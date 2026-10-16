namespace Cortex.Contained.Bridge.Channels;

/// <summary>
/// Composes the channel text for an agent error: the sanitized message, optionally
/// followed by the raw provider response for tenants that opt into full LLM errors.
/// The detail is capped so the whole message fits a Discord post (2000 chars).
/// </summary>
public static class LlmErrorDetailFormatter
{
    /// <summary>Maximum characters of provider detail shown in chat.</summary>
    public const int MaxDetailLength = 1500;

    public static string Compose(string message, string? detail, bool showDetail)
    {
        if (!showDetail || string.IsNullOrWhiteSpace(detail))
        {
            return message;
        }

        var trimmedDetail = detail.Trim();
        if (trimmedDetail.Length > MaxDetailLength)
        {
            trimmedDetail = string.Concat(trimmedDetail.AsSpan(0, MaxDetailLength), "…");
        }

        return $"{message}\n\nProvider response: {trimmedDetail}";
    }
}
