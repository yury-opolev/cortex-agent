using System.Collections.Concurrent;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Tracks active <see cref="SubagentRunner"/> instances by task ID and limits
/// concurrent subagent execution. The <see cref="this.runners"/> dictionary is both
/// the runner registry and the concurrency limiter — slot availability is derived
/// from the count. Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed partial class SubagentRunnerRegistry
{
    private const int MinConcurrent = 1;
    private const int MaxConcurrentLimit = 20;

    private readonly ConcurrentDictionary<string, SubagentRunner> runners = new(StringComparer.Ordinal);
    private readonly ILogger<SubagentRunnerRegistry> logger;

    private readonly Lock capLock = new();

    private volatile int maxConcurrent;
    private Action? slotsOpenedCallback;

    public SubagentRunnerRegistry(int maxConcurrent, ILogger<SubagentRunnerRegistry> logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);
        this.maxConcurrent = maxConcurrent;
        this.logger = logger;
    }

    /// <summary>Number of currently active runners.</summary>
    public int ActiveCount => this.runners.Count;

    /// <summary>The current live concurrency cap.</summary>
    public int MaxConcurrent => this.maxConcurrent;

    /// <summary>
    /// Try to register a runner if a concurrency slot is available.
    /// Returns true if registered, false if all slots are in use.
    /// </summary>
    public bool TryRegister(string taskId, SubagentRunner runner)
    {
        // Check count first — not perfectly atomic with the add, but
        // ConcurrentDictionary ensures no corruption. Worst case: one
        // extra runner sneaks in under high concurrency, which is acceptable.
        if (this.runners.Count >= this.maxConcurrent)
        {
            this.LogSlotUnavailable(taskId, this.runners.Count, this.maxConcurrent);
            return false;
        }

        this.runners[taskId] = runner;
        this.LogRunnerRegistered(taskId, this.runners.Count);
        return true;
    }

    /// <summary>
    /// Remove a runner when it completes or fails. Implicitly frees the concurrency slot.
    /// </summary>
    public bool Remove(string taskId)
    {
        var removed = this.runners.TryRemove(taskId, out _);
        if (removed)
        {
            this.LogRunnerRemoved(taskId, this.runners.Count);
        }

        return removed;
    }

    /// <summary>Get a runner by task ID, or null if not active.</summary>
    public SubagentRunner? TryGet(string taskId)
        => this.runners.GetValueOrDefault(taskId);

    /// <summary>Check if a concurrency slot is available without registering.</summary>
    public bool HasAvailableSlot => this.runners.Count < this.maxConcurrent;

    /// <summary>Get all active task IDs.</summary>
    public IReadOnlyList<string> GetActiveTaskIds()
        => [.. this.runners.Keys];

    /// <summary>
    /// Register a callback invoked when concurrency slots open (cap raised). The
    /// consumer (SubAgentStartTool) uses it to start queued subagents immediately.
    /// </summary>
    public void SetSlotsOpenedCallback(Action callback)
    {
        lock (this.capLock)
        {
            this.slotsOpenedCallback = callback;
        }
    }

    /// <summary>
    /// Set the live concurrency cap (clamped to [1,20]). Raising it invokes the
    /// slots-opened callback so waiting subagents start without a restart. Lowering
    /// it only caps NEW registrations — running subagents are never force-stopped.
    /// </summary>
    public void SetMaxConcurrent(int value)
    {
        var clamped = Math.Clamp(value, MinConcurrent, MaxConcurrentLimit);
        int previous;
        Action? callback;
        lock (this.capLock)
        {
            previous = this.maxConcurrent;
            this.maxConcurrent = clamped;
            callback = this.slotsOpenedCallback;
        }

        if (clamped != previous)
        {
            this.LogMaxConcurrentChanged(previous, clamped);
        }

        if (clamped > previous)
        {
            callback?.Invoke();
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "[subagent-registry] No slot for {TaskId}: {ActiveCount}/{MaxConcurrent} active")]
    private partial void LogSlotUnavailable(string taskId, int activeCount, int maxConcurrent);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[subagent-registry] Runner registered: {TaskId} ({ActiveCount} active)")]
    private partial void LogRunnerRegistered(string taskId, int activeCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[subagent-registry] Runner removed: {TaskId} ({ActiveCount} active)")]
    private partial void LogRunnerRemoved(string taskId, int activeCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-registry] Max concurrent changed: {Previous} -> {Current}")]
    private partial void LogMaxConcurrentChanged(int previous, int current);
}
