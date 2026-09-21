namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Routes agent-directed messages through the only live inlet for subagent conversations before
/// falling back to the main runtime queue, preventing subagent-owned work from becoming orphaned
/// in an ordinary conversation lane when the addressed runner is still active.
/// </summary>
public sealed partial class SubagentMessageRouter
{
    private readonly AgentMessageChannel messageChannel;
    private readonly SubagentRunnerRegistry registry;
    private readonly ILogger<SubagentMessageRouter> logger;

    /// <summary>Create a router over the live subagent registry and the main runtime queue.</summary>
    public SubagentMessageRouter(
        AgentMessageChannel messageChannel,
        SubagentRunnerRegistry registry,
        ILogger<SubagentMessageRouter> logger)
    {
        this.messageChannel = messageChannel;
        this.registry = registry;
        this.logger = logger;
    }

    /// <summary>Route without waiting; live subagent injection succeeds synchronously.</summary>
    public bool TryEnqueue(AgentMessage message)
    {
        if (this.TryInjectLiveSubagent(message))
        {
            return true;
        }

        return this.messageChannel.TryEnqueue(message);
    }

    /// <summary>Route with fallback backpressure when the message belongs to the main runtime queue.</summary>
    public async ValueTask EnqueueAsync(AgentMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (this.TryInjectLiveSubagent(message))
        {
            return;
        }

        await this.messageChannel.EnqueueAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private bool TryInjectLiveSubagent(AgentMessage message)
    {
        if (!SubagentConversationIds.TryGetTaskId(message.ConversationId, out var taskId))
        {
            return false;
        }

        var runner = this.registry.TryGet(taskId);
        if (runner is null)
        {
            this.LogLiveRunnerNotFound(taskId, message.ConversationId);
            return false;
        }

        // TOCTOU: the runner may have finished between the registry lookup and here. InjectMessage
        // enqueues onto a pending session that is only drained per round, so injecting into a
        // finished runner would silently swallow the message — and returning true would suppress
        // the channel fallback. A dropped coda permission request is exactly the failure this
        // router exists to prevent: it would sit until the Bridge's expiry fallback REFUSES it.
        if (!runner.InjectMessage(message.Text))
        {
            this.LogRunnerNoLongerAccepting(taskId, message.ConversationId);
            return false;
        }

        this.LogInjected(taskId, message.ConversationId);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "[subagent-router] No live runner for {TaskId}; falling back to message channel for {ConversationId}")]
    private partial void LogLiveRunnerNotFound(string taskId, string conversationId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[subagent-router] Injected message into live runner {TaskId} for {ConversationId}")]
    private partial void LogInjected(string taskId, string conversationId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[subagent-router] Runner {TaskId} stopped accepting messages; falling back to message channel for {ConversationId}")]
    private partial void LogRunnerNoLongerAccepting(string taskId, string conversationId);
}
