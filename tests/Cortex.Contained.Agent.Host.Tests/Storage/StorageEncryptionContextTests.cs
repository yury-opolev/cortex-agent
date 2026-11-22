using System.Security.Cryptography;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

public sealed class StorageEncryptionContextTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"enc-context-{Guid.NewGuid():N}");
    private readonly StorageLayout layout;

    public StorageEncryptionContextTests()
    {
        Directory.CreateDirectory(this.root);
        this.layout = new StorageLayout(
            Path.Combine(this.root, "state"),
            Path.Combine(this.root, "data"),
            Path.Combine(this.root, "state", "memory", "memory.db"),
            Path.Combine(this.root, "state", "memory", "memories"));
        Directory.CreateDirectory(this.layout.MemoriesDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void Resolve_NothingConfiguredNothingEncrypted_Off()
    {
        var context = StorageEncryptionContext.Resolve(new StorageOptions(), this.layout);

        Assert.False(context.Enabled);
        Assert.Equal("off", context.Source);
        Assert.False(context.ProtectsNewWrites);
    }

    [Fact]
    public void Resolve_Config_On()
    {
        var context = StorageEncryptionContext.Resolve(new StorageOptions { EncryptAtRest = true }, this.layout);

        Assert.True(context.Enabled);
        Assert.Equal("config", context.Source);
    }

    [Fact]
    public void Resolve_Marker_WinsOverConfigOff()
    {
        new StorageEncryptionMarker { KeyFingerprint = "0123456789abcdef" }.Write(this.layout.Marker);

        var context = StorageEncryptionContext.Resolve(new StorageOptions { EncryptAtRest = false }, this.layout);

        Assert.True(context.Enabled);
        Assert.Equal("marker", context.Source);
        Assert.Equal("0123456789abcdef", context.Marker!.KeyFingerprint);
    }

    [Fact]
    public void Resolve_UnreadableMarker_StaysEncrypted()
    {
        File.WriteAllText(this.layout.Marker, "{ not json");

        var context = StorageEncryptionContext.Resolve(new StorageOptions(), this.layout);

        Assert.True(context.Enabled);
        Assert.Equal(string.Empty, context.Marker!.KeyFingerprint);
    }

    [Fact]
    public void Resolve_EncryptedMessagesDbWithoutMarker_Detected()
    {
        File.WriteAllBytes(this.layout.MessagesDatabase, RandomNumberGenerator.GetBytes(4096));

        var context = StorageEncryptionContext.Resolve(new StorageOptions(), this.layout);

        Assert.True(context.Enabled);
        Assert.Equal("detected", context.Source);
    }

    [Fact]
    public void Resolve_EncryptedMemoryFileWithoutMarker_Detected()
    {
        var cipher = new StorageFileCipher(RandomNumberGenerator.GetBytes(32), "memory-content");
        File.WriteAllBytes(Path.Combine(this.layout.MemoriesDirectory, "a.memory.data"), cipher.Encrypt("x"u8.ToArray()));

        Assert.Equal("detected", StorageEncryptionContext.Resolve(new StorageOptions(), this.layout).Source);
    }

    [Fact]
    public void MarkRestartPending_PersistsMarkerAndProtectsNewWrites()
    {
        var context = StorageEncryptionContext.Resolve(new StorageOptions(), this.layout);

        context.MarkRestartPending(new StorageEncryptionMarker { KeyFingerprint = "fp", EnabledAt = DateTimeOffset.UnixEpoch });

        Assert.False(context.Enabled);
        Assert.True(context.RestartPending);
        Assert.True(context.ProtectsNewWrites);
        Assert.Equal("fp", StorageEncryptionMarker.Read(this.layout.Marker)!.KeyFingerprint);
        Assert.False(File.Exists(this.layout.Marker + ".tmp"));
    }

    [Fact]
    public void EnsureMarker_DetectedWithoutMarker_WritesTheFingerprintOnce()
    {
        File.WriteAllBytes(this.layout.MessagesDatabase, RandomNumberGenerator.GetBytes(4096));
        var context = StorageEncryptionContext.Resolve(new StorageOptions(), this.layout);

        Assert.True(context.EnsureMarker("fp1", DateTimeOffset.UnixEpoch));
        Assert.False(context.EnsureMarker("fp2", DateTimeOffset.UnixEpoch));
        Assert.Equal("fp1", StorageEncryptionMarker.Read(this.layout.Marker)!.KeyFingerprint);
    }
}
