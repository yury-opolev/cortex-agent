namespace Cortex.Contained.Agent.Host.Agent.Autonomy;

internal enum TerminationProofResult
{
    GenuinelyBlocked,
    Stalled,
    KeepGoing,
}

internal sealed record TerminationProgressSnapshot(
    int CompletedTodoCount,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> DistinctLedgerEntryKeys,
    string? JudgeProse = null);

internal sealed record TerminationProofInput(
    IReadOnlyList<string> RemainingItemKeys,
    IReadOnlyList<string> ParkedBlockerItemKeys,
    bool IsLooping,
    bool NothingLeftToAdvance,
    IReadOnlyList<TerminationProgressSnapshot> ProgressWindow);

/// <summary>
/// Provides observable, deterministic reasons to stop an autonomous run without trusting prose.
/// </summary>
internal static class TerminationProof
{
    private const int NoProgressWindow = 3;

    public static TerminationProofResult Evaluate(TerminationProofInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.RemainingItemKeys.Count > 0
            && input.RemainingItemKeys.All(item => input.ParkedBlockerItemKeys.Contains(item)))
        {
            return TerminationProofResult.GenuinelyBlocked;
        }

        if (input.IsLooping
            && input.NothingLeftToAdvance
            && HasFullNoProgressWindow(input.ProgressWindow))
        {
            return TerminationProofResult.Stalled;
        }

        return TerminationProofResult.KeepGoing;
    }

    private static bool HasFullNoProgressWindow(IReadOnlyList<TerminationProgressSnapshot> progressWindow)
    {
        if (progressWindow.Count < NoProgressWindow)
        {
            return false;
        }

        var start = progressWindow.Count - NoProgressWindow;
        var first = progressWindow[start];
        var completedTodos = first.CompletedTodoCount;
        var changedFiles = first.ChangedFiles.ToHashSet(StringComparer.Ordinal);
        var ledgerEntries = first.DistinctLedgerEntryKeys.ToHashSet(StringComparer.Ordinal);

        for (var i = start + 1; i < progressWindow.Count; i++)
        {
            var current = progressWindow[i];
            if (current.CompletedTodoCount > completedTodos)
            {
                return false;
            }

            if (current.ChangedFiles.Any(file => !changedFiles.Contains(file)))
            {
                return false;
            }

            if (current.DistinctLedgerEntryKeys.Any(entry => !ledgerEntries.Contains(entry)))
            {
                return false;
            }

            completedTodos = Math.Max(completedTodos, current.CompletedTodoCount);
            changedFiles.UnionWith(current.ChangedFiles);
            ledgerEntries.UnionWith(current.DistinctLedgerEntryKeys);
        }

        return true;
    }
}