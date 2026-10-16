using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Llm.Providers;

/// <summary>Shared statics for the provider API clients and the facade.</summary>
internal static class ProviderClientHelpers
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string Truncate(string? s)
        => s is null ? string.Empty : s.Length <= 200 ? s : s[..200] + "…";

    internal static string TruncateError(string error)
    {
        const int maxLength = 500;
        return error.Length <= maxLength ? error : string.Concat(error.AsSpan(0, maxLength), "...");
    }

    /// <summary>
    /// Request headers whose values carry credentials (case-insensitive). Their values are
    /// masked by <see cref="FormatHeadersForLog"/> — e.g. the GitHub Copilot
    /// <c>Authorization: Bearer gho_…</c> header and the Anthropic <c>x-api-key</c> header.
    /// </summary>
    private static readonly FrozenSet<string> sensitiveHeaderNames = FrozenSet.ToFrozenSet(
        ["Authorization", "Proxy-Authorization", "x-api-key", "api-key", "Cookie"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Formats request headers as <c>name=value, …</c> for an error-context log line,
    /// masking every credential-bearing header so a live token never reaches the logs.
    /// </summary>
    internal static string FormatHeadersForLog(HttpHeaders headers)
    {
        return string.Join(", ", headers.Select(header => sensitiveHeaderNames.Contains(header.Key)
            ? $"{header.Key}={MaskToken(header.Value.FirstOrDefault() ?? string.Empty)}"
            : $"{header.Key}={string.Join(",", header.Value)}"));
    }

    /// <summary>Masks a token for safe logging: shows prefix and suffix only.</summary>
    internal static string MaskToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "<empty>";
        }

        if (token.Length <= 8)
        {
            return "****";
        }

        if (token.Length <= 20)
        {
            return token[..4] + "****";
        }

        return token[..10] + "****" + token[^4..];
    }

    internal static async IAsyncEnumerable<LlmStreamChunk> ErrorStream(string errorMessage)
    {
        yield return new LlmStreamChunk { IsComplete = true, ErrorMessage = errorMessage };
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
