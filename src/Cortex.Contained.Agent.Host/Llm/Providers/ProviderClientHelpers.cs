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

    /// <summary>Masks a token for safe logging: shows prefix and suffix only.</summary>
    internal static string MaskToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "<empty>";
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
