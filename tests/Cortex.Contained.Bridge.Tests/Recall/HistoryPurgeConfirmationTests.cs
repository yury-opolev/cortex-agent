using Cortex.Contained.Bridge.Recall;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Tests.Recall;

public sealed class HistoryPurgeConfirmationTests
{
    [Fact]
    public void Describe_Review_StatesCountAndChoice()
    {
        var text = HistoryPurgeConfirmation.Describe(new HistoryPurgePreviewDto { Messages = 40, DependentMemories = 3 }, DependentMemoryActions.Review);

        Assert.Equal(
            "This deletes 40 messages and erases them from the search index. 3 memories were created in that period. You chose Review: they are hidden from the agent until you keep or delete them under Recall → Needs review.",
            text);
    }

    [Fact]
    public void Describe_Delete_SaysPermanent()
    {
        var text = HistoryPurgeConfirmation.Describe(new HistoryPurgePreviewDto { Messages = 40, DependentMemories = 3 }, DependentMemoryActions.Delete);

        Assert.EndsWith("3 memories were created in that period. You chose Delete: they are erased permanently together with the history.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_NoMemoriesOrNoMessages_SaysSo()
    {
        Assert.EndsWith("No memories were created in that period.", HistoryPurgeConfirmation.Describe(new HistoryPurgePreviewDto { Messages = 1 }, DependentMemoryActions.Delete), StringComparison.Ordinal);
        Assert.Equal("Nothing matches: no messages would be deleted.", HistoryPurgeConfirmation.Describe(new HistoryPurgePreviewDto(), DependentMemoryActions.Review));
    }

    [Fact]
    public void Describe_OverlappingExportedFrozenCorpus_NamesTheOffMachineCopyToDelete()
    {
        var preview = new HistoryPurgePreviewDto
        {
            Messages = 2,
            FrozenCorpusVersions =
            [
                new FrozenCorpusVersionDto { Version = "v1", PackageFileName = "cortex-eval-corpus-v1.cxpkg", ExportedAt = DateTimeOffset.UnixEpoch },
                new FrozenCorpusVersionDto { Version = "v2", PackageFileName = "cortex-eval-corpus-v2.cxpkg" },
            ],
        };

        var text = HistoryPurgeConfirmation.Describe(preview, DependentMemoryActions.Review);

        Assert.EndsWith(
            "No memories were created in that period. It also destroys the frozen evaluation corpus v1, v2 on this machine (freeze a new version afterwards). " +
            "Its off-machine copy must be deleted by you: file cortex-eval-corpus-v1.cxpkg of the SafeExchange secret cortex-recovery-kit (Recall lists it until you confirm the deletion).",
            text,
            StringComparison.Ordinal);
    }
}
