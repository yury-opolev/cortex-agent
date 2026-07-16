using System.Text.Json;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>
/// Maps a Cortex <see cref="LlmCompletionRequest"/> onto the OpenAI Responses
/// protocol request body. System messages collapse into <c>instructions</c>;
/// user/assistant/tool turns become ordered <c>input</c> items.
/// </summary>
internal static class OpenAiResponsesRequestMapper
{
    private const string SystemSeparator = "\n\n";

    internal static OpenAiResponsesRequest Build(LlmCompletionRequest request)
    {
        var systemTexts = new List<string>();
        var input = new List<OpenAiResponsesInputItem>();

        // Pair function_call / function_call_output items by call ID before mapping.
        // Orphaned calls (no matching result) and orphaned results (no matching call
        // or empty ID) are dropped so the Responses API does not reject the request
        // with HTTP 400 — mirroring Chat's SanitizeToolCalls semantics.
        var pairedCallIds = ComputePairedCallIds(request.Messages);

        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case "system":
                    var systemText = ExtractText(message);
                    if (!string.IsNullOrEmpty(systemText))
                    {
                        systemTexts.Add(systemText);
                    }

                    break;

                case "user":
                    var userParts = BuildUserContent(message);
                    if (userParts.Count > 0)
                    {
                        input.Add(new OpenAiResponsesMessageItem { Role = "user", Content = userParts });
                    }

                    break;

                case "assistant":
                    var assistantText = ExtractText(message);
                    if (!string.IsNullOrEmpty(assistantText))
                    {
                        input.Add(new OpenAiResponsesMessageItem
                        {
                            Role = "assistant",
                            Content = [new OpenAiResponsesContentPart { Type = "output_text", Text = assistantText }],
                        });
                    }

                    if (message.ToolCalls is { Count: > 0 })
                    {
                        foreach (var toolCall in message.ToolCalls)
                        {
                            if (!pairedCallIds.Contains(toolCall.Id))
                            {
                                continue;
                            }

                            input.Add(new OpenAiResponsesFunctionCallItem
                            {
                                CallId = toolCall.Id,
                                Name = toolCall.Name,
                                Arguments = toolCall.Arguments,
                            });
                        }
                    }

                    break;

                case "tool":
                    if (message.ToolCallId is { Length: > 0 } toolCallId
                        && pairedCallIds.Contains(toolCallId))
                    {
                        input.Add(new OpenAiResponsesFunctionCallOutputItem
                        {
                            CallId = toolCallId,
                            Output = message.Content ?? string.Empty,
                        });
                    }

                    break;
            }
        }

        return new OpenAiResponsesRequest
        {
            Model = request.Model,
            Stream = false,
            Instructions = systemTexts.Count > 0 ? string.Join(SystemSeparator, systemTexts) : null,
            Input = input,
            Tools = BuildTools(request.Tools),
        };
    }

    /// <summary>
    /// Determines which tool call IDs form a complete function_call/function_call_output
    /// pair. A call ID is kept only when its assistant tool-call group is fully
    /// responded: every call in the group has a matching tool result with a non-empty
    /// call ID. Partially-responded groups are dropped atomically so no orphaned call
    /// or result survives. Matching is ordinal by call ID.
    /// </summary>
    private static HashSet<string> ComputePairedCallIds(IReadOnlyList<LlmMessage> messages)
    {
        var respondedCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role == "tool" && message.ToolCallId is { Length: > 0 } toolCallId)
            {
                respondedCallIds.Add(toolCallId);
            }
        }

        var pairedCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role != "assistant" || message.ToolCalls is not { Count: > 0 } toolCalls)
            {
                continue;
            }

            if (toolCalls.All(toolCall => respondedCallIds.Contains(toolCall.Id)))
            {
                foreach (var toolCall in toolCalls)
                {
                    pairedCallIds.Add(toolCall.Id);
                }
            }
        }

        return pairedCallIds;
    }

    private static List<OpenAiResponsesContentPart> BuildUserContent(LlmMessage message)
    {
        var parts = new List<OpenAiResponsesContentPart>();

        if (message.ContentBlocks is { Count: > 0 })
        {
            foreach (var block in message.ContentBlocks)
            {
                if (block.Type == "image" && block.ImageData is not null && block.ImageMediaType is not null)
                {
                    parts.Add(new OpenAiResponsesContentPart
                    {
                        Type = "input_image",
                        ImageUrl = $"data:{block.ImageMediaType};base64,{block.ImageData}",
                    });
                }
                else if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
                {
                    parts.Add(new OpenAiResponsesContentPart { Type = "input_text", Text = block.Text });
                }
            }
        }
        else if (!string.IsNullOrEmpty(message.Content))
        {
            parts.Add(new OpenAiResponsesContentPart { Type = "input_text", Text = message.Content });
        }

        return parts;
    }

    private static List<OpenAiResponsesTool>? BuildTools(IReadOnlyList<LlmToolDefinition>? tools)
    {
        if (tools is not { Count: > 0 })
        {
            return null;
        }

        return tools.Select(tool => new OpenAiResponsesTool
        {
            Type = "function",
            Name = tool.Name,
            Description = tool.Description,
            Parameters = JsonSerializer.Deserialize<JsonElement>(tool.ParametersSchema),
        }).ToList();
    }

    /// <summary>Plain text of a message: prefers <see cref="LlmMessage.Content"/>, else text blocks.</summary>
    private static string ExtractText(LlmMessage message)
    {
        if (!string.IsNullOrEmpty(message.Content))
        {
            return message.Content;
        }

        if (message.ContentBlocks is { Count: > 0 })
        {
            var texts = message.ContentBlocks
                .Where(block => block.Type == "text" && !string.IsNullOrEmpty(block.Text))
                .Select(block => block.Text!);
            return string.Join(SystemSeparator, texts);
        }

        return string.Empty;
    }
}
