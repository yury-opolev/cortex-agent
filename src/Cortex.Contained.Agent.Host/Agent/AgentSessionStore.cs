using System.Collections.Concurrent;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Manages agent sessions (conversation state, history).
/// Thread-safe in-memory store with idle session reset.
/// Persistent history is now maintained by the Bridge's SQLite MessageStore;
/// this store only holds the in-memory working set for the LLM.
/// </summary>
public sealed partial class AgentSessionStore
{
    private readonly ConcurrentDictionary<string, AgentSession> sessions = new();
    private readonly SessionConfig config;
    private readonly MemorySettingsStore settingsStore;
    private readonly ILogger<AgentSessionStore> logger;

    public AgentSessionStore(
        SessionConfig config,
        MemorySettingsStore settingsStore,
        ILogger<AgentSessionStore> logger)
    {
        this.config = config;
        this.settingsStore = settingsStore;
        this.logger = logger;
    }

    /// <summary>Get or create a session for the given conversation.</summary>
    public AgentSession GetOrCreate(string conversationId)
    {
        return this.sessions.GetOrAdd(conversationId, _ => new AgentSession(conversationId));
    }

    /// <summary>
    /// Get or create a session, checking for idle timeout and resetting if expired.
    /// </summary>
    public AgentSession GetOrCreateWithIdleCheck(string conversationId)
    {
        var session = GetOrCreate(conversationId);
        return CheckIdleReset(session);
    }

    /// <summary>Try to get an existing session.</summary>
    public bool TryGet(string conversationId, out AgentSession? session)
    {
        return this.sessions.TryGetValue(conversationId, out session);
    }

    /// <summary>Remove a session from memory.</summary>
    public bool Remove(string conversationId)
    {
        return this.sessions.TryRemove(conversationId, out _);
    }

    /// <summary>Get all sessions currently in memory.</summary>
    public IReadOnlyCollection<AgentSession> GetAll()
    {
        return this.sessions.Values.ToArray();
    }

    /// <summary>
    /// Reset (clear history) a session for the given channel/conversation.
    /// </summary>
    public void Reset(string conversationId)
    {
        if (this.sessions.TryGetValue(conversationId, out var session))
        {
            session.ClearHistory();
        }

        this.LogSessionReset(conversationId);
    }

    /// <summary>
    /// Reset all sessions (clear history for every active session).
    /// </summary>
    public void ResetAll()
    {
        foreach (var session in this.sessions.Values)
        {
            session.ClearHistory();
        }

        this.LogAllSessionsReset();
    }

    /// <summary>
    /// Seed a session with historical messages from the Bridge.
    /// Replaces any existing history in the session.
    /// </summary>
    public void Seed(string conversationId, IReadOnlyList<LlmMessage> messages, int maxHistory)
    {
        var session = GetOrCreate(conversationId);
        session.ClearHistory();

        foreach (var message in messages)
        {
            session.AddMessage(message);
        }

        session.TrimHistory(maxHistory);
        this.LogSessionSeeded(conversationId, messages.Count);
    }

    private AgentSession CheckIdleReset(AgentSession session)
    {
        // Runtime override takes precedence over config-file value
        var idleResetMinutes = this.settingsStore.IdleResetMinutes ?? this.config.IdleResetMinutes;

        if (idleResetMinutes <= 0)
        {
            return session;
        }

        var idleTime = DateTimeOffset.UtcNow - session.LastMessageAt;
        if (idleTime.TotalMinutes > idleResetMinutes)
        {
            this.LogIdleReset(session.ConversationId, idleTime.TotalMinutes);

            var idleCompactionEnabled = this.settingsStore.IdleCompactionEnabled ?? true;

            // When idle compaction is enabled, flag the session for LLM summarization
            // instead of wiping. AgentRuntime will detect the flag and run compaction
            // before processing the next message, preserving important context.
            // If there aren't enough messages to compact (< 6), fall back to
            // a hard clear so we don't carry stale short conversations forever.
            if (idleCompactionEnabled && session.MessageCount >= 6)
            {
                session.NeedsIdleCompaction = true;
            }
            else
            {
                session.ClearHistory();
            }
        }

        return session;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Idle reset for session {ConversationId} after {IdleMinutes:F1} minutes")]
    private partial void LogIdleReset(string conversationId, double idleMinutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session reset: {ConversationId}")]
    private partial void LogSessionReset(string conversationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "All sessions reset")]
    private partial void LogAllSessionsReset();

    [LoggerMessage(Level = LogLevel.Information, Message = "Session seeded for {ConversationId}: {MessageCount} messages")]
    private partial void LogSessionSeeded(string conversationId, int messageCount);
}
