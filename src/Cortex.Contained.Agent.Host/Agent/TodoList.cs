using System.Text;
using System.Text.RegularExpressions;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Status of a todo item, parsed from markdown checkbox markers.
/// </summary>
public enum TodoStatus
{
    /// <summary><c>- [ ]</c> — not started.</summary>
    Pending,

    /// <summary><c>- [-]</c> — currently being worked on.</summary>
    InProgress,

    /// <summary><c>- [x]</c> — done.</summary>
    Completed,

    /// <summary><c>- [~]</c> — skipped or not applicable.</summary>
    Skipped,
}

/// <summary>
/// A single item in a todo list, parsed from a markdown checkbox line.
/// </summary>
public sealed record TodoItem
{
    /// <summary>The full text of the item (excluding the checkbox marker).</summary>
    public required string Description { get; init; }

    /// <summary>Lifecycle status parsed from the checkbox marker.</summary>
    public required TodoStatus Status { get; init; }
}

/// <summary>
/// A named todo list containing items in markdown checkbox format.
/// </summary>
public sealed record TodoList
{
    /// <summary>Name of the list (e.g., "API migration").</summary>
    public required string Name { get; init; }

    /// <summary>Raw markdown text as written by the agent.</summary>
    public required string Markdown { get; init; }

    /// <summary>Parsed items from the markdown.</summary>
    public required IReadOnlyList<TodoItem> Items { get; init; }

    /// <summary>When the list was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Lightweight summary of a todo list for system prompt injection.
/// </summary>
public sealed record TodoSummary
{
    /// <summary>Name of the list.</summary>
    public required string Name { get; init; }

    /// <summary>Total number of items.</summary>
    public required int TotalCount { get; init; }

    /// <summary>Number of completed or skipped items.</summary>
    public required int DoneCount { get; init; }
}

/// <summary>
/// Parses markdown checkbox lists into <see cref="TodoItem"/> collections.
/// </summary>
public static partial class TodoParser
{
    // Matches lines like: - [ ] Description, - [x] Description, - [-] Description, - [~] Description
    // Allows optional leading whitespace and various list markers (-, *, +)
    [GeneratedRegex(@"^\s*[-*+]\s*\[(.)\]\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex CheckboxPattern();

    /// <summary>
    /// Parse markdown text into a list of <see cref="TodoItem"/>s.
    /// Non-checkbox lines are ignored.
    /// </summary>
    public static IReadOnlyList<TodoItem> Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var items = new List<TodoItem>();
        foreach (var match in CheckboxPattern().EnumerateMatches(markdown))
        {
            var fullMatch = markdown.AsSpan(match.Index, match.Length);
            var regexMatch = CheckboxPattern().Match(fullMatch.ToString());
            if (!regexMatch.Success)
            {
                continue;
            }

            var marker = regexMatch.Groups[1].Value;
            var description = regexMatch.Groups[2].Value.Trim();

            if (description.Length == 0)
            {
                continue;
            }

            var status = marker switch
            {
                "x" or "X" => TodoStatus.Completed,
                "-" => TodoStatus.InProgress,
                "~" => TodoStatus.Skipped,
                _ => TodoStatus.Pending,
            };

            items.Add(new TodoItem { Description = description, Status = status });
        }

        return items;
    }

    /// <summary>
    /// Create a <see cref="TodoSummary"/> from a parsed list.
    /// </summary>
    public static TodoSummary Summarize(string name, IReadOnlyList<TodoItem> items)
    {
        var done = items.Count(i => i.Status is TodoStatus.Completed or TodoStatus.Skipped);
        return new TodoSummary
        {
            Name = name,
            TotalCount = items.Count,
            DoneCount = done,
        };
    }

    /// <summary>
    /// Format a <see cref="TodoSummary"/> for system prompt injection.
    /// </summary>
    public static string FormatSummary(TodoSummary summary)
        => $"- \"{summary.Name}\" ({summary.DoneCount}/{summary.TotalCount} done)";
}

/// <summary>
/// Abstraction for todo list storage. Implemented by <see cref="SqliteTodoStore"/>
/// (persistent, for main agent) and <see cref="InMemoryTodoStore"/> (ephemeral, for subagents).
/// </summary>
public interface ITodoStore
{
    /// <summary>Create or replace a named todo list.</summary>
    void Write(string conversationId, string name, string markdown);

    /// <summary>Read a specific list, or null if not found.</summary>
    TodoList? Read(string conversationId, string name);

    /// <summary>Read all lists for a conversation.</summary>
    IReadOnlyList<TodoList> ReadAll(string conversationId);

    /// <summary>Delete a named list. Returns true if it existed.</summary>
    bool Delete(string conversationId, string name);

    /// <summary>Get lightweight summaries of all lists for a conversation (for system prompt).</summary>
    IReadOnlyList<TodoSummary> GetSummaries(string conversationId);
}
