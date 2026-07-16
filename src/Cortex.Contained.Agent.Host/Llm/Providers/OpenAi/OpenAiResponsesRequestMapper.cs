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
                    input.Add(new OpenAiResponsesFunctionCallOutputItem
                    {
                        CallId = message.ToolCallId ?? string.Empty,
                        Output = message.Content ?? string.Empty,
                    });

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
