using System.Collections.Concurrent;

namespace Cortex.Contained.Bridge.Mcp.Actions;

/// <summary>
/// Tracks the in-flight dispatch of approved MCP actions so a cancel request arriving WHILE an
/// action is dispatching can cancel the active remote invocation. The dispatcher registers each
/// action's <see cref="CancellationTokenSource"/> for the duration of its dispatch; the approval
/// service requests cancellation by action id. Cancelling an active dispatch never produces a
/// <c>cancelled</c> action — once dispatch has begun the outcome is decided by the invocation
/// result (an in-flight cancellation maps to <c>outcome_unknown</c>).
/// </summary>
public sealed class McpActionDispatchRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> active = new(StringComparer.Ordinal);

    /// <summary>Registers the active dispatch of <paramref name="actionId"/>. The registry does not take ownership of <paramref name="cancellationSource"/>.</summary>
    public void Register(string actionId, CancellationTokenSource cancellationSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentNullException.ThrowIfNull(cancellationSource);
        this.active[actionId] = cancellationSource;
    }

    /// <summary>Removes the active-dispatch registration of <paramref name="actionId"/>.</summary>
    public void Unregister(string actionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        this.active.TryRemove(actionId, out _);
    }

    /// <summary>
    /// Requests cancellation of the active dispatch of <paramref name="actionId"/>. Returns true
    /// when an active dispatch was found and signalled; false when none is in flight (the
    /// dispatch already completed or never started here).
    /// </summary>
    public bool RequestCancel(string actionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        if (this.active.TryGetValue(actionId, out var cancellationSource))
        {
            try
            {
                cancellationSource.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                // The dispatch finished (and disposed its source) between lookup and cancel —
                // equivalent to no active dispatch.
                return false;
            }
        }

        return false;
    }
}
