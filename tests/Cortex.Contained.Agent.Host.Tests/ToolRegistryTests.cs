using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class ToolRegistryTests
{
    private static readonly ToolExecutionContext _context = new()
    {
        ConversationId = "conv-test",
        ChannelId = "webchat-default",
    };
    [Fact]
    public void Constructor_NoTools_RegistersZero()
    {
        var registry = new ToolRegistry([], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);

        Assert.Equal(0, registry.Count);
        Assert.Empty(registry.GetDefinitions());
    }

    [Fact]
    public void Constructor_WithTools_RegistersAll()
    {
        var tool1 = new FakeTool("tool_a", "Tool A");
        var tool2 = new FakeTool("tool_b", "Tool B");
        var registry = new ToolRegistry([tool1, tool2], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);

        Assert.Equal(2, registry.Count);
        Assert.Equal(2, registry.GetDefinitions().Count);
    }

    [Fact]
    public void GetTool_ExistingName_ReturnsTool()
    {
        var tool = new FakeTool("my_tool", "My Tool");
        var registry = new ToolRegistry([tool], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);

        var result = registry.GetTool("my_tool");

        Assert.NotNull(result);
        Assert.Equal("my_tool", result.Name);
    }

    [Fact]
    public void GetTool_CaseInsensitive_ReturnsTool()
    {
        var tool = new FakeTool("my_tool", "My Tool");
        var registry = new ToolRegistry([tool], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);

        var result = registry.GetTool("MY_TOOL");

        Assert.NotNull(result);
    }

    [Fact]
    public void GetTool_UnknownName_ReturnsNull()
    {
        var registry = new ToolRegistry([], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);

        var result = registry.GetTool("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task ExecuteAsync_KnownTool_ReturnsResult()
    {
        var tool = new FakeTool("my_tool", "My Tool", new AgentToolResult
        {
            Success = true,
            Content = "done",
        });
        var registry = new ToolRegistry([tool], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);

        var toolCall = new LlmToolCall { Id = "call_1", Name = "my_tool", Arguments = "{}" };
        var result = await registry.ExecuteAsync(toolCall, _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("done", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTool_ReturnsFailure()
    {
        var registry = new ToolRegistry([], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);

        var toolCall = new LlmToolCall { Id = "call_1", Name = "unknown", Arguments = "{}" };
        var result = await registry.ExecuteAsync(toolCall, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unknown tool", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ToolThrows_ReturnsFailure()
    {
        var tool = new ThrowingTool();
        var registry = new ToolRegistry([tool], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);

        var toolCall = new LlmToolCall { Id = "call_1", Name = "throw_tool", Arguments = "{}" };
        var result = await registry.ExecuteAsync(toolCall, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Tool execution failed", result.Error);
    }

    [Fact]
    public void GetDefinitions_ContainsCorrectSchema()
    {
        var tool = new FakeTool("my_tool", "My description");
        var registry = new ToolRegistry([tool], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);

        var defs = registry.GetDefinitions();

        Assert.Single(defs);
        Assert.Equal("my_tool", defs[0].Name);
        Assert.Equal("My description", defs[0].Description);
        Assert.NotNull(defs[0].ParametersSchema);
    }

    [Fact]
    public void GetDefinitionsForConversation_VoiceConversation_IncludesVoiceOnlyTools()
    {
        var voiceTool = new FakeTool("speak_after_delay", "voice-only tool");
        var normalTool = new FakeTool("file_read", "normal tool");
        var registry = new ToolRegistry(
            [voiceTool, normalTool],
            new ActiveChannelStore(),
            NullLogger<ToolRegistry>.Instance,
            gates: [new VoiceOnlyToolGate()]);

        var defs = registry.GetDefinitionsForConversation("discord-voice-tenant-1");

        Assert.Contains(defs, d => d.Name == "speak_after_delay");
        Assert.Contains(defs, d => d.Name == "file_read");
    }

    [Fact]
    public void GetDefinitionsForConversation_NonVoiceConversation_ExcludesVoiceOnlyTools()
    {
        var setTool = new FakeTool("speak_after_delay", "voice-only tool");
        var cancelTool = new FakeTool("cancel_delayed_speech", "voice-only tool");
        var normalTool = new FakeTool("file_read", "normal tool");
        var registry = new ToolRegistry(
            [setTool, cancelTool, normalTool],
            new ActiveChannelStore(),
            NullLogger<ToolRegistry>.Instance,
            gates: [new VoiceOnlyToolGate()]);

        var defs = registry.GetDefinitionsForConversation("webchat-default");

        Assert.DoesNotContain(defs, d => d.Name == "speak_after_delay");
        Assert.DoesNotContain(defs, d => d.Name == "cancel_delayed_speech");
        Assert.Contains(defs, d => d.Name == "file_read");
    }

    [Fact]
    public void GetDefinitionsForConversation_EmptyOrNullConversationId_ExcludesVoiceOnlyTools()
    {
        var voiceTool = new FakeTool("speak_after_delay", "voice-only tool");
        var normalTool = new FakeTool("file_read", "normal tool");
        var registry = new ToolRegistry(
            [voiceTool, normalTool],
            new ActiveChannelStore(),
            NullLogger<ToolRegistry>.Instance,
            gates: [new VoiceOnlyToolGate()]);

        var defs = registry.GetDefinitionsForConversation(string.Empty);

        Assert.DoesNotContain(defs, d => d.Name == "speak_after_delay");

        var nullDefs = registry.GetDefinitionsForConversation(null);
        Assert.DoesNotContain(nullDefs, d => d.Name == "speak_after_delay");
    }

    private sealed class FakeTool : IAgentTool
    {
        private readonly AgentToolResult? _result;

        public FakeTool(string name, string description, AgentToolResult? result = null)
        {
            Name = name;
            Description = description;
            _result = result;
        }

        public string Name { get; }
        public string Description { get; }
        public string ParametersSchema => """{"type":"object","properties":{}}""";

        public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(_result ?? new AgentToolResult
            {
                Success = true,
                Content = "default",
            });
        }
    }

    private sealed class ThrowingTool : IAgentTool
    {
        public string Name => "throw_tool";
        public string Description => "A tool that throws";
        public string ParametersSchema => """{"type":"object","properties":{}}""";

        public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Test exception");
        }
    }
}
