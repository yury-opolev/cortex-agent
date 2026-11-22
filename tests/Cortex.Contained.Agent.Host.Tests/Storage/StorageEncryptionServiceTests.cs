using System.Runtime.CompilerServices;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

public sealed class StorageEncryptionServiceTests : IDisposable
{
    private static readonly string KeyA = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly string KeyB = Convert.ToBase64String(Enumerable.Range(2, 32).Select(i => (byte)i).ToArray());

    private readonly string root = Path.Combine(Path.GetTempPath(), $"enc-service-{Guid.NewGuid():N}");
    private readonly StorageLayout layout;
    private readonly IHostApplicationLifetime lifetime = Substitute.For<IHostApplicationLifetime>();
    private readonly IDiskSpace disk = Substitute.For<IDiskSpace>();
    private readonly IStorageState messages = Substitute.For<IStorageState>();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));

    public StorageEncryptionServiceTests()
    {
        this.layout = new StorageLayout(
            Path.Combine(this.root, "state"),
            Path.Combine(this.root, "data"),
            Path.Combine(this.root, "state", "memory", "memory.db"),
            Path.Combine(this.root, "state", "memory", "memories"));
        Directory.CreateDirectory(this.layout.MemoriesDirectory);
        this.disk.AvailableBytes(Arg.Any<string>()).Returns(long.MaxValue);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private StorageEncryptionService Service(StorageKeyRing ring, StorageOptions? options = null, StorageInventory? inventory = null) => new(
        StorageEncryptionContext.Resolve(options ?? new StorageOptions(), this.layout),
        ring,
        this.messages,
        inventory ?? new StorageInventory(this.layout),
        this.disk,
        this.lifetime,
        this.time,
        NullLogger<StorageEncryptionService>.Instance,
        memory: null,
        selfRestart: true);

    private static StorageKeyRing Ring(string? key = null)
    {
        var ring = new StorageKeyRing();
        if (key is not null)
        {
            ring.Provide(key);
        }

        return ring;
    }

    private static StorageEncryptionEnableRequestDto Request(string key) => new() { KeyFingerprint = StorageKeyFingerprint.Compute(key) };

    [Fact]
    public async Task Enable_MatchingKey_ErasesEvaluationWritesMarkerAndRestarts()
    {
        var run = Path.Combine(this.layout.EvaluationDirectory, "hybrid", "runs", "r1");
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "messages.db"), "zqevalmarker");
        var service = this.Service(Ring(KeyA));

        var result = service.Enable(Request(KeyA));

        Assert.True(result.Accepted);
        Assert.True(result.RestartScheduled);
        Assert.False(Directory.Exists(this.layout.EvaluationDirectory));
        var marker = StorageEncryptionMarker.Read(this.layout.Marker)!;
        Assert.Equal(StorageKeyFingerprint.Compute(KeyA), marker.KeyFingerprint);
        Assert.Equal(this.time.GetUtcNow(), marker.EnabledAt);
        Assert.True(service.GetStatus().RestartPending);

        this.lifetime.DidNotReceive().StopApplication();
        this.time.Advance(StorageEncryptionService.RestartDelay);
        await WaitUntilAsync(() => this.lifetime.ReceivedCalls().Any());
        this.lifetime.Received(1).StopApplication();
    }

    [Fact]
    public void Enable_WithoutKeyOrWithAnotherKey_RefusedAndNothingWritten()
    {
        Assert.False(this.Service(Ring()).Enable(Request(KeyA)).Accepted);
        var mismatch = this.Service(Ring(KeyB)).Enable(Request(KeyA));

        Assert.False(mismatch.Accepted);
        Assert.Contains(mismatch.Errors, e => e.Contains("different storage key", StringComparison.Ordinal));
        Assert.False(File.Exists(this.layout.Marker));
        this.lifetime.DidNotReceive().StopApplication();
    }

    [Fact]
    public void Enable_NotEnoughSpace_RefusedWithTheNumbers()
    {
        this.disk.AvailableBytes(Arg.Any<string>()).Returns(1024);

        var result = this.Service(Ring(KeyA)).Enable(Request(KeyA));

        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, e => e.StartsWith("Not enough free space on the state volume", StringComparison.Ordinal));
        Assert.False(File.Exists(this.layout.Marker));
    }

    [Fact]
    public void Enable_AlreadyEncrypted_NoOp()
    {
        var result = this.Service(Ring(KeyA), new StorageOptions { EncryptAtRest = true }).Enable(Request(KeyA));

        Assert.True(result.AlreadyEnabled);
        Assert.False(result.RestartScheduled);
        Assert.False(File.Exists(this.layout.Marker));
    }

    [Fact]
    public void Status_KeyOfAnotherFingerprintThanTheMarker_ReportedRejected()
    {
        new StorageEncryptionMarker { KeyFingerprint = StorageKeyFingerprint.Compute(KeyA) }.Write(this.layout.Marker);
        var ring = new StorageKeyRing { ExpectedFingerprint = StorageKeyFingerprint.Compute(KeyA) };
        ring.Provide(KeyB);
        this.messages.State.Returns(StorageState.AwaitingKey);

        var status = this.Service(ring).GetStatus();

        Assert.True(status.EncryptAtRest);
        Assert.Equal("marker", status.Source);
        Assert.Equal(StorageKeyStates.Rejected, status.KeyState);
        Assert.Equal(StorageKeyFingerprint.Compute(KeyA), status.ExpectedKeyFingerprint);
        Assert.Equal("awaitingKey", status.MessagesState);
    }

    [Fact]
    public void Status_CorrectKeyArrivesAfterAWrongOne_ReportedAcceptedNotRejected()
    {
        new StorageEncryptionMarker { KeyFingerprint = StorageKeyFingerprint.Compute(KeyA) }.Write(this.layout.Marker);
        var ring = new StorageKeyRing { ExpectedFingerprint = StorageKeyFingerprint.Compute(KeyA) };
        Assert.Equal(StorageKeyAcceptance.Rejected, ring.Provide(KeyB));
        Assert.Equal(StorageKeyAcceptance.Accepted, ring.Provide(KeyA));
        this.messages.State.Returns(StorageState.Open);

        var service = this.Service(ring);

        Assert.Equal(StorageKeyStates.Accepted, service.GetStatus().KeyState);
        Assert.Equal(StorageKeyStates.Accepted, service.GetHealth().KeyState);
        Assert.Equal(StorageKeyFingerprint.Compute(KeyA), service.GetStatus().KeyFingerprint);
    }

    [Fact]
    public void Preview_ListsWhatIsEncryptedErasedAndNotCovered()
    {
        File.WriteAllBytes(this.layout.MessagesDatabase, [.. "SQLite format 3\0"u8.ToArray(), .. new byte[4080]]);
        Directory.CreateDirectory(Path.Combine(this.layout.EvaluationDirectory, "smoke"));
        File.WriteAllText(Path.Combine(this.layout.EvaluationDirectory, "smoke", "x.json"), "{}");
        Directory.CreateDirectory(Path.Combine(this.layout.StateRoot, "subagents"));
        File.WriteAllText(Path.Combine(this.layout.StateRoot, "subagents", "subagents.db"), "x");

        var preview = this.Service(Ring(KeyA)).Preview();

        Assert.Equal("messages", Assert.Single(preview.WillEncrypt).Id);
        Assert.Equal(StorageInventory.EvaluationId, Assert.Single(preview.WillErase).Id);
        Assert.Contains(preview.NotCovered, i => i.Id == "subagents");
        Assert.Equal(4096 + StorageEncryptionService.MigrationMarginBytes, preview.RequiredFreeBytes);
        Assert.Empty(preview.Blockers);
        Assert.Equal(StorageKeyFingerprint.Compute(KeyA), preview.KeyFingerprint);
    }

    [Fact]
    public void Preview_CountsACopyOfEveryDatabaseAndTheContentFiles()
    {
        var header = "SQLite format 3\0"u8.ToArray();
        File.WriteAllBytes(this.layout.MessagesDatabase, [.. header, .. new byte[4080]]);
        Directory.CreateDirectory(Path.GetDirectoryName(this.layout.MemoryDatabasePath)!);
        File.WriteAllBytes(this.layout.MemoryDatabasePath, [.. header, .. new byte[8176]]);
        File.WriteAllBytes(Path.Combine(this.layout.MemoriesDirectory, "a.memory.data"), new byte[1000]);

        var preview = this.Service(Ring(KeyA)).Preview();

        Assert.Equal(4096 + 8192 + 1000 + StorageEncryptionService.MigrationMarginBytes, preview.RequiredFreeBytes);
    }

    [Fact]
    public void Status_AnEncryptionThatFailedVerification_IsListedAsAProblemWithoutPaths()
    {
        using (var plain = new Microsoft.Data.Sqlite.SqliteConnection(EncryptedSqlite.ConnectionString(this.layout.MessagesDatabase, null, Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate, pooling: false)))
        {
            plain.Open();
            using var create = plain.CreateCommand();
            create.CommandText = "CREATE TABLE t (x TEXT); INSERT INTO t VALUES ('a');";
            create.ExecuteNonQuery();
        }

        Assert.ThrowsAny<Exception>(() => EncryptedSqlite.Prepare(this.layout.MessagesDatabase, "0123456789abcdef", s =>
        {
            if (s == "after-export")
            {
                using var stream = new FileStream(this.layout.MessagesDatabase + ".encrypting", FileMode.Open, FileAccess.Write);
                stream.SetLength(10);
            }
        }));

        var status = this.Service(Ring(KeyA)).GetStatus();

        var problem = Assert.Single(status.Problems);
        Assert.Contains("messages.db", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(this.root, problem, StringComparison.Ordinal);
    }
    [Fact]
    public void Status_AStoreCannotBeRead_IsReportedUnavailableSoNoKeyIsMintedFromIt()
    {
        File.WriteAllBytes(this.layout.MessagesDatabase, [.. "SQLite format 3\0"u8.ToArray(), .. new byte[4080]]);
        var inventory = new StorageInventory(this.layout) { BeforeRead = p => { if (p.EndsWith("messages.db", StringComparison.Ordinal)) { throw new UnauthorizedAccessException(); } } };

        var status = this.Service(Ring(KeyA), inventory: inventory).GetStatusOrUnavailable();

        Assert.True(status.Unavailable);
        Assert.False(status.HasEncryptedData);
        Assert.NotNull(status.Error);
    }

    [Fact]
    public void GetStatusOrUnavailable_AnUnexpectedFailure_ReturnsAnErrorFlagInsteadOfThrowing()
    {
        var inventory = new StorageInventory(this.layout) { BeforeRead = _ => throw new InvalidOperationException("boom with /secret/path") };

        var status = this.Service(Ring(KeyA), inventory: inventory).GetStatusOrUnavailable();

        Assert.True(status.Unavailable);
        Assert.DoesNotContain("/secret/path", status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hub_WithoutTheService_ThrowsHubException()
    {
        var hub = (AgentHub)RuntimeHelpers.GetUninitializedObject(typeof(AgentHub));

        Assert.Contains(typeof(IStorageHub), typeof(IAgentHub).GetInterfaces());
        await Assert.ThrowsAsync<HubException>(() => hub.GetStorageEncryptionStatus());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }
}
