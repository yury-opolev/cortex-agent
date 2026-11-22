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
        if (preview.DependentMemories == 0)
        {
            return text + " No memories were created in that period.";
        }

        return text + (DependentMemoryActions.IsDelete(dependentMemories)
            ? string.Create(CultureInfo.InvariantCulture, $" {preview.DependentMemories} memories were created in that period. You chose Delete: they are erased permanently together with the history.")
            : string.Create(CultureInfo.InvariantCulture, $" {preview.DependentMemories} memories were created in that period. You chose Review: they are hidden from the agent until you keep or delete them under Recall → Needs review."));
    }
}
