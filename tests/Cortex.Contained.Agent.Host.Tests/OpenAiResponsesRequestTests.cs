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
            ],
        };

        var body = OpenAiResponsesRequestMapper.Build(request);

        var call = Assert.IsType<OpenAiResponsesFunctionCallItem>(Assert.Single(body.Input));
        Assert.Equal("call_9", call.CallId);
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
