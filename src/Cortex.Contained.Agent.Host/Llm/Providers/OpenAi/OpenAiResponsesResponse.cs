using System.Text;
using System.Text.Json;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>
/// Non-streaming OpenAI Responses API response body, plus the <see cref="Parse(string, ProviderState)"/>
/// entry point that projects it onto the shared <see cref="LlmCompletionResult"/>
/// contract. Also reused as the nested <c>response</c> object carried by terminal
/// streaming events (<c>response.completed</c>/<c>.incomplete</c>/<c>.failed</c>).
/// The internal wire DTOs never escape this type.
/// </summary>
internal sealed class OpenAiResponsesResponse
{
    /// <summary>Run status: <c>completed</c>, <c>failed</c>, or <c>incomplete</c>.</summary>
    public string? Status { get; set; }

    public List<OpenAiResponsesOutputItem>? Output { get; set; }

    public OpenAiResponsesUsage? Usage { get; set; }

    public OpenAiResponsesError? Error { get; set; }

    public OpenAiResponsesIncompleteDetails? IncompleteDetails { get; set; }

    /// <summary>
    /// Parses a non-streaming Responses body into an <see cref="LlmCompletionResult"/>,
    /// stamping <paramref name="provider"/>'s credential name as the served provider ID.
    /// </summary>
    internal static LlmCompletionResult Parse(string json, ProviderState provider)
        => Parse(json, provider.Credential.Name);

    /// <summary>
    /// Parses a non-streaming Responses body into an <see cref="LlmCompletionResult"/>.
    /// A <c>failed</c> status (or any <c>error</c> object) becomes a clean failure result
    /// carrying the provider's (truncated) error message; otherwise assistant
    /// <c>output_text</c> is joined and <c>function_call</c> items become
    /// <see cref="LlmToolCall"/>s with a <c>tool_calls</c>/<c>stop</c> finish reason.
    /// </summary>
    internal static LlmCompletionResult Parse(string json, string providerId)
    {
        OpenAiResponsesResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<OpenAiResponsesResponse>(
                json, ProviderClientHelpers.JsonOptions);
        }
        catch (JsonException)
        {
            response = null;
        }

        if (response is null)
        {
            return new LlmCompletionResult
            {
                Success = false,
                ErrorCode = "LlmError",
                ErrorMessage = "Failed to deserialize Responses output.",
                ProviderId = providerId,
            };
        }

        if (string.Equals(response.Status, "failed", StringComparison.Ordinal)
            || response.Error is not null)
        {
            var message = response.Error?.Message ?? "Responses request failed.";
            return new LlmCompletionResult
            {
                Success = false,
                ErrorCode = "LlmError",
                ErrorMessage = ProviderClientHelpers.TruncateError(message),
                ProviderId = providerId,
            };
        }

        var text = new StringBuilder();
        var toolCalls = new List<LlmToolCall>();

        if (response.Output is not null)
        {
            foreach (var item in response.Output)
            {
                switch (item.Type)
                {
                    case "message":
                        if (item.Content is not null)
                        {
                            foreach (var part in item.Content)
                            {
                                if (part.Type == "output_text" && part.Text is not null)
                                {
                                    text.Append(part.Text);
                                }
                            }
                        }

                        break;

                    case "function_call":
                        toolCalls.Add(new LlmToolCall
                        {
                            Id = item.CallId ?? string.Empty,
                            Name = item.Name ?? string.Empty,
                            Arguments = item.Arguments ?? "{}",
                        });
                        break;
                }
            }
        }

        return new LlmCompletionResult
        {
            Success = true,
            Content = text.Length > 0 ? text.ToString() : null,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            FinishReason = toolCalls.Count > 0 ? "tool_calls" : "stop",
            Usage = response.Usage?.ToTokenUsage(),
            ProviderId = providerId,
        };
    }
}
