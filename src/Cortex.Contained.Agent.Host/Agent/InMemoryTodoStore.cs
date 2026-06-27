using System.Collections.Concurrent;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// In-memory todo store for subagents. Each subagent gets a single list
/// that lives only as long as the subagent runner. Thread-safe via
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed partial class InMemoryTodoStore : ITodoStore
{
    /// <summary>Key: (conversationId, name). Value: markdown text.</summary>
    private readonly ConcurrentDictionary<(string ConversationId, string Name), string> store = new();

    private readonly ILogger<InMemoryTodoStore> logger;

    /// <summary>Maximum items per list (same limit as SQLite store).</summary>
    internal const int MaxItemsPerList = 20;

    public InMemoryTodoStore(ILogger<InMemoryTodoStore> logger)
    {
        this.logger = logger;
    }

    public void Write(string conversationId, string name, string markdown)
    {
        var items = TodoParser.Parse(markdown);
        if (items.Count > MaxItemsPerList)
        {
            this.LogTodosTooManyItems(name, conversationId, items.Count, MaxItemsPerList);
            return;
        }

        // Subagents get max 1 list — enforce by removing any existing list with a different name
        foreach (var key in this.store.Keys)
        {
            if (string.Equals(key.ConversationId, conversationId, StringComparison.Ordinal)
                && !string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                this.store.TryRemove(key, out _);
            }
        }

        this.store[(conversationId, name)] = markdown;
        this.LogTodosWritten(name, conversationId, items.Count);
    }

    public TodoList? Read(string conversationId, string name)
    {
        if (!this.store.TryGetValue((conversationId, name), out var markdown))
        {
            return null;
        }

        return new TodoList
        {
            Name = name,
            Markdown = markdown,
            Items = TodoParser.Parse(markdown),
        };
    }

    public IReadOnlyList<TodoList> ReadAll(string conversationId)
    {
        var lists = new List<TodoList>();
        foreach (var (key, markdown) in this.store)
        {
            if (string.Equals(key.ConversationId, conversationId, StringComparison.Ordinal))
            {
                lists.Add(new TodoList
                {
                    Name = key.Name,
                    Markdown = markdown,
                    Items = TodoParser.Parse(markdown),
                });
            }
        }

        return lists;
    }

    public bool Delete(string conversationId, string name)
    {
        var removed = this.store.TryRemove((conversationId, name), out _);
        if (removed)
        {
            this.LogTodosDeleted(name, conversationId);
        }

        return removed;
    }

    public IReadOnlyList<TodoSummary> GetSummaries(string conversationId)
    {
        return ReadAll(conversationId)
            .Select(l => TodoParser.Summarize(l.Name, l.Items))
            .ToList();
    }

    /// <summary>Remove all lists for a conversation (called when subagent completes).</summary>
    public void Clear(string conversationId)
    {
        foreach (var key in this.store.Keys)
        {
            if (string.Equals(key.ConversationId, conversationId, StringComparison.Ordinal))
            {
                this.store.TryRemove(key, out _);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[todos] Written (in-memory): \"{Name}\" for {ConversationId} ({ItemCount} items)")]
    private partial void LogTodosWritten(string name, string conversationId, int itemCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "[todos] Deleted (in-memory): \"{Name}\" for {ConversationId}")]
    private partial void LogTodosDeleted(string name, string conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[todos] Too many items in \"{Name}\" for {ConversationId}: {Count} exceeds max {Max}")]
    private partial void LogTodosTooManyItems(string name, string conversationId, int count, int max);
}
