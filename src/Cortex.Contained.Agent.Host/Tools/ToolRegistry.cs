using System.Collections.Frozen;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Mcp;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// Registers and discovers agent tools.
/// Provides tool definitions for LLM requests and dispatches tool calls.
/// </summary>
public sealed partial class ToolRegistry
{

    private readonly FrozenDictionary<string, IAgentTool> tools;
    private readonly IAgentTool[] toolList;
    private readonly ILogger<ToolRegistry> logger;
    private readonly ActiveChannelStore activeChannelStore;
    private readonly IConversationToolGate[] gates;
    private readonly McpToolStore? mcpToolStore;

    /// <summary>
    /// Cached tool definitions, invalidated when EITHER the active channel list
    /// (<see cref="ActiveChannelStore.Version"/>) OR the dynamic MCP tool set
    /// (<see cref="McpToolStore.Version"/>) changes, tracked as a composite key.
    /// </summary>
    private IReadOnlyList<LlmToolDefinition>? cachedDefinitions;
    private int cachedChannelVersion = -1;
    private int cachedMcpVersion = -1;

    public ToolRegistry(IEnumerable<IAgentTool> tools, ActiveChannelStore activeChannelStore, ILogger<ToolRegistry> logger, IEnumerable<IConversationToolGate>? gates = null, McpToolStore? mcpToolStore = null)
    {
        this.logger = logger;
        this.activeChannelStore = activeChannelStore;
        this.gates = gates?.ToArray() ?? [];
        this.mcpToolStore = mcpToolStore;

        this.toolList = tools.ToArray();
        this.tools = this.toolList.ToFrozenDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        var toolNames = string.Join(", ", this.tools.Keys);
        this.LogToolsRegistered(this.tools.Count, toolNames);
    }

    /// <summary>
    /// The full current tool set: the static built-in tools unioned with the dynamic
    /// MCP proxy tools currently pushed by the Bridge (if any).
    /// </summary>
    private IEnumerable<IAgentTool> AllTools()
    {
        if (this.mcpToolStore is null)
        {
            return this.toolList;
        }

        return this.toolList.Concat(this.mcpToolStore.Tools);
    }

    /// <summary>
    /// Get all tool definitions for inclusion in LLM requests.
    /// Definitions are cached and only rebuilt when the active channel list
    /// changes (detected via <see cref="ActiveChannelStore.Version"/>),
    /// since dynamic tool schemas (e.g. send_message, schedule_task) embed
    /// the current channel list.
    /// </summary>
    public IReadOnlyList<LlmToolDefinition> GetDefinitions()
    {
        var channelVersion = this.activeChannelStore.Version;
        var mcpVersion = this.mcpToolStore?.Version ?? 0;
        if (this.cachedDefinitions is not null
            && this.cachedChannelVersion == channelVersion
            && this.cachedMcpVersion == mcpVersion)
        {
            return this.cachedDefinitions;
        }

        var definitions = BuildToolDefinitions(this.AllTools());

        this.cachedDefinitions = definitions;
        this.cachedChannelVersion = channelVersion;
        this.cachedMcpVersion = mcpVersion;
        return definitions;
    }

    /// <summary>
    /// Get tool definitions filtered for the given conversation.
    /// Each registered <see cref="IConversationToolGate"/> is asked which tools to hide;
    /// the results are unioned and all hidden tools are excluded from the returned list.
    /// </summary>
    public IReadOnlyList<LlmToolDefinition> GetDefinitionsForConversation(string? conversationId)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gate in this.gates)
        {
            foreach (var name in gate.GetHiddenTools(conversationId))
            {
                hidden.Add(name);
            }
        }

        return hidden.Count == 0
            ? BuildToolDefinitions(this.AllTools())
            : BuildToolDefinitions(this.AllTools().Where(t => !hidden.Contains(t.Name)));
    }

    /// <summary>
    /// Get tool definitions excluding the specified tool names.
    /// Used by subagents to get all tools except the task tool (preventing recursion).
    /// </summary>
    public IReadOnlyList<LlmToolDefinition> GetDefinitionsExcluding(IReadOnlySet<string> excludedNames)
    {
        return BuildToolDefinitions(this.AllTools().Where(t => !excludedNames.Contains(t.Name)));
    }

    private static LlmToolDefinition[] BuildToolDefinitions(IEnumerable<IAgentTool> tools)
    {
        return tools.Select(t => new LlmToolDefinition
        {
            Name = t.Name,
            Description = t.Description,
            ParametersSchema = t.ParametersSchema,
        }).ToArray();
    }

    /// <summary>Get a tool by name, or null if not found.</summary>
    public IAgentTool? GetTool(string name)
    {
        this.tools.TryGetValue(name, out var tool);
        return tool;
    }

    /// <summary>Execute a tool call and return the result.</summary>
    public async Task<AgentToolResult> ExecuteAsync(LlmToolCall toolCall, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!this.tools.TryGetValue(toolCall.Name, out var tool))
        {
            // Fall back to the dynamic MCP proxy tools pushed by the Bridge.
            if (this.mcpToolStore is null || !this.mcpToolStore.TryGet(toolCall.Name, out tool))
            {
                this.LogToolNotFound(toolCall.Name, toolCall.Id);
                return AgentToolResult.Fail($"Unknown tool: {toolCall.Name}");
            }
        }

        this.LogToolExecuting(toolCall.Name, toolCall.Id);

        try
        {
            var result = await tool.ExecuteAsync(toolCall.Arguments, context, cancellationToken).ConfigureAwait(false);

            // Truncate large tool output to prevent context window overflow.
            // Full output is saved to disk; LLM gets a preview + hint to use file_read.
            if (result.Success && result.Content.Length > 0)
            {
                var truncated = ToolOutputTruncator.Truncate(result.Content);
                if (!ReferenceEquals(truncated, result.Content))
                {
                    result = result with { Content = truncated };
                }
            }

            this.LogToolComplete(toolCall.Name, toolCall.Id, result.Success);
            return result;
        }
        catch (OperationCanceledException)
        {
            this.LogToolCancelled(toolCall.Name, toolCall.Id);
            return AgentToolResult.Fail("Tool execution was cancelled.");
        }
        catch (SandboxEscapeException ex)
        {
            // A blocked sandbox escape is a security-relevant event — log it at warning
            // level for auditing. Tool-visible behavior is unchanged: SandboxEscapeException
            // is an ArgumentException, so the result text matches the general handler below.
            this.LogSandboxEscapeBlocked(ex.Message, toolCall.Name);
            return AgentToolResult.Fail($"Tool execution failed: {ex.Message}");
        }
#pragma warning disable CA1031 // Do not catch general exception types -- tool errors must be reported, not crash the agent
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogToolError(toolCall.Name, toolCall.Id, ex.Message);
            return AgentToolResult.Fail($"Tool execution failed: {ex.Message}");
        }
    }

    /// <summary>Number of registered tools.</summary>
    public int Count => this.tools.Count;

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered {Count} tools: [{ToolNames}]")]
    private partial void LogToolsRegistered(int count, string toolNames);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Executing tool {ToolName} (call {ToolCallId})")]
    private partial void LogToolExecuting(string toolName, string toolCallId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tool {ToolName} (call {ToolCallId}) completed: success={Success}")]
    private partial void LogToolComplete(string toolName, string toolCallId, bool success);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool not found: {ToolName} (call {ToolCallId})")]
    private partial void LogToolNotFound(string toolName, string toolCallId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {ToolName} (call {ToolCallId}) was cancelled")]
    private partial void LogToolCancelled(string toolName, string toolCallId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tool {ToolName} (call {ToolCallId}) failed: {ErrorMessage}")]
    private partial void LogToolError(string toolName, string toolCallId, string errorMessage);

    [LoggerMessage(EventId = 1801, Level = LogLevel.Warning, Message = "Sandbox escape attempt blocked: {Reason} tool={ToolName}")]
    private partial void LogSandboxEscapeBlocked(string reason, string toolName);
}
