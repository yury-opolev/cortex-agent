using System.Text.Json;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Cortex.Contained.Contracts.Messages;

namespace Cortex.Contained.Contracts.Tests;

/// <summary>
/// Shared JSON options matching what SignalR uses by default.
/// </summary>
internal static class SerializationFixture
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Serialize, then deserialize, returning the round-tripped object.</summary>
    internal static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidOperationException("Deserialization returned null.");
    }
}

#region Messages

public class InboundMessageTests
{
    private static InboundMessage CreateSample() => new()
    {
        MessageId = "msg-1",
        ConversationId = "conv-1",
        ChannelId = "discord-main",
        ChannelType = ChannelType.Discord,
        Sender = new SenderInfo { Id = "sender-hash-abc", DisplayName = "Alice", IsVerified = true },
        Content = new MessageContent { Text = "Hello!", IsMarkdown = false },
        Timestamp = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero),
        ReplyToMessageId = "msg-0",
        ThreadId = "thread-1",
        IsGroup = true,
        Properties = new Dictionary<string, string> { ["pushName"] = "Alice" },
    };

    [Fact]
    public void Json_RoundTrip_PreservesAllFields()
    {
        var original = CreateSample();
        var result = SerializationFixture.RoundTrip(original);

        Assert.Equal(original.MessageId, result.MessageId);
        Assert.Equal(original.ConversationId, result.ConversationId);
        Assert.Equal(original.ChannelId, result.ChannelId);
        Assert.Equal(original.ChannelType, result.ChannelType);
        Assert.Equal(original.Sender.Id, result.Sender.Id);
        Assert.Equal(original.Sender.DisplayName, result.Sender.DisplayName);
        Assert.Equal(original.Sender.IsVerified, result.Sender.IsVerified);
        Assert.Equal(original.Content.Text, result.Content.Text);
        Assert.Equal(original.Timestamp, result.Timestamp);
        Assert.Equal(original.ReplyToMessageId, result.ReplyToMessageId);
        Assert.Equal(original.ThreadId, result.ThreadId);
        Assert.Equal(original.IsGroup, result.IsGroup);
        Assert.NotNull(result.Properties);
        Assert.Equal("Alice", result.Properties["pushName"]);
    }

    [Fact]
    public void Record_Equality_Works()
    {
        var a = CreateSample();
        var b = CreateSample();
        // Records with identical values should be equal
        // (Properties uses reference equality for IReadOnlyDictionary, so we test without it)
        var aNoProps = a with { Properties = null };
        var bNoProps = b with { Properties = null };
        Assert.Equal(aNoProps, bNoProps);
    }

    [Fact]
    public void With_Expression_Creates_Modified_Copy()
    {
        var original = CreateSample();
        var modified = original with { MessageId = "msg-2", IsGroup = false };

        Assert.Equal("msg-2", modified.MessageId);
        Assert.False(modified.IsGroup);
        // Unchanged fields remain
        Assert.Equal(original.ConversationId, modified.ConversationId);
    }

    [Fact]
    public void Nullable_Fields_Default_To_Null()
    {
        var minimal = new InboundMessage
        {
            MessageId = "m",
            ConversationId = "c",
            ChannelId = "ch",
            ChannelType = ChannelType.WebChat,
            Sender = new SenderInfo { Id = "s" },
            Content = new MessageContent { Text = "hi" },
            Timestamp = DateTimeOffset.UtcNow,
        };

        Assert.Null(minimal.ReplyToMessageId);
        Assert.Null(minimal.ThreadId);
        Assert.False(minimal.IsGroup);
        Assert.Null(minimal.Properties);
    }
}

public class OutboundMessageTests
{
    [Fact]
    public void Json_RoundTrip_PreservesAllFields()
    {
        var original = new OutboundMessage
        {
            MessageId = "out-1",
            ConversationId = "conv-1",
            ChannelId = "web-main",
            Content = new MessageContent { Text = "Reply!", IsMarkdown = true },
            ReplyToMessageId = "msg-1",
            ThreadId = "t-1",
        };

        var result = SerializationFixture.RoundTrip(original);

        Assert.Equal(original.MessageId, result.MessageId);
        Assert.Equal(original.ConversationId, result.ConversationId);
        Assert.Equal(original.ChannelId, result.ChannelId);
        Assert.Equal(original.Content.Text, result.Content.Text);
        Assert.True(result.Content.IsMarkdown);
        Assert.Equal(original.ReplyToMessageId, result.ReplyToMessageId);
        Assert.Equal(original.ThreadId, result.ThreadId);
    }
}

public class MessageContentTests
{
    [Fact]
    public void Json_RoundTrip_WithAttachments()
    {
        var content = new MessageContent
        {
            Text = "See attached",
            IsMarkdown = false,
            Attachments =
            [
                new MediaAttachment
                {
                    MimeType = "image/jpeg",
                    FileName = "photo.jpg",
                    SizeBytes = 102_400,
                    Url = "https://example.com/photo.jpg",
                    Caption = "A nice photo",
                },
            ],
        };

        var result = SerializationFixture.RoundTrip(content);

        Assert.Equal("See attached", result.Text);
        Assert.NotNull(result.Attachments);
        Assert.Single(result.Attachments);
        var att = result.Attachments[0];
        Assert.Equal("image/jpeg", att.MimeType);
        Assert.Equal("photo.jpg", att.FileName);
        Assert.Equal(102_400, att.SizeBytes);
        Assert.Equal("https://example.com/photo.jpg", att.Url);
        Assert.Equal("A nice photo", att.Caption);
        Assert.Null(att.Data);
    }

    [Fact]
    public void Json_RoundTrip_WithBinaryData()
    {
        byte[] data = [0x89, 0x50, 0x4E, 0x47]; // PNG magic bytes
        var content = new MessageContent
        {
            Attachments =
            [
                new MediaAttachment { MimeType = "image/png", Data = data },
            ],
        };

        var result = SerializationFixture.RoundTrip(content);

        Assert.NotNull(result.Attachments);
        Assert.NotNull(result.Attachments[0].Data);
        Assert.Equal(data, result.Attachments[0].Data);
    }

    [Fact]
    public void TextOnly_Content_HasNoAttachments()
    {
        var content = new MessageContent { Text = "Just text" };
        Assert.Null(content.Attachments);
        Assert.False(content.IsMarkdown);
    }
}

public class SendResultTests
{
    [Fact]
    public void Json_RoundTrip_Success()
    {
        var result = new SendResult
        {
            Success = true,
            ExternalMessageId = "ext-123",
        };

        var rt = SerializationFixture.RoundTrip(result);
        Assert.True(rt.Success);
        Assert.Equal("ext-123", rt.ExternalMessageId);
        Assert.Null(rt.ErrorMessage);
    }

    [Fact]
    public void Json_RoundTrip_Failure()
    {
        var result = new SendResult
        {
            Success = false,
            ErrorMessage = "Channel offline",
        };

        var rt = SerializationFixture.RoundTrip(result);
        Assert.False(rt.Success);
        Assert.Equal("Channel offline", rt.ErrorMessage);
        Assert.Null(rt.ExternalMessageId);
    }
}

#endregion

#region Hub Types

public class HubInboundMessageTests
{
    [Fact]
    public void Json_RoundTrip_PreservesAllFields()
    {
        var msg = new HubInboundMessage
        {
            ConversationId = "conv-1",
            ChannelId = "webchat-default",
            SenderIdHash = "sha256-abc123",
            Text = "Hello agent!",
            Timestamp = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero),
            Attachments =
            [
                new MediaAttachment { MimeType = "image/png", Url = "file:///tmp/img.png" },
            ],
        };

        var rt = SerializationFixture.RoundTrip(msg);

        Assert.Equal(msg.ConversationId, rt.ConversationId);
        Assert.Equal(msg.ChannelId, rt.ChannelId);
        Assert.Equal(msg.SenderIdHash, rt.SenderIdHash);
        Assert.Equal(msg.Text, rt.Text);
        Assert.Equal(msg.Timestamp, rt.Timestamp);
        Assert.NotNull(rt.Attachments);
        Assert.Single(rt.Attachments);
    }
}

public class ResponseChunkMessageTests
{
    [Fact]
    public void Json_RoundTrip()
    {
        var chunk = new ResponseChunkMessage
        {
            ConversationId = "conv-1",
            Text = "Hello",
            SequenceNumber = 0,
            IsComplete = false,
        };

        var rt = SerializationFixture.RoundTrip(chunk);
        Assert.Equal("conv-1", rt.ConversationId);
        Assert.Equal("Hello", rt.Text);
        Assert.Equal(0, rt.SequenceNumber);
        Assert.False(rt.IsComplete);
    }
}

public class ResponseCompleteMessageTests
{
    [Fact]
    public void Json_RoundTrip_WithUsage()
    {
        var msg = new ResponseCompleteMessage
        {
            ConversationId = "conv-1",
            MessageId = "resp-1",
            FullText = "Here is the answer.",
            Timestamp = DateTimeOffset.UtcNow,
            Usage = new TokenUsage { PromptTokens = 100, CompletionTokens = 50, TotalTokens = 150 },
        };

        var rt = SerializationFixture.RoundTrip(msg);
        Assert.Equal("Here is the answer.", rt.FullText);
        Assert.NotNull(rt.Usage);
        Assert.Equal(100, rt.Usage.PromptTokens);
        Assert.Equal(50, rt.Usage.CompletionTokens);
        Assert.Equal(150, rt.Usage.TotalTokens);
    }
}

public class ToolExecutionMessageTests
{
    [Fact]
    public void Json_RoundTrip()
    {
        var msg = new ToolExecutionMessage
        {
            ConversationId = "conv-1",
            ToolName = "web_search",
            Status = ToolExecutionStatus.Completed,
            Input = """{"query": "weather"}""",
            Output = """{"result": "sunny"}""",
            Duration = TimeSpan.FromMilliseconds(350),
        };

        var rt = SerializationFixture.RoundTrip(msg);
        Assert.Equal("web_search", rt.ToolName);
        Assert.Equal(ToolExecutionStatus.Completed, rt.Status);
        Assert.Equal("""{"query": "weather"}""", rt.Input);
        Assert.Equal("""{"result": "sunny"}""", rt.Output);
        Assert.NotNull(rt.Duration);
        Assert.Equal(350, rt.Duration.Value.TotalMilliseconds);
    }

    [Fact]
    public void ToolExecutionStatus_Enum_Values()
    {
        Assert.Equal(0, (int)ToolExecutionStatus.Started);
        Assert.Equal(1, (int)ToolExecutionStatus.Completed);
        Assert.Equal(2, (int)ToolExecutionStatus.Failed);
    }
}

public class AgentErrorMessageTests
{
    [Fact]
    public void Json_RoundTrip()
    {
        var err = new AgentErrorMessage
        {
            ConversationId = "conv-1",
            ErrorCode = ErrorCodes.LlmTimeout,
            Message = "Provider timed out after 30s",
            IsRetryable = true,
        };

        var rt = SerializationFixture.RoundTrip(err);
        Assert.Equal(ErrorCodes.LlmTimeout, rt.ErrorCode);
        Assert.Equal("Provider timed out after 30s", rt.Message);
        Assert.True(rt.IsRetryable);
    }
}

public class AgentStatusInfoTests
{
    [Fact]
    public void Json_RoundTrip()
    {
        var info = new AgentStatusInfo
        {
            Status = AgentStatus.Processing,
            ActiveConversations = 3,
            CurrentModel = "gpt-4o",
            Uptime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        var rt = SerializationFixture.RoundTrip(info);
        Assert.Equal(AgentStatus.Processing, rt.Status);
        Assert.Equal(3, rt.ActiveConversations);
        Assert.Equal("gpt-4o", rt.CurrentModel);
    }

    [Fact]
    public void AgentStatus_Enum_Values()
    {
        Assert.Equal(0, (int)AgentStatus.Idle);
        Assert.Equal(1, (int)AgentStatus.Processing);
        Assert.Equal(2, (int)AgentStatus.Streaming);
        Assert.Equal(3, (int)AgentStatus.Error);
        Assert.Equal(4, (int)AgentStatus.ShuttingDown);
    }
}

public class ConversationInfoTests
{
    [Fact]
    public void Json_RoundTrip()
    {
        var conv = new ConversationInfo
        {
            ConversationId = "conv-1",
            CreatedAt = DateTimeOffset.UtcNow,
            LastMessageAt = DateTimeOffset.UtcNow,
            MessageCount = 42,
            Title = "Chat about weather",
        };

        var rt = SerializationFixture.RoundTrip(conv);
        Assert.Equal("conv-1", rt.ConversationId);
        Assert.Equal(42, rt.MessageCount);
        Assert.Equal("Chat about weather", rt.Title);
    }
}

public class HubChatMessageTests
{
    [Fact]
    public void Json_RoundTrip_WithToolCalls()
    {
        var msg = new HubChatMessage
        {
            MessageId = "msg-1",
            Role = "assistant",
            Text = "Let me search for that.",
            Timestamp = DateTimeOffset.UtcNow,
            ToolCalls =
            [
                new ToolExecutionMessage
                {
                    ConversationId = "conv-1",
                    ToolName = "web_search",
                    Status = ToolExecutionStatus.Completed,
                },
            ],
            Usage = new TokenUsage { PromptTokens = 200, CompletionTokens = 100, TotalTokens = 300 },
        };

        var rt = SerializationFixture.RoundTrip(msg);
        Assert.Equal("assistant", rt.Role);
        Assert.NotNull(rt.ToolCalls);
        Assert.Single(rt.ToolCalls);
        Assert.Equal("web_search", rt.ToolCalls[0].ToolName);
        Assert.NotNull(rt.Usage);
        Assert.Equal(300, rt.Usage.TotalTokens);
    }
}

public class AgentConfigUpdateTests
{
    [Fact]
    public void Json_RoundTrip_PartialUpdate()
    {
        // Only temperature is set
        var update = new AgentConfigUpdate { Temperature = 0.5 };

        var rt = SerializationFixture.RoundTrip(update);
        Assert.Null(rt.SystemPrompt);
        Assert.Null(rt.MaxTokens);
        Assert.Equal(0.5, rt.Temperature);
    }
}

public class HealthInfoTests
{
    [Fact]
    public void Json_RoundTrip()
    {
        var health = new HealthInfo
        {
            Healthy = true,
            Timestamp = DateTimeOffset.UtcNow,
            Version = "1.0.0",
        };

        var rt = SerializationFixture.RoundTrip(health);
        Assert.True(rt.Healthy);
        Assert.Equal("1.0.0", rt.Version);
    }
}

public class SendMessageResultTests
{
    [Fact]
    public void Json_RoundTrip_Accepted()
    {
        var result = new SendMessageResult { Accepted = true, ConversationId = "conv-1" };
        var rt = SerializationFixture.RoundTrip(result);
        Assert.True(rt.Accepted);
        Assert.Equal("conv-1", rt.ConversationId);
        Assert.Null(rt.RejectionReason);
    }

    [Fact]
    public void Json_RoundTrip_Rejected()
    {
        var result = new SendMessageResult { Accepted = false, RejectionReason = "Rate limited" };
        var rt = SerializationFixture.RoundTrip(result);
        Assert.False(rt.Accepted);
        Assert.Equal("Rate limited", rt.RejectionReason);
    }
}

public class ProactiveMessageTests
{
    [Fact]
    public void Json_RoundTrip_AllFields()
    {
        var msg = new ProactiveMessage
        {
            ConversationId = "conv-42",
            Text = "Hey! Just a reminder about your meeting.",
            CorrelationId = "corr-123",
        };

        var rt = SerializationFixture.RoundTrip(msg);
        Assert.Equal("conv-42", rt.ConversationId);
        Assert.Equal("Hey! Just a reminder about your meeting.", rt.Text);
        Assert.Equal("corr-123", rt.CorrelationId);
    }

    [Fact]
    public void Json_RoundTrip_NullOptionalFields()
    {
        var msg = new ProactiveMessage
        {
            Text = "Hello!",
        };

        var rt = SerializationFixture.RoundTrip(msg);
        Assert.Null(rt.ConversationId);
        Assert.Null(rt.CorrelationId);
        Assert.Equal("Hello!", rt.Text);
    }

    [Fact]
    public void With_Expression_PreservesUnchangedFields()
    {
        var original = new ProactiveMessage
        {
            ConversationId = "conv-1",
            Text = "Original text",
            CorrelationId = "corr-1",
        };

        var modified = original with { Text = "Updated text" };

        Assert.Equal("conv-1", modified.ConversationId);
        Assert.Equal("Updated text", modified.Text);
        Assert.Equal("corr-1", modified.CorrelationId);
    }
}

public class ProactiveMessageResultTests
{
    [Fact]
    public void Json_RoundTrip_Success()
    {
        var result = new ProactiveMessageResult
        {
            Success = true,
            ConversationId = "conv-42",
        };

        var rt = SerializationFixture.RoundTrip(result);
        Assert.True(rt.Success);
        Assert.Equal("conv-42", rt.ConversationId);
        Assert.Null(rt.Error);
    }

    [Fact]
    public void Json_RoundTrip_Failure()
    {
        var result = new ProactiveMessageResult
        {
            Success = false,
            Error = "Channel not found",
        };

        var rt = SerializationFixture.RoundTrip(result);
        Assert.False(rt.Success);
        Assert.Equal("Channel not found", rt.Error);
        Assert.Null(rt.ConversationId);
    }
}

#endregion

#region LLM Types

public class LlmCompletionRequestTests
{
    [Fact]
    public void Json_RoundTrip_WithTools()
    {
        var request = new LlmCompletionRequest
        {
            Model = "gpt-4o",
            Messages =
            [
                new LlmMessage { Role = "system", Content = "You are helpful." },
                new LlmMessage { Role = "user", Content = "What's the weather?" },
            ],
            Tools =
            [
                new LlmToolDefinition
                {
                    Name = "get_weather",
                    Description = "Get current weather",
                    ParametersSchema = """{"type":"object","properties":{"city":{"type":"string"}}}""",
                },
            ],
            Temperature = 0.3,
            MaxTokens = 4096,
            RequestId = "req-1",
            ConversationId = "conv-1",
        };

        var rt = SerializationFixture.RoundTrip(request);

        Assert.Equal("gpt-4o", rt.Model);
        Assert.Equal(2, rt.Messages.Count);
        Assert.Equal("system", rt.Messages[0].Role);
        Assert.Equal("You are helpful.", rt.Messages[0].Content);
        Assert.NotNull(rt.Tools);
        Assert.Single(rt.Tools);
        Assert.Equal("get_weather", rt.Tools[0].Name);
        Assert.Equal(0.3, rt.Temperature);
        Assert.Equal(4096, rt.MaxTokens);
        Assert.Equal("req-1", rt.RequestId);
    }

    [Fact]
    public void Default_Temperature_Is_0_7()
    {
        var request = new LlmCompletionRequest
        {
            Model = "gpt-4o",
            Messages = [new LlmMessage { Role = "user", Content = "Hi" }],
            RequestId = "r",
            ConversationId = "c",
        };

        Assert.Equal(0.7, request.Temperature);
    }

    [Fact]
    public void Default_MaxTokens_Is_8192()
    {
        var request = new LlmCompletionRequest
        {
            Model = "gpt-4o",
            Messages = [new LlmMessage { Role = "user", Content = "Hi" }],
            RequestId = "r",
            ConversationId = "c",
        };

        Assert.Equal(8192, request.MaxTokens);
    }
}

public class LlmMessageTests
{
    [Fact]
    public void Json_RoundTrip_AssistantWithToolCalls()
    {
        var msg = new LlmMessage
        {
            Role = "assistant",
            Content = null,
            ToolCalls =
            [
                new LlmToolCall
                {
                    Id = "call-1",
                    Name = "get_weather",
                    Arguments = """{"city":"London"}""",
                },
            ],
        };

        var rt = SerializationFixture.RoundTrip(msg);
        Assert.Equal("assistant", rt.Role);
        Assert.Null(rt.Content);
        Assert.NotNull(rt.ToolCalls);
        Assert.Single(rt.ToolCalls);
        Assert.Equal("call-1", rt.ToolCalls[0].Id);
        Assert.Equal("get_weather", rt.ToolCalls[0].Name);
        Assert.Equal("""{"city":"London"}""", rt.ToolCalls[0].Arguments);
    }

    [Fact]
    public void Json_RoundTrip_ToolResponseMessage()
    {
        var msg = new LlmMessage
        {
            Role = "tool",
            Content = """{"temp":"15C","condition":"cloudy"}""",
            ToolCallId = "call-1",
        };

        var rt = SerializationFixture.RoundTrip(msg);
        Assert.Equal("tool", rt.Role);
        Assert.Equal("call-1", rt.ToolCallId);
        Assert.NotNull(rt.Content);
    }
}

public class LlmContentBlockTests
{
    [Fact]
    public void TextBlock_SetsTypeAndText()
    {
        var block = LlmContentBlock.TextBlock("Hello");

        Assert.Equal("text", block.Type);
        Assert.Equal("Hello", block.Text);
        Assert.Null(block.ImageData);
        Assert.Null(block.ImageMediaType);
    }

    [Fact]
    public void ImageBlock_SetsTypeDataAndMediaType()
    {
        var block = LlmContentBlock.ImageBlock("aGVsbG8=", "image/png");

        Assert.Equal("image", block.Type);
        Assert.Equal("aGVsbG8=", block.ImageData);
        Assert.Equal("image/png", block.ImageMediaType);
        Assert.Null(block.Text);
    }

    [Fact]
    public void Json_RoundTrip_TextBlock()
    {
        var block = LlmContentBlock.TextBlock("test content");

        var rt = SerializationFixture.RoundTrip(block);
        Assert.Equal("text", rt.Type);
        Assert.Equal("test content", rt.Text);
        Assert.Null(rt.ImageData);
    }

    [Fact]
    public void Json_RoundTrip_ImageBlock()
    {
        var block = LlmContentBlock.ImageBlock("base64data==", "image/jpeg");

        var rt = SerializationFixture.RoundTrip(block);
        Assert.Equal("image", rt.Type);
        Assert.Equal("base64data==", rt.ImageData);
        Assert.Equal("image/jpeg", rt.ImageMediaType);
        Assert.Null(rt.Text);
    }

    [Fact]
    public void Record_Equality_Works()
    {
        var a = LlmContentBlock.TextBlock("hello");
        var b = LlmContentBlock.TextBlock("hello");
        var c = LlmContentBlock.TextBlock("world");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void LlmMessage_WithContentBlocks_Json_RoundTrip()
    {
        var msg = new LlmMessage
        {
            Role = "user",
            Content = "fallback text",
            ContentBlocks =
            [
                LlmContentBlock.ImageBlock("aW1hZ2U=", "image/png"),
                LlmContentBlock.TextBlock("What is in this image?"),
            ],
        };

        var rt = SerializationFixture.RoundTrip(msg);
        Assert.Equal("user", rt.Role);
        Assert.Equal("fallback text", rt.Content);
        Assert.NotNull(rt.ContentBlocks);
        Assert.Equal(2, rt.ContentBlocks.Count);
        Assert.Equal("image", rt.ContentBlocks[0].Type);
        Assert.Equal("aW1hZ2U=", rt.ContentBlocks[0].ImageData);
        Assert.Equal("image/png", rt.ContentBlocks[0].ImageMediaType);
        Assert.Equal("text", rt.ContentBlocks[1].Type);
        Assert.Equal("What is in this image?", rt.ContentBlocks[1].Text);
    }

    [Fact]
    public void LlmMessage_WithoutContentBlocks_HasNullProperty()
    {
        var msg = new LlmMessage
        {
            Role = "user",
            Content = "plain text only",
        };

        Assert.Null(msg.ContentBlocks);
    }

    [Fact]
    public void ImageBlock_CanCacheDescriptionLater()
    {
        var block = LlmContentBlock.ImageBlock("base64==", "image/png");
        Assert.Null(block.ImageDescription);

        block.ImageDescription = "A red sunset over the ocean.";

        Assert.Equal("A red sunset over the ocean.", block.ImageDescription);
        Assert.Equal("base64==", block.ImageData);
        Assert.Equal("image/png", block.ImageMediaType);
    }
}

public class LlmCompletionResultTests
{
    [Fact]
    public void Json_RoundTrip_Success()
    {
        var result = new LlmCompletionResult
        {
            Success = true,
            Content = "It's 15 degrees and cloudy in London.",
            FinishReason = "stop",
            Usage = new LlmTokenUsage { PromptTokens = 50, CompletionTokens = 20, TotalTokens = 70 },
            ProviderId = "openai",
        };

        var rt = SerializationFixture.RoundTrip(result);
        Assert.True(rt.Success);
        Assert.Equal("It's 15 degrees and cloudy in London.", rt.Content);
        Assert.Equal("stop", rt.FinishReason);
        Assert.NotNull(rt.Usage);
        Assert.Equal(70, rt.Usage.TotalTokens);
        Assert.Equal("openai", rt.ProviderId);
        Assert.Null(rt.ErrorCode);
    }

    [Fact]
    public void Json_RoundTrip_WithToolCalls()
    {
        var result = new LlmCompletionResult
        {
            Success = true,
            ToolCalls =
            [
                new LlmToolCall { Id = "tc-1", Name = "search", Arguments = "{}" },
            ],
            FinishReason = "tool_calls",
        };

        var rt = SerializationFixture.RoundTrip(result);
        Assert.NotNull(rt.ToolCalls);
        Assert.Single(rt.ToolCalls);
        Assert.Equal("tool_calls", rt.FinishReason);
    }

    [Fact]
    public void Json_RoundTrip_Error()
    {
        var result = new LlmCompletionResult
        {
            Success = false,
            ErrorCode = ErrorCodes.LlmProviderUnavailable,
            ErrorMessage = "OpenAI API returned 503",
        };

        var rt = SerializationFixture.RoundTrip(result);
        Assert.False(rt.Success);
        Assert.Equal(ErrorCodes.LlmProviderUnavailable, rt.ErrorCode);
        Assert.Equal("OpenAI API returned 503", rt.ErrorMessage);
    }
}

public class LlmStreamChunkTests
{
    [Fact]
    public void Json_RoundTrip_ContentDelta()
    {
        var chunk = new LlmStreamChunk
        {
            ContentDelta = "Hello",
            IsComplete = false,
        };

        var rt = SerializationFixture.RoundTrip(chunk);
        Assert.Equal("Hello", rt.ContentDelta);
        Assert.False(rt.IsComplete);
        Assert.Null(rt.ToolCallDeltas);
    }

    [Fact]
    public void Json_RoundTrip_ToolCallDeltas()
    {
        var chunk = new LlmStreamChunk
        {
            ToolCallDeltas =
            [
                new LlmToolCallDelta
                {
                    Index = 0,
                    Id = "tc-1",
                    Name = "search",
                    ArgumentsDelta = """{"q":""",
                },
            ],
        };

        var rt = SerializationFixture.RoundTrip(chunk);
        Assert.NotNull(rt.ToolCallDeltas);
        var delta = Assert.Single(rt.ToolCallDeltas);
        Assert.Equal(0, delta.Index);
        Assert.Equal("tc-1", delta.Id);
        Assert.Equal("search", delta.Name);
        Assert.Equal("""{"q":""", delta.ArgumentsDelta);
    }

    [Fact]
    public void Json_RoundTrip_ToolCallDeltas_Multiple()
    {
        var chunk = new LlmStreamChunk
        {
            ToolCallDeltas =
            [
                new LlmToolCallDelta { Index = 0, ArgumentsDelta = """{"a":1""" },
                new LlmToolCallDelta { Index = 1, Id = "tc-2", Name = "send_message" },
            ],
        };

        var rt = SerializationFixture.RoundTrip(chunk);
        Assert.NotNull(rt.ToolCallDeltas);
        Assert.Equal(2, rt.ToolCallDeltas.Count);
        Assert.Equal(0, rt.ToolCallDeltas[0].Index);
        Assert.Null(rt.ToolCallDeltas[0].Id);
        Assert.Equal(1, rt.ToolCallDeltas[1].Index);
        Assert.Equal("tc-2", rt.ToolCallDeltas[1].Id);
        Assert.Equal("send_message", rt.ToolCallDeltas[1].Name);
    }

    [Fact]
    public void Json_RoundTrip_FinalChunk()
    {
        var chunk = new LlmStreamChunk
        {
            IsComplete = true,
            FinishReason = "stop",
            Usage = new LlmTokenUsage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 },
        };

        var rt = SerializationFixture.RoundTrip(chunk);
        Assert.True(rt.IsComplete);
        Assert.Equal("stop", rt.FinishReason);
        Assert.NotNull(rt.Usage);
        Assert.Equal(15, rt.Usage.TotalTokens);
    }
}

public class LlmTokenUsageTests
{
    [Fact]
    public void Record_Equality()
    {
        var a = new LlmTokenUsage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 };
        var b = new LlmTokenUsage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Defaults_Are_Zero()
    {
        var usage = new LlmTokenUsage();
        Assert.Equal(0, usage.PromptTokens);
        Assert.Equal(0, usage.CompletionTokens);
        Assert.Equal(0, usage.TotalTokens);
    }
}

#endregion

#region Channel Types

public class ChannelTypeTests
{
    [Theory]
    [InlineData(ChannelType.WebChat, 0)]
    [InlineData(ChannelType.Teams, 2)]
    [InlineData(ChannelType.Telegram, 3)]
    [InlineData(ChannelType.Voice, 4)]
    [InlineData(ChannelType.Discord, 5)]
    public void Enum_Has_Expected_Values(ChannelType type, int expected)
    {
        Assert.Equal(expected, (int)type);
    }

    [Fact]
    public void Json_RoundTrip_Enum()
    {
        var rt = SerializationFixture.RoundTrip(ChannelType.Discord);
        Assert.Equal(ChannelType.Discord, rt);
    }
}

public class ChannelStatusTests
{
    [Theory]
    [InlineData(ChannelStatus.Disconnected, 0)]
    [InlineData(ChannelStatus.Connecting, 1)]
    [InlineData(ChannelStatus.Connected, 2)]
    [InlineData(ChannelStatus.Pairing, 3)]
    [InlineData(ChannelStatus.Reconnecting, 4)]
    [InlineData(ChannelStatus.Error, 5)]
    public void Enum_Has_Expected_Values(ChannelStatus status, int expected)
    {
        Assert.Equal(expected, (int)status);
    }
}

public class ChannelCapabilitiesTests
{
    [Fact]
    public void Json_RoundTrip()
    {
        var caps = new ChannelCapabilities
        {
            SupportsMedia = true,
            SupportsThreads = false,
            SupportsReactions = true,
            SupportsStreaming = true,
            SupportsRichText = true,
            SupportsGroups = true,
            SupportsEditing = false,
            SupportsDeletion = false,
            MaxMessageLength = 4096,
            SupportedMediaTypes = ["image/jpeg", "image/png", "audio/ogg"],
        };

        var rt = SerializationFixture.RoundTrip(caps);
        Assert.True(rt.SupportsMedia);
        Assert.False(rt.SupportsThreads);
        Assert.True(rt.SupportsReactions);
        Assert.Equal(4096, rt.MaxMessageLength);
        Assert.Equal(3, rt.SupportedMediaTypes.Count);
        Assert.Contains("audio/ogg", rt.SupportedMediaTypes);
    }

    [Fact]
    public void Defaults_Are_Sensible()
    {
        var caps = new ChannelCapabilities();
        Assert.False(caps.SupportsMedia);
        Assert.Equal(int.MaxValue, caps.MaxMessageLength);
        Assert.Empty(caps.SupportedMediaTypes);
    }
}

public class ChannelStatusChangeTests
{
    [Fact]
    public void Json_RoundTrip_WithoutException()
    {
        // ChannelStatusChange has an Exception field which is not JSON-serializable,
        // so we test without it (Exception should be null in serialization scenarios).
        var change = new ChannelStatusChange(
            ChannelStatus.Connecting,
            ChannelStatus.Connected,
            "Connection established"
        );

        var rt = SerializationFixture.RoundTrip(change);
        Assert.Equal(ChannelStatus.Connecting, rt.PreviousStatus);
        Assert.Equal(ChannelStatus.Connected, rt.CurrentStatus);
        Assert.Equal("Connection established", rt.Reason);
    }

    [Fact]
    public void Positional_Constructor_Works()
    {
        var change = new ChannelStatusChange(ChannelStatus.Connected, ChannelStatus.Error, "Timeout");
        Assert.Equal(ChannelStatus.Connected, change.PreviousStatus);
        Assert.Equal(ChannelStatus.Error, change.CurrentStatus);
        Assert.Equal("Timeout", change.Reason);
        Assert.Null(change.Error);
    }
}

#endregion

#region Config Types

public class AgentConfigTests
{
    private static AgentConfig CreateSample() => new()
    {
        Name = "Cortex",
        SystemPrompt = "You are Cortex, a personal assistant.",
        MaxTokens = 4096,
        Temperature = 0.5,
        Security = new SecurityConfig
        {
            HubToken = "test-token",
            RateLimiting = new RateLimitConfig
            {
                MaxAttempts = 5,
                WindowSeconds = 30,
                LockoutSeconds = 600,
            },
        },
        Sessions = new SessionConfig
        {
            MaxHistory = 50,
            IdleResetMinutes = 30,
        },
        AvailableModels =
        [
            new ModelDefinition { Id = "gpt-4o", ContextWindow = 128_000, MaxOutputTokens = 16_384 },
            new ModelDefinition { Id = "claude-sonnet-4-20250514", ContextWindow = 200_000 },
        ],
        EnabledTools = ["web_search", "calculator"],
    };

    [Fact]
    public void Json_RoundTrip_PreservesAllFields()
    {
        var config = CreateSample();
        var rt = SerializationFixture.RoundTrip(config);

        Assert.Equal("Cortex", rt.Name);
        Assert.Equal("You are Cortex, a personal assistant.", rt.SystemPrompt);
        Assert.Equal(4096, rt.MaxTokens);
        Assert.Equal(0.5, rt.Temperature);
        Assert.Equal("test-token", rt.Security.HubToken);
        Assert.Equal(5, rt.Security.RateLimiting.MaxAttempts);
        Assert.Equal(30, rt.Security.RateLimiting.WindowSeconds);
        Assert.Equal(600, rt.Security.RateLimiting.LockoutSeconds);
        Assert.Equal(50, rt.Sessions.MaxHistory);
        Assert.Equal(30, rt.Sessions.IdleResetMinutes);
        Assert.Equal(2, rt.AvailableModels.Count);
        Assert.Equal("gpt-4o", rt.AvailableModels[0].Id);
        Assert.Equal(128_000, rt.AvailableModels[0].ContextWindow);
        Assert.Equal(16_384, rt.AvailableModels[0].MaxOutputTokens);
        Assert.Equal(2, rt.EnabledTools.Count);
    }

    [Fact]
    public void Defaults_Are_Correct()
    {
        var config = new AgentConfig
        {
            Name = "Cortex",
            Security = new SecurityConfig { HubToken = "t" },
            Sessions = new SessionConfig(),
        };

        Assert.Equal(8192, config.MaxTokens);
        Assert.Equal(0.7, config.Temperature);
        Assert.Null(config.SystemPrompt);
        Assert.Empty(config.AvailableModels);
        Assert.Empty(config.EnabledTools);
    }
}

public class RateLimitConfigTests
{
    [Fact]
    public void Defaults_Are_Correct()
    {
        var config = new RateLimitConfig();
        Assert.Equal(10, config.MaxAttempts);
        Assert.Equal(60, config.WindowSeconds);
        Assert.Equal(300, config.LockoutSeconds);
    }
}

public class SessionConfigTests
{
    [Fact]
    public void Defaults_Are_Correct()
    {
        var config = new SessionConfig();
        Assert.Equal(100, config.MaxHistory);
        Assert.Equal(360, config.IdleResetMinutes);
    }
}

public class ModelDefinitionTests
{
    [Fact]
    public void Defaults_Are_Correct()
    {
        var model = new ModelDefinition { Id = "test" };
        Assert.Equal(128_000, model.ContextWindow);
        Assert.Equal(8_192, model.MaxOutputTokens);
    }
}

public class BridgeConfigTests
{
    [Fact]
    public void Json_RoundTrip_PreservesAllFields()
    {
        var config = new BridgeConfig
        {
            AgentHubUrl = "http://agent:5000/agent-hub",
            HubToken = "secret-token",
            WebUi = new WebUiConfig { Enabled = true, Port = 8080, BindAddress = "0.0.0.0" },
            LlmProviders =
            [
                new LlmProviderConfig
                {
                    Name = "openai",
                    Api = "openai-completions",
                    BaseUrl = "https://api.openai.com/v1",
                    ApiKey = "sk-encrypted",
                    Models = ["gpt-4o", "gpt-4o-mini"],
                    RateLimits = new LlmRateLimitConfig
                    {
                        RequestsPerMinute = 60,
                        TokensPerMinute = 100_000,
                    },
                },
            ],
            LlmProxy = new LlmProxyConfig
            {
                FallbackOrder = ["openai", "anthropic"],
                CostTracking = new CostTrackingConfig { Enabled = true, MonthlyBudgetUsd = 50.0m },
            },
            Channels = new Dictionary<string, ChannelConfig>
            {
                ["discord"] = new ChannelConfig
                {
                    Enabled = true,
                    Settings = new Dictionary<string, string> { ["guildId"] = "123456789" },
                },
            },
        };

        var rt = SerializationFixture.RoundTrip(config);

        Assert.Equal("http://agent:5000/agent-hub", rt.AgentHubUrl);
        Assert.Equal("secret-token", rt.HubToken);
        Assert.True(rt.WebUi.Enabled);
        Assert.Equal(8080, rt.WebUi.Port);
        Assert.Equal("0.0.0.0", rt.WebUi.BindAddress);
        Assert.Single(rt.LlmProviders);
        Assert.Equal("openai", rt.LlmProviders[0].Name);
        Assert.Equal("openai-completions", rt.LlmProviders[0].Api);
        Assert.Equal(2, rt.LlmProviders[0].Models.Count);
        var rateLimits = rt.LlmProviders[0].RateLimits;
        Assert.NotNull(rateLimits);
        Assert.Equal(60, rateLimits.RequestsPerMinute);
        Assert.Equal(2, rt.LlmProxy.FallbackOrder.Count);
        var costTracking = rt.LlmProxy.CostTracking;
        Assert.NotNull(costTracking);
        Assert.True(costTracking.Enabled);
        Assert.Equal(50.0m, costTracking.MonthlyBudgetUsd);
        Assert.True(rt.Channels.ContainsKey("discord"));
        Assert.True(rt.Channels["discord"].Enabled);
    }

    [Fact]
    public void Defaults_Are_Correct()
    {
        var config = new BridgeConfig
        {
            AgentHubUrl = "http://localhost:5000",
            HubToken = "t",
        };

        Assert.True(config.WebUi.Enabled);
        Assert.Equal(5080, config.WebUi.Port);
        Assert.Equal("127.0.0.1", config.WebUi.BindAddress);
        Assert.Empty(config.LlmProviders);
        Assert.Empty(config.LlmProxy.FallbackOrder);
        Assert.Null(config.LlmProxy.CostTracking);
        Assert.Empty(config.Channels);
    }
}

public class ChannelConfigTests
{
    [Fact]
    public void Defaults_Are_Correct()
    {
        var config = new ChannelConfig();
        Assert.False(config.Enabled);
        Assert.Empty(config.Settings);
    }
}

#endregion

#region Error Codes

public class ErrorCodesTests
{
    [Theory]
    [InlineData(nameof(ErrorCodes.AuthFailed), "AUTH_FAILED")]
    [InlineData(nameof(ErrorCodes.RateLimited), "RATE_LIMITED")]
    [InlineData(nameof(ErrorCodes.InvalidMessage), "INVALID_MESSAGE")]
    [InlineData(nameof(ErrorCodes.ConversationNotFound), "CONVERSATION_NOT_FOUND")]
    [InlineData(nameof(ErrorCodes.AgentBusy), "AGENT_BUSY")]
    [InlineData(nameof(ErrorCodes.LlmError), "LLM_ERROR")]
    [InlineData(nameof(ErrorCodes.LlmTimeout), "LLM_TIMEOUT")]
    [InlineData(nameof(ErrorCodes.LlmProviderUnavailable), "LLM_PROVIDER_UNAVAILABLE")]
    [InlineData(nameof(ErrorCodes.LlmBudgetExceeded), "LLM_BUDGET_EXCEEDED")]
    [InlineData(nameof(ErrorCodes.ToolError), "TOOL_ERROR")]
    [InlineData(nameof(ErrorCodes.ChannelError), "CHANNEL_ERROR")]
    [InlineData(nameof(ErrorCodes.ConfigError), "CONFIG_ERROR")]
    [InlineData(nameof(ErrorCodes.InternalError), "INTERNAL_ERROR")]
    [InlineData(nameof(ErrorCodes.MessageTooLong), "MESSAGE_TOO_LONG")]
    [InlineData(nameof(ErrorCodes.UnsupportedMediaType), "UNSUPPORTED_MEDIA_TYPE")]
    public void ErrorCode_Has_Expected_Value(string fieldName, string expectedValue)
    {
        var field = typeof(ErrorCodes).GetField(fieldName);
        Assert.NotNull(field);
        var value = field.GetValue(null);
        Assert.Equal(expectedValue, value);
    }

    [Fact]
    public void All_ErrorCodes_Are_Covered()
    {
        var fields = typeof(ErrorCodes).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        // Ensure we have all 15 error codes
        Assert.Equal(15, fields.Length);
    }
}

public class ImageAgingConfigTests
{
    [Fact]
    public void Defaults_MatchSpec()
    {
        var cfg = new ImageAgingConfig();
        Assert.Equal(4, cfg.PreserveRecentTurns);
        Assert.True(cfg.DescribeOnStrip);
    }

    [Fact]
    public void AgentConfig_ExposesImageAgingSubsection()
    {
        var agent = new AgentConfig();
        Assert.NotNull(agent.ImageAging);
        Assert.Equal(4, agent.ImageAging.PreserveRecentTurns);
    }
}

#endregion
