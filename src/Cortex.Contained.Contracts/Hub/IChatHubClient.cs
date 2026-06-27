namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Streaming, status, error, and proactive-message callbacks the agent pushes to
/// the Bridge. Part of the composed <see cref="IAgentHubClient"/> surface — these
/// callbacks share the single SignalR hub connection and route by method name.
/// </summary>
public interface IChatHubClient
{
    /// <summary>A streaming response chunk from the agent.</summary>
    Task OnResponseChunk(ResponseChunkMessage chunk);

    /// <summary>The agent has finished generating a response.</summary>
    Task OnResponseComplete(ResponseCompleteMessage response);

    /// <summary>The agent is executing a tool.</summary>
    Task OnToolExecution(ToolExecutionMessage toolExec);

    /// <summary>The agent's status has changed.</summary>
    Task OnStatusChanged(AgentStatusInfo status);

    /// <summary>An error occurred during processing.</summary>
    Task OnError(AgentErrorMessage agentError);

    /// <summary>A conversation's metadata was updated.</summary>
    Task OnConversationUpdated(ConversationInfo conversation);

    /// <summary>
    /// The agent is sending a proactive message to a channel without a preceding
    /// user message. The Bridge routes the message to the target channel and
    /// returns the delivery result via SignalR Client Results.
    /// </summary>
    Task<ProactiveMessageResult> OnProactiveMessage(ProactiveMessage message);

    /// <summary>
    /// A scheduled task has finished execution. The Bridge persists the task
    /// instruction and agent response to the <c>scheduled-tasks</c> channel
    /// so they appear in the history page.
    /// </summary>
    Task OnScheduledTaskComplete(ScheduledTaskCompleteMessage message);
}
