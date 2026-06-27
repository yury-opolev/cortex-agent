using Cortex.Contained.Bridge;

namespace Cortex.Contained.Bridge.Tests;

public class SettingsDiffTests
{
    [Fact]
    public void ModelDiff_NoChanges_EmptyBoth()
    {
        var (added, removed) = SettingsDiff.ModelDiff(
            ["claude-opus-4.7", "claude-sonnet-4.6"],
            ["claude-sonnet-4.6", "claude-opus-4.7"]);

        Assert.Empty(added);
        Assert.Empty(removed);
    }

    [Fact]
    public void ModelDiff_OnlyAdditions_ReportsAdded()
    {
        var (added, removed) = SettingsDiff.ModelDiff(
            ["claude-opus-4.7"],
            ["claude-opus-4.7", "gpt-5", "o4-mini"]);

        Assert.Equal(["gpt-5", "o4-mini"], added);
        Assert.Empty(removed);
    }

    [Fact]
    public void ModelDiff_OnlyRemovals_ReportsRemoved()
    {
        var (added, removed) = SettingsDiff.ModelDiff(
            ["claude-opus-4.7", "claude-sonnet-4.6", "claude-haiku-4.5"],
            ["claude-opus-4.7"]);

        Assert.Empty(added);
        Assert.Equal(["claude-haiku-4.5", "claude-sonnet-4.6"], removed);
    }

    [Fact]
    public void ModelDiff_Mixed_ReportsBothSorted()
    {
        var (added, removed) = SettingsDiff.ModelDiff(
            ["b", "a", "c"],
            ["c", "d", "e"]);

        Assert.Equal(["d", "e"], added);
        Assert.Equal(["a", "b"], removed);
    }

    [Fact]
    public void ModelDiff_EmptyBefore_AllAdded()
    {
        var (added, removed) = SettingsDiff.ModelDiff([], ["one", "two"]);

        Assert.Equal(["one", "two"], added);
        Assert.Empty(removed);
    }

    [Fact]
    public void ModelDiff_EmptyAfter_AllRemoved()
    {
        var (added, removed) = SettingsDiff.ModelDiff(["one", "two"], []);

        Assert.Empty(added);
        Assert.Equal(["one", "two"], removed);
    }
}
