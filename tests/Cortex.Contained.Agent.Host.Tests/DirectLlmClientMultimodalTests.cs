using System.Text.Json;
using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;
using Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Tests for multimodal (image/vision) support in <see cref="DirectLlmClient"/>:
/// verifying that <c>BuildAnthropicMessages</c> and <c>BuildOpenAiRequestBody</c>
/// correctly translate <see cref="LlmContentBlock"/> image/text blocks into the
/// provider-specific JSON structures.
/// </summary>
public class DirectLlmClientMultimodalTests
{
    private static readonly JsonSerializerOptions SnakeCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static LlmCompletionRequest MakeRequest(params LlmMessage[] messages) =>
        new()
        {
            Model = "gpt-4o",
            Messages = messages,
            RequestId = "test-req",
            ConversationId = "test-conv",
        };

    private static LlmCompletionRequest MakeRequest(int maxTokens, params LlmMessage[] messages) =>
        new()
        {
            Model = "gpt-4o",
            MaxTokens = maxTokens,
            Messages = messages,
            RequestId = "test-req",
            ConversationId = "test-conv",
        };

    // ── BuildAnthropicMessages ──────────────────────────────────────────

    [Fact]
    public void BuildAnthropicMessages_TextOnly_NoContentBlocks()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "Hello" },
        };

        var (system, result) = AnthropicApiClient.BuildAnthropicMessages(messages);

        Assert.Null(system);
        Assert.Single(result);
        Assert.Equal("user", result[0].Role);
        Assert.Single(result[0].Content);
        Assert.Equal("text", result[0].Content[0].Type);
        Assert.Equal("Hello", result[0].Content[0].Text);
    }

    [Fact]
    public void BuildAnthropicMessages_WithImageContentBlocks_EmitsImageSource()
    {
        var messages = new List<LlmMessage>
        {
            new()
            {
                Role = "user",
                ContentBlocks =
                [
                    LlmContentBlock.ImageBlock("aW1hZ2VkYXRh", "image/png"),
                    LlmContentBlock.TextBlock("What is this?"),
                ],
            },
        };

        var (system, result) = AnthropicApiClient.BuildAnthropicMessages(messages);

        Assert.Null(system);
        Assert.Single(result);
        Assert.Equal("user", result[0].Role);
        Assert.Equal(2, result[0].Content.Count);

        // First block: image
        var imageBlock = result[0].Content[0];
        Assert.Equal("image", imageBlock.Type);
        Assert.NotNull(imageBlock.Source);
        Assert.Equal("base64", imageBlock.Source!.Type);
        Assert.Equal("image/png", imageBlock.Source.MediaType);
        Assert.Equal("aW1hZ2VkYXRh", imageBlock.Source.Data);

        // Second block: text
        var textBlock = result[0].Content[1];
        Assert.Equal("text", textBlock.Type);
        Assert.Equal("What is this?", textBlock.Text);
    }

    [Fact]
    public void BuildAnthropicMessages_MultipleImages_AllEmitted()
    {
        var messages = new List<LlmMessage>
        {
            new()
            {
                Role = "user",
                ContentBlocks =
                [
                    LlmContentBlock.ImageBlock("img1data", "image/jpeg"),
                    LlmContentBlock.ImageBlock("img2data", "image/webp"),
                    LlmContentBlock.TextBlock("Compare these images"),
                ],
            },
        };

        var (_, result) = AnthropicApiClient.BuildAnthropicMessages(messages);

        Assert.Single(result);
        Assert.Equal(3, result[0].Content.Count);
        Assert.Equal("image", result[0].Content[0].Type);
        Assert.Equal("image/jpeg", result[0].Content[0].Source!.MediaType);
        Assert.Equal("image", result[0].Content[1].Type);
        Assert.Equal("image/webp", result[0].Content[1].Source!.MediaType);
        Assert.Equal("text", result[0].Content[2].Type);
    }

    [Fact]
    public void BuildAnthropicMessages_ContentBlocks_TakesPrecedenceOverContent()
    {
        var messages = new List<LlmMessage>
        {
            new()
            {
                Role = "user",
                Content = "This should be ignored",
                ContentBlocks =
                [
                    LlmContentBlock.TextBlock("This should be used"),
                ],
            },
        };

        var (_, result) = AnthropicApiClient.BuildAnthropicMessages(messages);

        Assert.Single(result);
        Assert.Single(result[0].Content);
        Assert.Equal("This should be used", result[0].Content[0].Text);
    }

    [Fact]
    public void BuildAnthropicMessages_ImageBlockWithMissingData_Skipped()
    {
        var messages = new List<LlmMessage>
        {
            new()
            {
                Role = "user",
                Content = "fallback",
                ContentBlocks =
                [
                    new LlmContentBlock { Type = "image", ImageData = null, ImageMediaType = "image/png" },
                ],
            },
        };

        var (_, result) = AnthropicApiClient.BuildAnthropicMessages(messages);

        // Empty content blocks fall through to text Content
        Assert.Single(result);
        Assert.Equal("fallback", result[0].Content[0].Text);
    }

    [Fact]
    public void BuildAnthropicMessages_EmptyContentBlocks_FallsBackToContent()
    {
        var messages = new List<LlmMessage>
        {
            new()
            {
                Role = "user",
                Content = "plain text",
                ContentBlocks = [],
            },
        };

        var (_, result) = AnthropicApiClient.BuildAnthropicMessages(messages);

        Assert.Single(result);
        Assert.Equal("plain text", result[0].Content[0].Text);
    }

    [Fact]
    public void BuildAnthropicMessages_SystemMessage_ExtractedSeparately()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are helpful." },
            new()
            {
                Role = "user",
                ContentBlocks =
                [
                    LlmContentBlock.ImageBlock("data", "image/gif"),
                    LlmContentBlock.TextBlock("Describe this"),
                ],
            },
        };

        var (system, result) = AnthropicApiClient.BuildAnthropicMessages(messages);

        Assert.Equal("You are helpful.", system);
        Assert.Single(result);
        Assert.Equal("user", result[0].Role);
    }

    // ── BuildOpenAiRequestBody ──────────────────────────────────────────

    [Fact]
    public void BuildOpenAiRequestBody_TextOnly_ContentIsString()
    {
        var request = MakeRequest(new LlmMessage { Role = "user", Content = "Hello" });

        var body = OpenAiCompatibleApiClient.BuildOpenAiRequestBody(request, stream: false, apiType: "openai");

        Assert.Single(body.Messages);
        Assert.IsType<string>(body.Messages[0].Content);
        Assert.Equal("Hello", body.Messages[0].Content);
    }

    [Fact]
    public void BuildOpenAiRequestBody_WithImageContentBlocks_ContentIsList()
    {
        var request = MakeRequest(
            new LlmMessage
            {
                Role = "user",
                ContentBlocks =
                [
                    LlmContentBlock.ImageBlock("aW1hZ2U=", "image/png"),
                    LlmContentBlock.TextBlock("What is this?"),
                ],
            });

        var body = OpenAiCompatibleApiClient.BuildOpenAiRequestBody(request, stream: false, apiType: "openai");

        Assert.Single(body.Messages);
        var content = body.Messages[0].Content;
        Assert.NotNull(content);

        var parts = Assert.IsAssignableFrom<List<OpenAiContentPart>>(content);
        Assert.Equal(2, parts.Count);

        Assert.Equal("image_url", parts[0].Type);
        Assert.NotNull(parts[0].ImageUrl);
        Assert.Equal("data:image/png;base64,aW1hZ2U=", parts[0].ImageUrl!.Url);

        Assert.Equal("text", parts[1].Type);
        Assert.Equal("What is this?", parts[1].Text);
    }

    [Fact]
    public void BuildOpenAiRequestBody_MultipleImages_AllIncluded()
    {
        var request = MakeRequest(
            new LlmMessage
            {
                Role = "user",
                ContentBlocks =
                [
                    LlmContentBlock.ImageBlock("img1", "image/jpeg"),
                    LlmContentBlock.ImageBlock("img2", "image/webp"),
                    LlmContentBlock.TextBlock("Compare"),
                ],
            });

        var body = OpenAiCompatibleApiClient.BuildOpenAiRequestBody(request, stream: false, apiType: "openai");

        var parts = Assert.IsAssignableFrom<List<OpenAiContentPart>>(body.Messages[0].Content);
        Assert.Equal(3, parts.Count);
        Assert.Equal("data:image/jpeg;base64,img1", parts[0].ImageUrl!.Url);
        Assert.Equal("data:image/webp;base64,img2", parts[1].ImageUrl!.Url);
        Assert.Equal("Compare", parts[2].Text);
    }

    [Fact]
    public void BuildOpenAiRequestBody_EmptyContentBlocks_FallsBackToContent()
    {
        var request = MakeRequest(
            new LlmMessage
            {
                Role = "user",
                Content = "plain text",
                ContentBlocks = [],
            });

        var body = OpenAiCompatibleApiClient.BuildOpenAiRequestBody(request, stream: false, apiType: "openai");

        Assert.IsType<string>(body.Messages[0].Content);
        Assert.Equal("plain text", body.Messages[0].Content);
    }

    [Fact]
    public void BuildOpenAiRequestBody_ContentBlocksWithInvalidImage_SkipsInvalidPart()
    {
        var request = MakeRequest(
            new LlmMessage
            {
                Role = "user",
                Content = "fallback",
                ContentBlocks =
                [
                    new LlmContentBlock { Type = "image", ImageData = null, ImageMediaType = "image/png" },
                    LlmContentBlock.TextBlock("Valid text"),
                ],
            });

        var body = OpenAiCompatibleApiClient.BuildOpenAiRequestBody(request, stream: false, apiType: "openai");

        var parts = Assert.IsAssignableFrom<List<OpenAiContentPart>>(body.Messages[0].Content);
        Assert.Single(parts);
        Assert.Equal("text", parts[0].Type);
        Assert.Equal("Valid text", parts[0].Text);
    }

    [Fact]
    public void BuildOpenAiRequestBody_CopilotApi_UsesMaxTokens()
    {
        var request = MakeRequest(
            4096,
            new LlmMessage
            {
                Role = "user",
                ContentBlocks = [LlmContentBlock.TextBlock("test")],
            });

        var body = OpenAiCompatibleApiClient.BuildOpenAiRequestBody(request, stream: false, apiType: "github-copilot-api");

        Assert.Equal(4096, body.MaxTokens);
        Assert.Null(body.MaxCompletionTokens);
    }

    [Fact]
    public void BuildOpenAiRequestBody_OpenAiApi_UsesMaxCompletionTokens()
    {
        var request = MakeRequest(
            4096,
            new LlmMessage
            {
                Role = "user",
                ContentBlocks = [LlmContentBlock.TextBlock("test")],
            });

        var body = OpenAiCompatibleApiClient.BuildOpenAiRequestBody(request, stream: false, apiType: "openai");

        Assert.Null(body.MaxTokens);
        Assert.Equal(4096, body.MaxCompletionTokens);
    }

    // ── JSON Serialization Verification ─────────────────────────────────

    [Fact]
    public void BuildOpenAiRequestBody_ImageContent_SerializesToCorrectJson()
    {
        var request = MakeRequest(
            new LlmMessage
            {
                Role = "user",
                ContentBlocks =
                [
                    LlmContentBlock.ImageBlock("dGVzdA==", "image/png"),
                    LlmContentBlock.TextBlock("Describe"),
                ],
            });

        var body = OpenAiCompatibleApiClient.BuildOpenAiRequestBody(request, stream: false, apiType: "openai");

        var json = JsonSerializer.Serialize(body, SnakeCaseOptions);
        var doc = JsonDocument.Parse(json);

        var contentArray = doc.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content");

        Assert.Equal(JsonValueKind.Array, contentArray.ValueKind);
        Assert.Equal(2, contentArray.GetArrayLength());

        var imagePart = contentArray[0];
        Assert.Equal("image_url", imagePart.GetProperty("type").GetString());
        Assert.Equal(
            "data:image/png;base64,dGVzdA==",
            imagePart.GetProperty("image_url").GetProperty("url").GetString());

        var textPart = contentArray[1];
        Assert.Equal("text", textPart.GetProperty("type").GetString());
        Assert.Equal("Describe", textPart.GetProperty("text").GetString());
    }

    [Fact]
    public void BuildOpenAiRequestBody_TextOnlyContent_SerializesAsString()
    {
        var request = MakeRequest(new LlmMessage { Role = "user", Content = "Just text" });

        var body = OpenAiCompatibleApiClient.BuildOpenAiRequestBody(request, stream: false, apiType: "openai");

        var json = JsonSerializer.Serialize(body, SnakeCaseOptions);
        var doc = JsonDocument.Parse(json);

        var contentElement = doc.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content");

        Assert.Equal(JsonValueKind.String, contentElement.ValueKind);
        Assert.Equal("Just text", contentElement.GetString());
    }

    [Fact]
    public void BuildAnthropicMessages_ImageContent_SerializesToCorrectJson()
    {
        var messages = new List<LlmMessage>
        {
            new()
            {
                Role = "user",
                ContentBlocks =
                [
                    LlmContentBlock.ImageBlock("dGVzdA==", "image/jpeg"),
                    LlmContentBlock.TextBlock("What is this?"),
                ],
            },
        };

        var (_, result) = AnthropicApiClient.BuildAnthropicMessages(messages);

        var json = JsonSerializer.Serialize(result[0], SnakeCaseOptions);
        var doc = JsonDocument.Parse(json);

        var contentArray = doc.RootElement.GetProperty("content");
        Assert.Equal(2, contentArray.GetArrayLength());

        // Image block
        var imageBlock = contentArray[0];
        Assert.Equal("image", imageBlock.GetProperty("type").GetString());
        var source = imageBlock.GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/jpeg", source.GetProperty("media_type").GetString());
        Assert.Equal("dGVzdA==", source.GetProperty("data").GetString());

        // Text block
        var textBlock = contentArray[1];
        Assert.Equal("text", textBlock.GetProperty("type").GetString());
        Assert.Equal("What is this?", textBlock.GetProperty("text").GetString());
    }
}
