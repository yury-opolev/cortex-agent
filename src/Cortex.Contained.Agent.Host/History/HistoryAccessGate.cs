namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Async reader/writer gate that makes deletion and recall linearizable (§10). Recall tools hold
/// a read lease until their response is assembled; purges and the index cut-over take the write
/// lease, which waits for in-flight reads and blocks new reads while a writer waits or runs
/// (writer preference).
/// </summary>
public sealed class HistoryAccessGate
{
    private readonly Lock sync = new();
    private readonly LinkedList<TaskCompletionSource> waitingReaders = new();
    private readonly LinkedList<TaskCompletionSource> waitingWriters = new();
    private int activeReaders;
    private bool writerActive;

    /// <summary>Number of read leases currently held (diagnostics and tests).</summary>
    public int ActiveReaders
    {
        get
        {
            lock (this.sync)
            {
                return this.activeReaders;
            }
        }
    }

    /// <summary>Acquires a read lease; dispose the result to release it.</summary>
    public async Task<IAsyncDisposable> AcquireReadAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource waiter;
        LinkedListNode<TaskCompletionSource> node;
        lock (this.sync)
        {
            if (!this.writerActive && this.waitingWriters.Count == 0)
            {
                this.activeReaders++;
                return new Lease(this, isWriter: false);
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = this.waitingReaders.AddLast(waiter);
        }

        await this.WaitAsync(waiter, node, this.waitingReaders, cancellationToken).ConfigureAwait(false);
        return new Lease(this, isWriter: false);
    }

    /// <summary>Acquires the exclusive write lease; dispose the result to release it.</summary>
    public async Task<IAsyncDisposable> AcquireWriteAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource waiter;
        LinkedListNode<TaskCompletionSource> node;
        lock (this.sync)
        {
            if (!this.writerActive && this.activeReaders == 0)
            {
                this.writerActive = true;
                return new Lease(this, isWriter: true);
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = this.waitingWriters.AddLast(waiter);
        }

        await this.WaitAsync(waiter, node, this.waitingWriters, cancellationToken).ConfigureAwait(false);
        return new Lease(this, isWriter: true);
    }

    private async Task WaitAsync(
        TaskCompletionSource waiter,
        LinkedListNode<TaskCompletionSource> node,
        LinkedList<TaskCompletionSource> queue,
        CancellationToken cancellationToken)
    {
        await using var registration = cancellationToken.Register(() =>
        {
            lock (this.sync)
            {
                if (node.List is null)
                {
                    return;
                }

                queue.Remove(node);
                this.Promote();
            }

            waiter.TrySetCanceled(cancellationToken);
        }).ConfigureAwait(false);

        await waiter.Task.ConfigureAwait(false);
    }

    private void Release(bool isWriter)
    {
        lock (this.sync)
        {
            if (isWriter)
            {
                this.writerActive = false;
            }
            else
            {
                this.activeReaders--;
            }

            this.Promote();
        }
    }

    // Must run under the lock: hands the gate to the next writer, else to every waiting reader.
    private void Promote()
    {
        if (this.writerActive)
        {
            return;
        }

        if (this.waitingWriters.First is { } writerNode)
        {
            if (this.activeReaders == 0)
            {
                this.waitingWriters.RemoveFirst();
                this.writerActive = true;
                writerNode.Value.TrySetResult();
            }

            return;
        }

        while (this.waitingReaders.First is { } readerNode)
        {
            this.waitingReaders.RemoveFirst();
            this.activeReaders++;
            readerNode.Value.TrySetResult();
        }
    }

    private sealed class Lease(HistoryAccessGate gate, bool isWriter) : IAsyncDisposable
    {
        private int released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.released, 1) == 0)
            {
                gate.Release(isWriter);
            }

            return ValueTask.CompletedTask;
        }
    }
}
