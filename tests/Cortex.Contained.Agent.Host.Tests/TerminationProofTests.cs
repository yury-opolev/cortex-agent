using Cortex.Contained.Agent.Host.Agent.Autonomy;

namespace Cortex.Contained.Agent.Host.Tests;

public class TerminationProofTests
{
    [Fact]
    public void Evaluate_AllRemainingItemsHaveParkedBlockers_ReturnsGenuinelyBlocked()
    {
        var input = new TerminationProofInput(
            RemainingItemKeys: ["a", "b"],
            ParkedBlockerItemKeys: ["a", "b"],
            IsLooping: false,
            NothingLeftToAdvance: false,
            ProgressWindow: []);

        Assert.Equal(TerminationProofResult.GenuinelyBlocked, TerminationProof.Evaluate(input));
    }

    [Fact]
    public void Evaluate_RemainingItemWithoutParkedBlocker_ReturnsKeepGoing()
    {
        var input = new TerminationProofInput(
            RemainingItemKeys: ["a", "b"],
            ParkedBlockerItemKeys: ["a"],
            IsLooping: false,
            NothingLeftToAdvance: false,
            ProgressWindow: []);

        Assert.Equal(TerminationProofResult.KeepGoing, TerminationProof.Evaluate(input));
    }

    [Fact]
    public void Evaluate_LoopingWithTwoNoProgressSnapshots_ReturnsKeepGoing()
    {
        var input = new TerminationProofInput(
            RemainingItemKeys: [],
            ParkedBlockerItemKeys: [],
            IsLooping: true,
            NothingLeftToAdvance: true,
            ProgressWindow:
            [
                new TerminationProgressSnapshot(1, ["a.txt"], ["ledger-1"]),
                new TerminationProgressSnapshot(1, ["a.txt"], ["ledger-1"]),
            ]);

        Assert.Equal(TerminationProofResult.KeepGoing, TerminationProof.Evaluate(input));
    }

    [Fact]
    public void Evaluate_LoopingWithThreeNoProgressSnapshots_ReturnsStalled()
    {
        var input = new TerminationProofInput(
            RemainingItemKeys: [],
            ParkedBlockerItemKeys: [],
            IsLooping: true,
            NothingLeftToAdvance: true,
            ProgressWindow:
            [
                new TerminationProgressSnapshot(1, ["a.txt"], ["ledger-1"]),
                new TerminationProgressSnapshot(1, ["a.txt"], ["ledger-1"]),
                new TerminationProgressSnapshot(1, ["a.txt"], ["ledger-1"]),
            ]);

        Assert.Equal(TerminationProofResult.Stalled, TerminationProof.Evaluate(input));
    }

    [Fact]
    public void Evaluate_LoopingWithTodoProgressInWindow_ReturnsKeepGoing()
    {
        var input = new TerminationProofInput(
            RemainingItemKeys: [],
            ParkedBlockerItemKeys: [],
            IsLooping: true,
            NothingLeftToAdvance: true,
            ProgressWindow:
            [
                new TerminationProgressSnapshot(1, [], []),
                new TerminationProgressSnapshot(2, [], []),
                new TerminationProgressSnapshot(2, [], []),
            ]);

        Assert.Equal(TerminationProofResult.KeepGoing, TerminationProof.Evaluate(input));
    }

    [Fact]
    public void Evaluate_LoopingWithFileProgressInWindow_ReturnsKeepGoing()
    {
        var input = new TerminationProofInput(
            RemainingItemKeys: [],
            ParkedBlockerItemKeys: [],
            IsLooping: true,
            NothingLeftToAdvance: true,
            ProgressWindow:
            [
                new TerminationProgressSnapshot(1, ["a.txt"], []),
                new TerminationProgressSnapshot(1, ["a.txt", "b.txt"], []),
                new TerminationProgressSnapshot(1, ["a.txt", "b.txt"], []),
            ]);

        Assert.Equal(TerminationProofResult.KeepGoing, TerminationProof.Evaluate(input));
    }

    [Fact]
    public void Evaluate_LoopingWithDistinctLedgerProgressInWindow_ReturnsKeepGoing()
    {
        var input = new TerminationProofInput(
            RemainingItemKeys: [],
            ParkedBlockerItemKeys: [],
            IsLooping: true,
            NothingLeftToAdvance: true,
            ProgressWindow:
            [
                new TerminationProgressSnapshot(1, [], ["ledger-1"]),
                new TerminationProgressSnapshot(1, [], ["ledger-1", "ledger-2"]),
                new TerminationProgressSnapshot(1, [], ["ledger-1", "ledger-2"]),
            ]);

        Assert.Equal(TerminationProofResult.KeepGoing, TerminationProof.Evaluate(input));
    }

    [Fact]
    public void Evaluate_LoopingWithOnlyDuplicateLedgerEntries_ReturnsStalled()
    {
        var input = new TerminationProofInput(
            RemainingItemKeys: [],
            ParkedBlockerItemKeys: [],
            IsLooping: true,
            NothingLeftToAdvance: true,
            ProgressWindow:
            [
                new TerminationProgressSnapshot(1, [], ["ledger-1"]),
                new TerminationProgressSnapshot(1, [], ["ledger-1"]),
                new TerminationProgressSnapshot(1, [], ["ledger-1"]),
            ]);

        Assert.Equal(TerminationProofResult.Stalled, TerminationProof.Evaluate(input));
    }

    [Fact]
    public void Evaluate_JudgeProseChangesWithoutObservableProgress_ReturnsStalled()
    {
        var input = new TerminationProofInput(
            RemainingItemKeys: [],
            ParkedBlockerItemKeys: [],
            IsLooping: true,
            NothingLeftToAdvance: true,
            ProgressWindow:
            [
                new TerminationProgressSnapshot(1, [], [], "made progress"),
                new TerminationProgressSnapshot(1, [], [], "made even more progress"),
                new TerminationProgressSnapshot(1, [], [], "almost done"),
            ]);

        Assert.Equal(TerminationProofResult.Stalled, TerminationProof.Evaluate(input));
    }
}