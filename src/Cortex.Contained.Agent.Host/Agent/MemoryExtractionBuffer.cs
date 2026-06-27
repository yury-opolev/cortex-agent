namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Thread-safe buffer of <see cref="ExtractionEntry"/> objects awaiting memory extraction.
/// Appended alongside conversation history but independent of compaction — drained only
/// by the pre-compaction flush.
/// </summary>
internal sealed class MemoryExtractionBuffer
{
    /// <summary>Maximum number of entries retained (safety valve).</summary>
    public const int MaxSize = 100;

    private readonly List<ExtractionEntry> entries = [];
    private readonly Lock syncLock = new();

    /// <summary>Current number of buffered entries.</summary>
    public int Count
    {
        get
        {
            lock (this.syncLock)
            {
                return this.entries.Count;
            }
        }
    }

    /// <summary>
    /// Appends <paramref name="entry"/> to the buffer. Silently discarded when the
    /// buffer already holds <see cref="MaxSize"/> entries.
    /// </summary>
    public void Append(ExtractionEntry entry)
    {
        lock (this.syncLock)
        {
            if (this.entries.Count < MaxSize)
            {
                this.entries.Add(entry);
            }
        }
    }

    /// <summary>
    /// Returns all buffered entries and clears the buffer atomically.
    /// Returns an empty list when the buffer is empty.
    /// </summary>
    public IReadOnlyList<ExtractionEntry> Drain()
    {
        lock (this.syncLock)
        {
            if (this.entries.Count == 0)
            {
                return [];
            }

            var snapshot = this.entries.ToList();
            this.entries.Clear();
            return snapshot;
        }
    }

    /// <summary>
    /// Returns all buffered entries without removing them. Used for session snapshot
    /// serialisation.
    /// </summary>
    public IReadOnlyList<ExtractionEntry> PeekAll()
    {
        lock (this.syncLock)
        {
            return this.entries.Count == 0 ? [] : this.entries.ToList();
        }
    }
}
