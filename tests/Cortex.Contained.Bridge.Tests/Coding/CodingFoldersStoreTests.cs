using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public sealed class CodingFoldersStoreTests
{
    private static CodingFoldersStore NewStore() =>
        new(Path.Combine(Directory.CreateTempSubdirectory().FullName, "coding-folders.json"));

    [Fact]
    public void Add_then_Get_returns_entry_with_policy()
    {
        var store = NewStore();
        var dir = Directory.CreateTempSubdirectory().FullName;

        Assert.True(store.Add(dir, label: "cortex", CodingPolicy.Prompt));
        var entries = store.Get();

        Assert.Single(entries);
        Assert.Equal("cortex", entries[0].Label);
        Assert.Equal(CodingPolicy.Prompt, entries[0].DefaultPolicy);
    }

    [Fact]
    public void IsAllowed_true_for_path_inside_entry()
    {
        var store = NewStore();
        var dir = Directory.CreateTempSubdirectory().FullName;
        store.Add(dir, null, CodingPolicy.YoloSafe);

        Assert.True(store.IsAllowed(Path.Combine(dir, "sub")));
        Assert.False(store.IsAllowed(Path.Combine(Path.GetTempPath(), "elsewhere-xyz")));
    }

    [Fact]
    public void GetCeiling_returns_entry_policy_or_most_restrictive_default()
    {
        var store = NewStore();
        var dir = Directory.CreateTempSubdirectory().FullName;
        store.Add(dir, null, CodingPolicy.Yolo);

        Assert.Equal(CodingPolicy.Yolo, store.GetCeiling(Path.Combine(dir, "x")));
        Assert.Equal(CodingPolicy.Prompt, store.GetCeiling("C:\\not\\allowed")); // default most-restrictive
    }
}
