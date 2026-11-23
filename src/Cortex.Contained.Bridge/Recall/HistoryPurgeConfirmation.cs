using System.Globalization;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Recall;

/// <summary>
/// The confirmation the owner reads before a history deletion: how many messages go, how many dependent
/// memories exist, and what the chosen option does with them. Built from the agent's preview.
/// </summary>
public static class HistoryPurgeConfirmation
{
    /// <summary>The text for <paramref name="dependentMemories"/> (<c>review</c> or <c>delete</c>).</summary>
    public static string Describe(HistoryPurgePreviewDto preview, string dependentMemories)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Messages == 0)
        {
            return "Nothing matches: no messages would be deleted.";
        }

        var text = string.Create(CultureInfo.InvariantCulture, $"This deletes {preview.Messages} messages and erases them from the search index.");
        var frozen = FrozenCorpora(preview);
        if (preview.DependentMemories == 0)
        {
            return text + " No memories were created in that period." + frozen;
        }

        return text + (DependentMemoryActions.IsDelete(dependentMemories)
            ? string.Create(CultureInfo.InvariantCulture, $" {preview.DependentMemories} memories were created in that period. You chose Delete: they are erased permanently together with the history.")
            : string.Create(CultureInfo.InvariantCulture, $" {preview.DependentMemories} memories were created in that period. You chose Review: they are hidden from the agent until you keep or delete them under Recall → Needs review.")) + frozen;
    }

    // D3: overlapping frozen evaluation corpora are destroyed here; an exported one leaves a copy only the owner can delete.
    private static string FrozenCorpora(HistoryPurgePreviewDto preview)
    {
        if (preview.FrozenCorpusVersions.Count == 0)
        {
            return string.Empty;
        }

        var text = $" It also destroys the frozen evaluation corpus {string.Join(", ", preview.FrozenCorpusVersions.Select(v => v.Version))} on this machine (freeze a new version afterwards).";
        var offsite = preview.FrozenCorpusVersions.Where(v => v.ExportedAt is not null || v.Uploaded).Select(v => v.PackageFileName).ToList();
        return offsite.Count == 0
            ? text
            : text + $" Its off-machine copy must be deleted by you: file {string.Join(", ", offsite)} of the SafeExchange secret cortex-recovery-kit (Recall lists it until you confirm the deletion).";
    }
}
