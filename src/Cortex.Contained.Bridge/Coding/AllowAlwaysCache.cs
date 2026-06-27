using System.Collections.Concurrent;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Per-session cache of tool names that the user has approved with "allow_always".
/// Thread-safe. Keyed by (sessionId, toolName).
/// </summary>
public sealed class AllowAlwaysCache
{
    // sessionId → set of approved tool names
    private readonly ConcurrentDictionary<string, HashSet<string>> entries =
        new(StringComparer.Ordinal);

    /// <summary>Record that <paramref name="toolName"/> is permanently allowed for <paramref name="sessionId"/>.</summary>
    public void Add(string sessionId, string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var set = this.entries.GetOrAdd(sessionId, _ => new HashSet<string>(StringComparer.Ordinal));
        lock (set)
        {
            set.Add(toolName);
        }
    }

    /// <summary>Returns true if <paramref name="toolName"/> has been permanently allowed for <paramref name="sessionId"/>.</summary>
    public bool IsAllowed(string sessionId, string toolName)
    {
        if (!this.entries.TryGetValue(sessionId, out var set))
        {
            return false;
        }

        lock (set)
        {
            return set.Contains(toolName);
        }
    }

    /// <summary>Remove all allow-always entries for the given session (called on session end/crash).</summary>
    public void ClearSession(string sessionId)
    {
        this.entries.TryRemove(sessionId, out _);
    }
}
