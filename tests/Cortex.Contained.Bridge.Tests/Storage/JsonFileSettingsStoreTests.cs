using Cortex.Contained.Bridge.Storage;

namespace Cortex.Contained.Bridge.Tests.Storage;

public sealed class JsonFileSettingsStoreTests
{
    private sealed class TestDoc
    {
        public string? Name { get; set; }
        public int Count { get; set; }
    }

    private sealed class TestStore : JsonFileSettingsStore<TestDoc>
    {
        public TestStore(string filePath)
            : base(filePath)
        {
        }

        public TestDoc Read() => this.Load();

        public void Write(TestDoc value) => this.Save(value);
    }

    private static TestStore NewStore(out string path)
    {
        path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "settings.json");
        return new TestStore(path);
    }

    [Fact]
    public void Load_returns_default_when_file_absent()
    {
        var doc = NewStore(out _).Read();
        Assert.Null(doc.Name);
        Assert.Equal(0, doc.Count);
    }

    [Fact]
    public void Save_then_Load_roundtrips()
    {
        var store = NewStore(out _);
        store.Write(new TestDoc { Name = "alpha", Count = 7 });

        var doc = store.Read();
        Assert.Equal("alpha", doc.Name);
        Assert.Equal(7, doc.Count);
    }

    [Fact]
    public void Load_returns_default_when_file_corrupt()
    {
        var store = NewStore(out var path);
        File.WriteAllText(path, "{ this is not valid json");

        var doc = store.Read();
        Assert.Null(doc.Name);
        Assert.Equal(0, doc.Count);
    }

    [Fact]
    public void Load_returns_default_when_file_empty()
    {
        var store = NewStore(out var path);
        File.WriteAllText(path, "   ");

        var doc = store.Read();
        Assert.Null(doc.Name);
        Assert.Equal(0, doc.Count);
    }

    [Fact]
    public void Save_persists_to_disk_for_new_store_instance()
    {
        var store = NewStore(out var path);
        store.Write(new TestDoc { Name = "beta", Count = 3 });

        var reopened = new TestStore(path);
        var doc = reopened.Read();
        Assert.Equal("beta", doc.Name);
        Assert.Equal(3, doc.Count);
    }

    [Fact]
    public void Save_creates_parent_directory()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(root, "nested", "deeper", "settings.json");
        var store = new TestStore(path);

        store.Write(new TestDoc { Name = "gamma", Count = 1 });

        Assert.True(File.Exists(path));
    }
}
