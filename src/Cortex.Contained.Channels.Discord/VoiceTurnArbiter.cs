using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Channels.Discord;

/// <summary>Side-effect callbacks the arbiter drives. Injected so the state
/// machine is unit-testable without Discord / audio.</summary>
internal sealed class VoiceTurnArbiterCallbacks
{
    public required Func<Task<PlaybackProgress>> StopPlayback { get; init; }
    public required Action<string> ReenqueueSentence { get; init; }
    public required Func<string, Task> CancelGenerationAndReabsorb { get; init; }
    public required Func<PlaybackProgress, Task> CommitInterrupt { get; init; }
    public required Func<string, float, CancellationToken, Task<InterruptClass>> Classify { get; init; }
}

/// <summary>
/// Owns the per-session conversation phase and serializes every transition
/// (one owner; no double cancel/absorb — the connectionGate lesson). Pure of
/// Discord/audio: all side effects go through injected callbacks so it is
/// unit-testable. See docs/superpowers/specs/2026-05-16-voice-turn-arbitration-design.md.
/// </summary>
internal sealed partial class VoiceTurnArbiter : IDisposable
{
    private readonly VoiceTurnArbiterCallbacks cb;
    private readonly bool bargeInEnabled;
    private readonly ILogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);

    public VoiceTurnArbiter(VoiceTurnArbiterCallbacks cb, bool bargeInEnabled, ILogger logger)
    {
        this.cb = cb;
        this.bargeInEnabled = bargeInEnabled;
        this.logger = logger;
    }

    // volatile: the phase setters below run on the Discord receive-loop thread
    // while OnUserSpeechOnsetAsync runs on a Task.Run thread. Gating the setters
    // would stall audio receive across the (slow) classify window, so we make
    // the field volatile instead — visibility is guaranteed and the gated
    // onset path always re-reads the authoritative value under the lock.
    private volatile ConversationPhase phase = ConversationPhase.Listening;
    private volatile bool disposed;

    public ConversationPhase Phase
    {
        get => this.phase;
        private set => this.phase = value;
    }

    /// <summary>Test/diagnostic seam — set the phase directly.</summary>
    public void SetPhase(ConversationPhase phase) => this.phase = phase;

    public async Task OnUserSpeechOnsetAsync(string partial, float pEou, CancellationToken ct)
    {
        if (this.disposed)
        {
            return;
        }

        try
        {
            await this.gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return; // arbiter torn down while this fire-and-forget onset was queued
        }
        catch (OperationCanceledException)
        {
            return; // voice session ending
        }

        try
        {
            var action = TurnDecision.Decide(
                this.Phase, TurnEvent.UserSpeechOnset, InterruptClass.None, this.bargeInEnabled);

            switch (action)
            {
                case TurnAction.Accumulate:
                case TurnAction.Ignore:
                    return;

                case TurnAction.CancelCommitAndReabsorb:
                    await this.cb.CancelGenerationAndReabsorb("committed").ConfigureAwait(false);
                    this.Phase = ConversationPhase.Listening;
                    return;

                case TurnAction.CancelGenAndReabsorb:
                    await this.cb.CancelGenerationAndReabsorb("thinking").ConfigureAwait(false);
                    this.Phase = ConversationPhase.Listening;
                    return;

                case TurnAction.StopPlaybackPendingClassify:
                    var progress = await this.cb.StopPlayback().ConfigureAwait(false);
                    var cls = await this.cb.Classify(partial, pEou, ct).ConfigureAwait(false);
                    var next = TurnDecision.Decide(
                        ConversationPhase.Speaking, TurnEvent.ClassifierResult, cls, this.bargeInEnabled);
                    if (next == TurnAction.ResumeFromSentenceStart
                        && progress.InterruptedSentenceText is { } s)
                    {
                        this.cb.ReenqueueSentence(s);
                        this.Phase = ConversationPhase.Speaking;
                        this.LogResumed();
                    }
                    else
                    {
                        await this.cb.CommitInterrupt(progress).ConfigureAwait(false);
                        this.Phase = ConversationPhase.Listening;
                        this.LogInterrupted(cls);
                    }

                    return;

                default:
                    return;
            }
        }
        finally
        {
            try
            {
                this.gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Arbiter disposed mid-critical-section during voice teardown.
            }
        }
    }

    public void OnUserCommit() => this.Phase = ConversationPhase.Committed;

    public void OnAgentSentToLlm() => this.Phase = ConversationPhase.Thinking;

    public void OnAgentFirstAudio() => this.Phase = ConversationPhase.Speaking;

    public void OnAgentFinished() => this.Phase = ConversationPhase.Listening;

    public void Dispose()
    {
        this.disposed = true;
        this.gate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: barge-in committed class={Cls}")]
    private partial void LogInterrupted(InterruptClass cls);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: barge-in resumed (backchannel)")]
    private partial void LogResumed();
}
