using System.Text;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

/// <summary>The shutdown snapshot of live sessions under encryption at rest.</summary>
[Trait("Category", "Integrity")]
public sealed class EncryptedSessionSnapshotTests : IDisposable
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"snapshot-enc-{Guid.NewGuid():N}");
    private readonly StorageLayout layout;

    public EncryptedSessionSnapshotTests()
    {
        Directory.CreateDirectory(this.dir);
        this.layout = new StorageLayout(this.dir, this.dir, Path.Combine(this.dir, "memory", "memory.db"), Path.Combine(this.dir, "memory", "memories"));
    }

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string SnapshotPath => Path.Combine(this.dir, SessionSnapshotSerializer.SnapshotFileName);

    private static AgentSessionStore Sessions(string? text = null)
    {
        var store = new AgentSessionStore(new SessionConfig(), new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
        if (text is not null)
        {
            store.GetOrCreate("ch").AddMessage(new LlmMessage { Role = "user", Content = text });
        }

        return store;
    }

    private static StorageKeyRing Ring(bool provided)
    {
        var ring = new StorageKeyRing();
        if (provided)
        {
            ring.Provide(Key);
        }

        return ring;
    }

    private static StorageFileCipher Cipher(StorageKeyRing ring) =>
        new(ring.KeyFor(SessionSnapshotSerializer.Purpose), SessionSnapshotSerializer.Purpose);

    private bool SnapshotContains(string marker) =>
        File.ReadAllBytes(this.SnapshotPath).AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) >= 0;

    [Fact]
    public async Task SaveWithCipher_NoPlaintextOnDisk_RestoredOnlyWithTheKey()
    {
        var ring = Ring(provided: true);
        await SessionSnapshotSerializer.SaveAsync(SessionSnapshotSerializer.CaptureSnapshot(Sessions("zqsnapmarker")), this.dir, NullLogger.Instance, Cipher(ring));

        Assert.False(this.SnapshotContains("zqsnapmarker"));
        Assert.Equal(0, SessionSnapshotSerializer.TryRestore(Sessions(), this.dir, NullLogger.Instance));
        Assert.True(File.Exists(this.SnapshotPath));

        var restoredInto = Sessions();
        Assert.Equal(1, SessionSnapshotSerializer.TryRestore(restoredInto, this.dir, NullLogger.Instance, Cipher(ring)));
        Assert.True(restoredInto.TryGet("ch", out _));
        Assert.False(File.Exists(this.SnapshotPath));
    }

    [Fact]
    public async Task Stop_WhileEnablingIsPending_WritesTheSnapshotEncrypted()
    {
        var context = StorageEncryptionContext.Resolve(new StorageOptions(), this.layout);
        context.MarkRestartPending(new StorageEncryptionMarker { KeyFingerprint = "fp" });
        using var service = new AgentProcessingService(
            Substitute.For<IAgentRuntime>(), Sessions("zqpendingmarker"), this.dir, NullLogger<AgentProcessingService>.Instance, context, Ring(provided: true));

        await service.StopAsync(CancellationToken.None);

        Assert.True(SessionSnapshotSerializer.IsEncrypted(this.dir));
        Assert.False(this.SnapshotContains("zqpendingmarker"));
    }

    [Fact]
    public async Task Stop_EncryptedWithoutKey_WritesNoSnapshotAtAll()
    {
        var context = StorageEncryptionContext.Resolve(new StorageOptions { EncryptAtRest = true }, this.layout);
        using var service = new AgentProcessingService(
            Substitute.For<IAgentRuntime>(), Sessions("zqnokeymarker"), this.dir, NullLogger<AgentProcessingService>.Instance, context, Ring(provided: false));

        await service.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(this.SnapshotPath));
    }

    [Fact]
    public async Task Start_EncryptedSnapshot_RestoredWhenTheKeyArrivesBeforeProcessingStarts()
    {
        var writer = Ring(provided: true);
        await SessionSnapshotSerializer.SaveAsync(SessionSnapshotSerializer.CaptureSnapshot(Sessions("zqrestoremarker")), this.dir, NullLogger.Instance, Cipher(writer));
        var context = StorageEncryptionContext.Resolve(new StorageOptions { EncryptAtRest = true }, this.layout);
        var runtime = Substitute.For<IAgentRuntime>();
        var sessions = Sessions();
        var ring = Ring(provided: false);
        using var service = new AgentProcessingService(runtime, sessions, this.dir, NullLogger<AgentProcessingService>.Instance, context, ring);

        await service.StartAsync(CancellationToken.None);
        await runtime.DidNotReceive().StartProcessingAsync(Arg.Any<CancellationToken>());
        ring.Provide(Key);
        await service.DeferredStart.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(sessions.TryGet("ch", out _));
        await runtime.Received(1).StartProcessingAsync(Arg.Any<CancellationToken>());
        Assert.False(File.Exists(this.SnapshotPath));
    }
}
