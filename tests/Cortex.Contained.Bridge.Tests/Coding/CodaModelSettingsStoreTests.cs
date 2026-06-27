using Cortex.Contained.Bridge.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public sealed class CodaModelSettingsStoreTests
{
    private static CodaModelSettingsStore NewStore() =>
        new(System.IO.Path.Combine(Directory.CreateTempSubdirectory().FullName, "coda-model.json"));

    [Fact]
    public void Get_defaults_to_nulls_when_absent()
    {
        var s = NewStore().Get();
        Assert.Null(s.Provider);
        Assert.Null(s.Model);
    }

    [Fact]
    public void Set_then_Get_roundtrips()
    {
        var store = NewStore();
        store.Set("copilot", "gpt-4o");
        var s = store.Get();
        Assert.Equal("copilot", s.Provider);
        Assert.Equal("gpt-4o", s.Model);
    }

    [Fact]
    public void Set_empty_strings_are_stored_as_null()
    {
        var store = NewStore();
        store.Set("  ", "");
        var s = store.Get();
        Assert.Null(s.Provider);
        Assert.Null(s.Model);
    }
}
