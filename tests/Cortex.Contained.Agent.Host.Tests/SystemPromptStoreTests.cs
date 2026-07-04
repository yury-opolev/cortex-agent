using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.SystemPrompt;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class SystemPromptStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "sp-store-" + Guid.NewGuid().ToString("N"));

    private SystemPromptStore NewStore() =>
        new(Path.Combine(this.dir, "system-prompt.json"), NullLogger<SystemPromptStore>.Instance);

    [Fact]
    public void Read_MissingFile_ReturnsDefaults()
    {
        var config = NewStore().Read();
        Assert.Equal(SystemPromptDefaults.MainTemplate, config.MainTemplate);
    }

    [Fact]
    public void Write_ThenRead_RoundTrips()
    {
        var store = NewStore();
        var config = SystemPromptDefaults.Create();
        config.CodingRelay = "custom relay";

        var result = store.Write(config);

        Assert.True(result.IsValid);
        Assert.Equal("custom relay", NewStore().Read().CodingRelay); // fresh store reads from disk
    }

    [Fact]
    public void Write_Invalid_DoesNotPersist()
    {
        var store = NewStore();
        var bad = SystemPromptDefaults.Create();
        bad.MainTemplate = "{{nope}}";

        var result = store.Write(bad);

        Assert.False(result.IsValid);
        Assert.Equal(SystemPromptDefaults.MainTemplate, NewStore().Read().MainTemplate);
    }

    [Fact]
    public void Reset_RestoresDefaults()
    {
        var store = NewStore();
        var config = SystemPromptDefaults.Create();
        config.VoiceMode = "changed";
        store.Write(config);

        var reset = store.Reset();

        Assert.Equal(SystemPromptDefaults.VoiceMode, reset.VoiceMode);
        Assert.Equal(SystemPromptDefaults.VoiceMode, NewStore().Read().VoiceMode);
    }

    [Fact]
    public void Fingerprint_ChangesWhenConfigChanges()
    {
        var store = NewStore();
        var before = store.Fingerprint();

        var config = SystemPromptDefaults.Create();
        config.CodingRelay = "different";
        store.Write(config);

        Assert.NotEqual(before, store.Fingerprint());
    }

    public void Dispose()
    {
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
