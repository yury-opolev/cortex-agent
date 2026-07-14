namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Tracks in-flight MCP tool invocations by their stable invocation id, holding one linked
/// <see cref="CancellationTokenSource"/> per active id. The hub client registers an invocation
/// before dispatching it to the MCP host and completes it in a <c>finally</c>;
/// <see cref="Cancel"/> (driven by the agent's <c>CancelMcpTool</c>) cancels exactly the
/// matching invocation, and <see cref="CancelAll"/> (connection close/reconnect/replacement/
/// dispose) cancels every outstanding one. Purely a cancellation registry — it never stores or
/// replays the invocations themselves.
/// </summary>
public sealed class McpInvocationTracker : IDisposable
{
    private readonly Lock stateLock = new();
    private readonly Dictionary<string, CancellationTokenSource> active = new(StringComparer.Ordinal);
    private bool disposed;

    /// <summary>The number of currently-registered (in-flight) invocations.</summary>
    public int ActiveCount
    {
        get
        {
            lock (this.stateLock)
            {
                return this.active.Count;
            }
        }
    }

    /// <summary>
    /// Registers <paramref name="invocationId"/> and yields a token linked to
    /// <paramref name="externalToken"/>. Returns false (with a cancelled token) when the id is
    /// already active — the same invocation must never run twice concurrently — or when the
    /// tracker is disposed.
    /// </summary>
    public bool TryRegister(string invocationId, CancellationToken externalToken, out CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);

        lock (this.stateLock)
        {
            if (this.disposed || this.active.ContainsKey(invocationId))
            {
                token = new CancellationToken(canceled: true);
                return false;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            this.active[invocationId] = cts;
            token = cts.Token;
            return true;
        }
    }

    /// <summary>
    /// Cancels the invocation registered under <paramref name="invocationId"/>, if any.
    /// Returns whether a matching in-flight invocation was found. The entry stays registered
    /// until its owner calls <see cref="Complete"/> (the id remains reserved while the
    /// cancelled call unwinds).
    /// </summary>
    public bool Cancel(string invocationId, string? reason)
    {
        _ = reason; // Carried for symmetry with the wire contract; logging happens at the caller.

        CancellationTokenSource? cts;
        lock (this.stateLock)
        {
            this.active.TryGetValue(invocationId, out cts);
        }

        if (cts is null)
        {
            return false;
        }

        TryCancel(cts);
        return true;
    }

    /// <summary>
    /// Cancels every outstanding invocation (connection close, reconnect, replacement, dispose).
    /// Returns the number of invocations cancelled.
    /// </summary>
    public int CancelAll(string? reason)
    {
        _ = reason;

        List<CancellationTokenSource> toCancel;
        lock (this.stateLock)
        {
            toCancel = [.. this.active.Values];
        }

        foreach (var cts in toCancel)
        {
            TryCancel(cts);
        }

        return toCancel.Count;
    }

    /// <summary>
    /// Removes a finished invocation and releases its token source. Must be called in the
    /// dispatcher's <c>finally</c> so an id can never leak.
    /// </summary>
    public void Complete(string invocationId)
    {
        CancellationTokenSource? cts;
        lock (this.stateLock)
        {
            this.active.Remove(invocationId, out cts);
        }

        cts?.Dispose();
    }

    public void Dispose()
    {
        List<CancellationTokenSource> toRelease;
        lock (this.stateLock)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            toRelease = [.. this.active.Values];
            this.active.Clear();
        }

        foreach (var cts in toRelease)
        {
            TryCancel(cts);
            cts.Dispose();
        }
    }

    /// <summary>Cancels outside the state lock; tolerates a concurrent <see cref="Complete"/> disposal.</summary>
    private static void TryCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The invocation completed (and was disposed) between snapshot and cancel — benign.
        }
    }
}
