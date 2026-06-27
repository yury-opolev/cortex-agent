namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Owns the mutable state for the in-flight LLM generation turn: the
/// <see cref="CancellationTokenSource"/> for the current turn, the
/// <see cref="IsGenerating"/> flag, the barge-in <see cref="InterruptedPlayedText"/>
/// marker, and the <see cref="LastAssistantRecordId"/> used for post-generation
/// durable-record updates.
///
/// All four fields are mutated atomically under a single <see cref="Lock"/> so that
/// <see cref="Begin"/> is an atomic reset of all generation state.
/// </summary>
internal sealed class GenerationState : IDisposable
{
    private readonly Lock syncLock = new();
    private CancellationTokenSource? currentGenerationCts;
    private bool isGenerating;
    private string? interruptedPlayedText;
    private long lastAssistantRecordId;

    /// <summary>
    /// <see langword="true"/> while the agent is actively producing a response for this
    /// session.
    /// </summary>
    public bool IsGenerating
    {
        get
        {
            lock (this.syncLock)
            {
                return this.isGenerating;
            }
        }
    }

    /// <summary>
    /// Set when the in-flight or just-finished assistant turn was barge-in interrupted;
    /// contains the played-only text (already ends with "…"). Consulted by the persist
    /// site so durable history matches what was spoken. <see langword="null"/> when no
    /// barge-in occurred.
    /// </summary>
    public string? InterruptedPlayedText
    {
        get
        {
            lock (this.syncLock)
            {
                return this.interruptedPlayedText;
            }
        }
    }

    /// <summary>
    /// Durable record id of the last persisted assistant turn (from
    /// <c>SaveMessageAsync</c>) so an interrupt arriving after persistence can update
    /// the existing row. 0 = none yet for the current turn.
    /// </summary>
    public long LastAssistantRecordId
    {
        get
        {
            lock (this.syncLock)
            {
                return this.lastAssistantRecordId;
            }
        }
    }

    /// <summary>
    /// Atomically begins a new generation turn: disposes any previous
    /// <see cref="CancellationTokenSource"/>, creates a new one linked to
    /// <paramref name="externalToken"/>, resets <see cref="InterruptedPlayedText"/> to
    /// <see langword="null"/>, resets <see cref="LastAssistantRecordId"/> to 0, and sets
    /// <see cref="IsGenerating"/> to <see langword="true"/>.
    /// </summary>
    /// <returns>The cancellation token for this generation turn.</returns>
    public CancellationToken Begin(CancellationToken externalToken)
    {
        lock (this.syncLock)
        {
            this.currentGenerationCts?.Dispose();
            this.currentGenerationCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            this.interruptedPlayedText = null;
            this.lastAssistantRecordId = 0;
            this.isGenerating = true;
            return this.currentGenerationCts.Token;
        }
    }

    /// <summary>Sets <see cref="IsGenerating"/> to <see langword="false"/> and disposes
    /// the current <see cref="CancellationTokenSource"/>.</summary>
    public void End()
    {
        lock (this.syncLock)
        {
            this.isGenerating = false;
            this.currentGenerationCts?.Dispose();
            this.currentGenerationCts = null;
        }
    }

    /// <summary>Cancels the in-flight generation token without changing
    /// <see cref="IsGenerating"/>.</summary>
    public void Abort()
    {
        lock (this.syncLock)
        {
            if (this.currentGenerationCts is { IsCancellationRequested: false } cts)
            {
                cts.Cancel();
            }
        }
    }

    /// <summary>Records the barge-in interruption marker for the current turn.</summary>
    public void MarkInterrupted(string playedText)
    {
        lock (this.syncLock)
        {
            this.interruptedPlayedText = playedText;
        }
    }

    /// <summary>Records the durable id of the just-persisted assistant turn.</summary>
    public void SetLastAssistantRecordId(long id)
    {
        lock (this.syncLock)
        {
            this.lastAssistantRecordId = id;
        }
    }

    /// <summary>
    /// Clears the barge-in marker. Keeps <see cref="LastAssistantRecordId"/> so a second
    /// barge-in on the same turn can still re-update the row; the id is zeroed per turn by
    /// <see cref="Begin"/>.
    /// </summary>
    public void ClearInterruption()
    {
        lock (this.syncLock)
        {
            this.interruptedPlayedText = null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.currentGenerationCts?.Dispose();
    }
}
