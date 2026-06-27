using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tests.Agent;

public sealed class GenerationStateTests
{
    // ── Begin ─────────────────────────────────────────────────────────────

    [Fact]
    public void Begin_SetsIsGeneratingTrue()
    {
        using var state = new GenerationState();
        state.Begin(CancellationToken.None);

        Assert.True(state.IsGenerating);
    }

    [Fact]
    public void Begin_ClearsInterruptedPlayedText()
    {
        using var state = new GenerationState();
        state.Begin(CancellationToken.None);
        state.MarkInterrupted("some text…");

        state.Begin(CancellationToken.None); // second begin resets

        Assert.Null(state.InterruptedPlayedText);
    }

    [Fact]
    public void Begin_ResetsLastAssistantRecordId()
    {
        using var state = new GenerationState();
        state.Begin(CancellationToken.None);
        state.SetLastAssistantRecordId(42);

        state.Begin(CancellationToken.None);

        Assert.Equal(0, state.LastAssistantRecordId);
    }

    [Fact]
    public void Begin_ReturnsTokenLinkedToExternalToken()
    {
        using var cts = new CancellationTokenSource();
        using var state = new GenerationState();

        var token = state.Begin(cts.Token);

        Assert.False(token.IsCancellationRequested);
        cts.Cancel();
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Begin_AtomicallyResetsAllFourFields()
    {
        // Verify that after Begin: IsGenerating=true, interruptedPlayedText=null,
        // lastAssistantRecordId=0, and a new linked token is returned.
        using var externalCts = new CancellationTokenSource();
        using var state = new GenerationState();

        state.Begin(CancellationToken.None);
        state.MarkInterrupted("played text…");
        state.SetLastAssistantRecordId(99);

        var token = state.Begin(externalCts.Token);

        Assert.True(state.IsGenerating);
        Assert.Null(state.InterruptedPlayedText);
        Assert.Equal(0, state.LastAssistantRecordId);
        Assert.False(token.IsCancellationRequested);

        externalCts.Cancel();
        Assert.True(token.IsCancellationRequested);
    }

    // ── End ───────────────────────────────────────────────────────────────

    [Fact]
    public void End_SetsIsGeneratingFalse()
    {
        using var state = new GenerationState();
        state.Begin(CancellationToken.None);
        state.End();

        Assert.False(state.IsGenerating);
    }

    // ── Abort ─────────────────────────────────────────────────────────────

    [Fact]
    public void Abort_CancelsInFlightToken()
    {
        using var state = new GenerationState();
        var token = state.Begin(CancellationToken.None);
        Assert.False(token.IsCancellationRequested);

        state.Abort();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Abort_DoesNotThrow_WhenNotGenerating()
    {
        using var state = new GenerationState();
        // Should not throw even when no generation is in progress.
        state.Abort();
    }

    // ── MarkInterrupted / InterruptedPlayedText ───────────────────────────

    [Fact]
    public void MarkInterrupted_SetsInterruptedPlayedText()
    {
        using var state = new GenerationState();
        state.Begin(CancellationToken.None);
        state.MarkInterrupted("hello…");

        Assert.Equal("hello…", state.InterruptedPlayedText);
    }

    // ── SetLastAssistantRecordId / LastAssistantRecordId ──────────────────

    [Fact]
    public void SetLastAssistantRecordId_ReflectedByProperty()
    {
        using var state = new GenerationState();
        state.SetLastAssistantRecordId(123);

        Assert.Equal(123, state.LastAssistantRecordId);
    }

    // ── ClearInterruption ─────────────────────────────────────────────────

    [Fact]
    public void ClearInterruption_ClearsInterruptedPlayedText_KeepsRecordId()
    {
        using var state = new GenerationState();
        state.Begin(CancellationToken.None);
        state.MarkInterrupted("played…");
        state.SetLastAssistantRecordId(7);

        state.ClearInterruption();

        Assert.Null(state.InterruptedPlayedText);
        Assert.Equal(7, state.LastAssistantRecordId);
    }

    [Fact]
    public void Begin_AfterClearInterruption_ResetsRecordId()
    {
        using var state = new GenerationState();
        state.Begin(CancellationToken.None);
        state.SetLastAssistantRecordId(7);
        state.ClearInterruption();

        state.Begin(CancellationToken.None); // new turn zeroes the record id

        Assert.Equal(0, state.LastAssistantRecordId);
    }
}
