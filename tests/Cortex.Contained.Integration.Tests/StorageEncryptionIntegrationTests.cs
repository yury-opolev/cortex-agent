using System.Text;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Security;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Contained.Integration.Tests;

/// <summary>
/// The owner's switch end to end: a plaintext agent is enabled over the hub, "restarts" (a second host on the
/// same volumes), receives the key and encrypts messages and memories, leaving no plaintext marker on disk.
/// </summary>
[Trait("Category", "Integrity")]
public sealed class StorageEncryptionIntegrationTests : IDisposable
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), "cortex-integration-tests", "enc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task EnableThenRestart_EncryptsMessagesAndMemoriesAndLeavesNoPlaintext()
    {
        await using (var plaintext = new StorageHostFactory(this.root))
        {
            await using var hub = await ConnectAsync(plaintext);
            await plaintext.Services.GetRequiredService<MessageStore>().SaveMessageAsync("u", "ch", "user", "zqe2emessage", T0);
            await plaintext.Services.GetRequiredService<IMemoryStore>().StoreMemoryAsync("m1", "zqe2ememory", Chunk("m1"), [Vector()]);

            Assert.False((await hub.InvokeAsync<StorageEncryptionStatusDto>(nameof(IAgentHub.GetStorageEncryptionStatus))).EncryptAtRest);
            var result = await hub.InvokeAsync<StorageEncryptionEnableResultDto>(
                nameof(IAgentHub.EnableStorageEncryption), new StorageEncryptionEnableRequestDto { KeyFingerprint = StorageKeyFingerprint.Compute(Key) });

            Assert.True(result.Accepted);
            Assert.True((await hub.InvokeAsync<StorageEncryptionStatusDto>(nameof(IAgentHub.GetStorageEncryptionStatus))).RestartPending);
        }

        SqliteConnection.ClearAllPools();
        await using (var encrypted = new StorageHostFactory(this.root))
        {
            await using var hub = await ConnectAsync(encrypted);
            var status = await WaitForOpenAsync(hub);

            Assert.Equal("marker", status.Source);
            Assert.Equal("zqe2emessage", Assert.Single(await encrypted.Services.GetRequiredService<MessageStore>().GetMessagesAsync("ch")).Content);
            Assert.Equal("zqe2ememory", (await encrypted.Services.GetRequiredService<IMemoryStore>().GetMemoryAsync("m1"))!.Content);
            Assert.All(status.Items.Where(i => i.Id is "messages" or "memory-db" or "memory-content"), i => Assert.Equal(StorageItemStates.Encrypted, i.State));
        }

        SqliteConnection.ClearAllPools();
        Assert.False(this.AnyStateFileContains("zqe2emessage"));
        Assert.False(this.AnyStateFileContains("zqe2ememory"));
    }

    private static async Task<HubConnection> ConnectAsync(StorageHostFactory factory)
    {
        var hub = factory.CreateHubConnection();
        await hub.StartAsync();
        Assert.True(await hub.InvokeAsync<bool>(nameof(IAgentHub.ProvideStorageKey), new StorageKeyGrant { Key = Key }));
        return hub;
    }

    private static async Task<StorageEncryptionStatusDto> WaitForOpenAsync(HubConnection hub)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = await hub.InvokeAsync<StorageEncryptionStatusDto>(nameof(IAgentHub.GetStorageEncryptionStatus));
            if (status.MessagesState == "open" && status.MemoryState == "open")
            {
                return status;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The encrypted stores did not open.");
    }

    private static List<ChunkRecord> Chunk(string memoryId) =>
    [
        new ChunkRecord { MemoryId = memoryId, ChunkIndex = 0, StartOffset = 0, Length = 11, Title = null, Tags = [], CreatedAt = T0, UpdatedAt = T0 },
    ];

    private static float[] Vector()
    {
        var vector = new float[1024];
        vector[0] = 1f;
        return vector;
    }

    private bool AnyStateFileContains(string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(this.root, "state"), "*", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            if (copy.ToArray().AsSpan().IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class StorageHostFactory(string root) : WebApplicationFactory<AgentHub>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("CORTEX_HUB_TOKEN", AgentHostFactory.TestHubToken);
            builder.UseSetting("CORTEX_DATA_PATH", Path.Combine(root, "data"));
            builder.UseSetting("CORTEX_STATE_PATH", Path.Combine(root, "state"));
            builder.UseSetting("MemoryMcp:DataDirectory", Path.Combine(root, "state", "memory"));
            builder.UseSetting("MemoryMcp:Ollama:Endpoint", "http://127.0.0.1:1");
            Directory.CreateDirectory(Path.Combine(root, "data"));
            Directory.CreateDirectory(Path.Combine(root, "state"));
        }

        public HubConnection CreateHubConnection()
        {
            var server = this.Server;
            return new HubConnectionBuilder()
                .WithUrl(new Uri(server.BaseAddress, "/hub/agent"), options =>
                {
                    options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(AgentHostFactory.TestHubToken);
                })
                .Build();
        }
    }
}
