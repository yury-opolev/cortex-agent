namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Resolves the appropriate <see cref="ITodoStore"/> based on the conversation context.
/// Main agent conversations use the persistent <see cref="SqliteTodoStore"/>;
/// subagent conversations use the ephemeral <see cref="InMemoryTodoStore"/>.
/// </summary>
public sealed class TodoStoreResolver
{
    private readonly SqliteTodoStore sqliteStore;
    private readonly InMemoryTodoStore inMemoryStore;

    /// <summary>Prefix used by subagent conversation IDs.</summary>
    private const string SubagentPrefix = "subagent-";

    public TodoStoreResolver(SqliteTodoStore sqliteStore, InMemoryTodoStore inMemoryStore)
    {
        this.sqliteStore = sqliteStore;
        this.inMemoryStore = inMemoryStore;
    }

    /// <summary>
    /// Get the appropriate store for the given conversation.
    /// Subagent conversations get the in-memory store; everything else gets SQLite.
    /// </summary>
    public ITodoStore Resolve(string conversationId)
    {
        return conversationId.StartsWith(SubagentPrefix, StringComparison.OrdinalIgnoreCase)
            ? this.inMemoryStore
            : this.sqliteStore;
    }

    /// <summary>Get the SQLite store directly (for main agent system prompt injection).</summary>
    public SqliteTodoStore PersistentStore => this.sqliteStore;

    /// <summary>Get the in-memory store directly (for subagent cleanup).</summary>
    public InMemoryTodoStore EphemeralStore => this.inMemoryStore;
}
