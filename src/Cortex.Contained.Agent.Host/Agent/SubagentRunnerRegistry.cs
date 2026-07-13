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

    private readonly ConcurrentDictionary<string, RunnerEntry> runners = new(StringComparer.Ordinal);
    private readonly ILogger<SubagentRunnerRegistry> logger;

    private sealed record RunnerEntry(SubagentRunner Runner, CancellationTokenSource Cts);

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
    /// Atomically admit a runner under the concurrency cap. Returns true and hands back the
    /// registry-owned <paramref name="cancellationToken"/> (the ONLY token the caller may use to
    /// run/cancel this task) when a slot was available; false (with <see cref="CancellationToken.None"/>)
    /// when the cap is reached or the task id is already registered. The count check, the
    /// <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>, and the CTS creation all happen under
    /// <see cref="capLock"/>, so concurrent admissions can never exceed the cap and the indexer is
    /// never used to overwrite an existing runner.
    /// </summary>
    public bool TryRegister(string taskId, SubagentRunner runner, out CancellationToken cancellationToken)
    {
        lock (this.capLock)
        {
            if (this.runners.Count >= this.maxConcurrent)
            {
                this.LogSlotUnavailable(taskId, this.runners.Count, this.maxConcurrent);
                cancellationToken = CancellationToken.None;
                return false;
            }

            // Create the CTS only for a successful add so no source leaks on a rejected admission.
            var cts = new CancellationTokenSource();
            if (!this.runners.TryAdd(taskId, new RunnerEntry(runner, cts)))
            {
                // Duplicate task id — never overwrite the existing runner via the indexer.
                cts.Dispose();
                cancellationToken = CancellationToken.None;
                return false;
            }

            cancellationToken = cts.Token;
            this.LogRunnerRegistered(taskId, this.runners.Count);
            return true;
        }
    }

    /// <summary>
    /// Remove a runner when it completes or fails. Implicitly frees the concurrency slot and
    /// invokes the slots-opened callback (outside the lock) so a waiting dispatcher wakes.
    /// </summary>
    public bool Remove(string taskId)
    {
        bool removed;
        RunnerEntry? entry;
        Action? callback;
        lock (this.capLock)
        {
            removed = this.runners.TryRemove(taskId, out entry);
            callback = removed ? this.slotsOpenedCallback : null;
        }

        if (removed)
        {
            entry!.Cts.Dispose();
            this.LogRunnerRemoved(taskId, this.runners.Count);
        }

        callback?.Invoke();
        return removed;
    }

    /// <summary>Get a runner by task ID, or null if not active.</summary>
    public SubagentRunner? TryGet(string taskId)
        => this.runners.TryGetValue(taskId, out var entry) ? entry.Runner : null;

    /// <summary>The cancellation token for a registered task, or <see cref="CancellationToken.None"/>.</summary>
    public CancellationToken GetCancellationToken(string taskId)
    {
        if (!this.runners.TryGetValue(taskId, out var entry))
        {
            return CancellationToken.None;
        }

        try
        {
            return entry.Cts.Token;
        }
        catch (ObjectDisposedException)
        {
            return CancellationToken.None;
        }
    }

    /// <summary>
    /// Cancel a running task's loop. Returns true if a runner was registered under
    /// <paramref name="taskId"/> (its token is cancelled); false if not found.
    /// </summary>
    public bool TryCancel(string taskId)
    {
        if (!this.runners.TryGetValue(taskId, out var entry))
        {
            return false;
        }

        try
        {
            entry.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        this.LogRunnerCancelled(taskId);
        return true;
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-registry] Runner cancelled: {TaskId}")]
    private partial void LogRunnerCancelled(string taskId);
}
