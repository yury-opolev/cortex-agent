namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// Result of executing an agent tool.
/// </summary>
public sealed record AgentToolResult
{
    /// <summary>Whether the tool executed successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>The result content (text, JSON, etc.).</summary>
    public required string Content { get; init; }

    /// <summary>Optional error message if <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>A successful result carrying <paramref name="content"/>.</summary>
    public static AgentToolResult Ok(string content) => new() { Success = true, Content = content };

    /// <summary>A failed result carrying an <paramref name="error"/> message and empty content.</summary>
    public static AgentToolResult Fail(string error) => new() { Success = false, Content = string.Empty, Error = error };
}

/// <summary>
/// Provides conversation context to tools during execution.
/// </summary>
public sealed record ToolExecutionContext
{
    /// <summary>Current conversation ID.</summary>
    public required string ConversationId { get; init; }

    /// <summary>
    /// The channel ID that originated the current conversation
    /// (e.g. <c>webchat-default</c>, <c>discord-dm</c>).
    /// Tools can use this to target messages to specific channels.
    /// </summary>
    public required string ChannelId { get; init; }

    /// <summary>
    /// The originating turn's correlation id, propagated to tool-initiated sends
    /// (e.g. proactive messages) so they can be traced end-to-end alongside the
    /// turn that triggered them. Null when no turn correlation is in scope.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Add-only collector for proactive messages sent during this turn via <c>send_message</c>
    /// or session transfer. Messages cannot be injected into session history mid-tool-loop
    /// (would break tool_call → tool ordering); the runtime drains
    /// <see cref="IProactiveMessageCollector.Collected"/> after the turn completes via
    /// <c>AppendOrGlueAssistantMessage</c>.
    /// </summary>
    public IProactiveMessageCollector ProactiveMessages { get; init; } = new ProactiveMessageCollector();
}

/// <summary>
/// Records a proactive message sent during a tool loop turn, for deferred
/// injection into the target channel's session history.
/// </summary>
public sealed record ProactiveMessageRecord
{
    /// <summary>Target channel ID (e.g. <c>discord-dm</c>).</summary>
    public required string ChannelId { get; init; }

    /// <summary>The message text that was sent.</summary>
    public required string Text { get; init; }
}

/// <summary>
/// Interface for tools the agent can invoke during conversation.
/// Each tool has a name, description, JSON Schema for parameters,
/// and an execute method.
/// </summary>
public interface IAgentTool
{
    /// <summary>Unique tool name (e.g., "file_read").</summary>
    string Name { get; }

    /// <summary>Human-readable description for the LLM.</summary>
    string Description { get; }

    /// <summary>JSON Schema string describing the tool's parameters.</summary>
    string ParametersSchema { get; }

    /// <summary>Execute the tool with JSON-encoded arguments.</summary>
    Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken);
}
