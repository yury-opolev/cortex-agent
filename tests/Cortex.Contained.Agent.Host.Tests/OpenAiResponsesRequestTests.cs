using System.Text.Json;
using Cortex.Contained.Agent.Host.Llm.Providers;
using Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Tests for <see cref="OpenAiResponsesRequestMapper"/>: mapping a Cortex
/// <see cref="LlmCompletionRequest"/> onto the OpenAI Responses protocol body.
/// </summary>
public class OpenAiResponsesRequestTests
{
    private static LlmCompletionRequest BuildRequest() => new()
    {
        Model = "gpt-5.6-sol",
        Messages =
        [
            new LlmMessage { Role = "system", Content = "system text" },
            new LlmMessage
            {
                Role = "user",
                ContentBlocks =
                [
                    LlmContentBlock.TextBlock("hello there"),
                    LlmContentBlock.ImageBlock("AAAA", "image/png"),
                ],
            },
            new LlmMessage
            {
                Role = "assistant",
                Content = "sure thing",
                ToolCalls =
                [
                    new LlmToolCall { Id = "call_1", Name = "do_thing", Arguments = "{\"x\":1}" },
                ],
            },
            new LlmMessage { Role = "tool", ToolCallId = "call_1", Content = "result output" },
        ],
        Tools =
        [
            new LlmToolDefinition
            {
                Name = "do_thing",
                Description = "Does the thing.",
                ParametersSchema = """{"type":"object","properties":{"x":{"type":"integer"}}}""",
            },
        ],
        RequestId = "req-1",
        ConversationId = "conv-1",
    };

    [Fact]
    public void Build_MapsTopLevelBodyFields()
    {
        var body = OpenAiResponsesRequestMapper.Build(BuildRequest());

        Assert.Equal("gpt-5.6-sol", body.Model);
        Assert.False(body.Stream);
        Assert.Equal("system text", body.Instructions);
        Assert.Equal("function", Assert.Single(body.Tools!).Type);
    }

    [Fact]
    public void Build_PreservesConversationItemOrder()
    {
        var body = OpenAiResponsesRequestMapper.Build(BuildRequest());

        // user message, assistant message, assistant tool call, tool result — in order.
        Assert.Equal(4, body.Input.Count);
        Assert.Equal("message", body.Input[0].Type);
        Assert.Equal("message", body.Input[1].Type);
        Assert.Equal("function_call", body.Input[2].Type);
        Assert.Equal("function_call_output", body.Input[3].Type);
    }

    [Fact]
    public void Build_MapsUserTextAndImage()
    {
        var body = OpenAiResponsesRequestMapper.Build(BuildRequest());

        var user = Assert.IsType<OpenAiResponsesMessageItem>(body.Input[0]);
        Assert.Equal("user", user.Role);
        Assert.Collection(
            user.Content,
            text =>
            {
                Assert.Equal("input_text", text.Type);
                Assert.Equal("hello there", text.Text);
            },
            image =>
            {
                Assert.Equal("input_image", image.Type);
                Assert.Equal("data:image/png;base64,AAAA", image.ImageUrl);
            });
    }

    [Fact]
    public void Build_MapsAssistantTextToOutputText()
    {
        var body = OpenAiResponsesRequestMapper.Build(BuildRequest());

        var assistant = Assert.IsType<OpenAiResponsesMessageItem>(body.Input[1]);
        Assert.Equal("assistant", assistant.Role);
        var part = Assert.Single(assistant.Content);
        Assert.Equal("output_text", part.Type);
        Assert.Equal("sure thing", part.Text);
    }

    [Fact]
    public void Build_MapsAssistantToolCallToFunctionCall()
    {
        var body = OpenAiResponsesRequestMapper.Build(BuildRequest());

        var call = Assert.IsType<OpenAiResponsesFunctionCallItem>(body.Input[2]);
        Assert.Equal("call_1", call.CallId);
        Assert.Equal("do_thing", call.Name);
        Assert.Equal("{\"x\":1}", call.Arguments);
    }

    [Fact]
    public void Build_MapsToolResultToFunctionCallOutput()
    {
        var body = OpenAiResponsesRequestMapper.Build(BuildRequest());

        var output = Assert.IsType<OpenAiResponsesFunctionCallOutputItem>(body.Input[3]);
        Assert.Equal("call_1", output.CallId);
        Assert.Equal("result output", output.Output);
    }

    [Fact]
    public void Build_MapsToolDefinitionToFlatFunctionTool()
    {
        var body = OpenAiResponsesRequestMapper.Build(BuildRequest());

        var tool = Assert.Single(body.Tools!);
        Assert.Equal("function", tool.Type);
        Assert.Equal("do_thing", tool.Name);
        Assert.Equal("Does the thing.", tool.Description);
        Assert.Equal(JsonValueKind.Object, tool.Parameters!.Value.ValueKind);
        Assert.Equal("integer", tool.Parameters.Value
            .GetProperty("properties").GetProperty("x").GetProperty("type").GetString());
    }

    [Fact]
    public void Build_CombinesMultipleSystemMessagesInOrder()
    {
        var request = BuildRequest() with
        {
            Messages =
            [
                new LlmMessage { Role = "system", Content = "first" },
                new LlmMessage { Role = "system", Content = "second" },
                new LlmMessage { Role = "user", Content = "hi" },
            ],
        };

        var body = OpenAiResponsesRequestMapper.Build(request);

        Assert.Equal("first\n\nsecond", body.Instructions);
    }

    [Fact]
    public void Build_AssistantWithOnlyToolCall_EmitsNoEmptyMessage()
    {
        var request = BuildRequest() with
        {
            Messages =
            [
                new LlmMessage
                {
                    Role = "assistant",
                    Content = null,
                    ToolCalls =
                    [
                        new LlmToolCall { Id = "call_9", Name = "noop", Arguments = "{}" },
                    ],
                },
                new LlmMessage { Role = "tool", ToolCallId = "call_9", Content = "ok" },
            ],
        };

        var body = OpenAiResponsesRequestMapper.Build(request);

        var call = Assert.IsType<OpenAiResponsesFunctionCallItem>(body.Input[0]);
        Assert.Equal("call_9", call.CallId);
        Assert.DoesNotContain(body.Input, item => item is OpenAiResponsesMessageItem);
    }

    [Fact]
    public void Build_AssistantToolCallWithoutResult_OmitsCallButKeepsText()
    {
        // Orphaned function_call (no matching later tool result) must be dropped
        // so Responses does not reject the request with HTTP 400. Assistant text
        // is preserved.
        var request = BuildRequest() with
        {
            Messages =
            [
                new LlmMessage
                {
                    Role = "assistant",
                    Content = "let me look that up",
                    ToolCalls =
                    [
                        new LlmToolCall { Id = "call_x", Name = "search", Arguments = "{}" },
                    ],
                },
            ],
        };

        var body = OpenAiResponsesRequestMapper.Build(request);

        var message = Assert.IsType<OpenAiResponsesMessageItem>(Assert.Single(body.Input));
        Assert.Equal("assistant", message.Role);
        Assert.Equal("let me look that up", Assert.Single(message.Content).Text);
        Assert.DoesNotContain(body.Input, item => item is OpenAiResponsesFunctionCallItem);
    }

    [Fact]
    public void Build_OrphanToolResult_WithoutPriorCall_IsOmitted()
    {
        var request = BuildRequest() with
        {
            Messages =
            [
                new LlmMessage { Role = "user", Content = "hi" },
                new LlmMessage { Role = "tool", ToolCallId = "call_orphan", Content = "stale result" },
            ],
        };

        var body = OpenAiResponsesRequestMapper.Build(request);

        var user = Assert.IsType<OpenAiResponsesMessageItem>(Assert.Single(body.Input));
        Assert.Equal("user", user.Role);
    }

    [Fact]
    public void Build_ToolResultWithEmptyCallId_IsOmitted()
    {
        var request = BuildRequest() with
        {
            Messages =
            [
                new LlmMessage { Role = "user", Content = "hi" },
                new LlmMessage { Role = "tool", ToolCallId = string.Empty, Content = "result" },
                new LlmMessage { Role = "tool", ToolCallId = null, Content = "result" },
            ],
        };

        var body = OpenAiResponsesRequestMapper.Build(request);

        var user = Assert.IsType<OpenAiResponsesMessageItem>(Assert.Single(body.Input));
        Assert.Equal("user", user.Role);
    }

    [Fact]
    public void Build_FullyPairedToolCall_RemainsInOrder()
    {
        var request = BuildRequest() with
        {
            Messages =
            [
                new LlmMessage
                {
                    Role = "assistant",
                    Content = "calling now",
                    ToolCalls =
                    [
                        new LlmToolCall { Id = "call_1", Name = "do_thing", Arguments = "{}" },
                    ],
                },
                new LlmMessage { Role = "tool", ToolCallId = "call_1", Content = "done" },
            ],
        };

        var body = OpenAiResponsesRequestMapper.Build(request);

        Assert.Equal(3, body.Input.Count);
        Assert.Equal("message", body.Input[0].Type);
        var call = Assert.IsType<OpenAiResponsesFunctionCallItem>(body.Input[1]);
        Assert.Equal("call_1", call.CallId);
        var output = Assert.IsType<OpenAiResponsesFunctionCallOutputItem>(body.Input[2]);
        Assert.Equal("call_1", output.CallId);
    }

    [Fact]
    public void Build_PartiallyRespondedToolCallGroup_StripsEntireGroupAtomically()
    {
        // One assistant turn requests two tool calls but only one has a result.
        // The whole group must be stripped so no orphaned call/output survives,
        // while the assistant's text stays.
        var request = BuildRequest() with
        {
            Messages =
            [
                new LlmMessage
                {
                    Role = "assistant",
                    Content = "running two tools",
                    ToolCalls =
                    [
                        new LlmToolCall { Id = "call_a", Name = "a", Arguments = "{}" },
                        new LlmToolCall { Id = "call_b", Name = "b", Arguments = "{}" },
                    ],
                },
                new LlmMessage { Role = "tool", ToolCallId = "call_a", Content = "a done" },
            ],
        };

        var body = OpenAiResponsesRequestMapper.Build(request);

        var message = Assert.IsType<OpenAiResponsesMessageItem>(Assert.Single(body.Input));
        Assert.Equal("running two tools", Assert.Single(message.Content).Text);
        Assert.DoesNotContain(body.Input, item => item is OpenAiResponsesFunctionCallItem);
        Assert.DoesNotContain(body.Input, item => item is OpenAiResponsesFunctionCallOutputItem);
    }

    [Fact]
    public void Build_SerializesToResponsesJsonShape()
    {
        var body = OpenAiResponsesRequestMapper.Build(BuildRequest());

        var json = JsonSerializer.Serialize(body, ProviderClientHelpers.JsonOptions);

        Assert.Contains("\"input_text\"", json, StringComparison.Ordinal);
        Assert.Contains("\"input_image\"", json, StringComparison.Ordinal);
        Assert.Contains("\"image_url\":\"data:image/png;base64,AAAA\"", json, StringComparison.Ordinal);
        Assert.Contains("\"output_text\"", json, StringComparison.Ordinal);
        Assert.Contains("\"function_call\"", json, StringComparison.Ordinal);
        Assert.Contains("\"function_call_output\"", json, StringComparison.Ordinal);
        Assert.Contains("\"call_id\":\"call_1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"instructions\":\"system text\"", json, StringComparison.Ordinal);

        // The Responses body must NOT carry token-budget fields.
        Assert.DoesNotContain("max_output_tokens", json, StringComparison.Ordinal);
        Assert.DoesNotContain("max_tokens", json, StringComparison.Ordinal);
    }
}
