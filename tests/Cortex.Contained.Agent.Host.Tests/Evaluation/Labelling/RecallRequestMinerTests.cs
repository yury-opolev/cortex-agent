using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed class RecallRequestMinerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 4, 2, 10, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"miner-{Guid.NewGuid():N}");
    private readonly string db;

    public RecallRequestMinerTests()
    {
        Directory.CreateDirectory(this.root);
        this.db = Path.Combine(this.root, "messages.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(this.root, recursive: true);
    }

    private static Task<string> CallAsync(MessageStore store, string tool, string channel, DateTimeOffset ts, string? args, bool redacted = false)
    {
        var payload = new JsonObject { ["tool"] = tool, ["toolCallId"] = Guid.NewGuid().ToString("N") };
        if (args is not null)
        {
            payload["args"] = args;
        }

        if (redacted)
        {
            payload["redaction"] = "unavailable";
        }

        return store.AppendAsync(new EventDraft(EventTypes.ToolCall, AuthorKinds.Main) { ChannelId = channel, Ts = ts, Payload = payload }, CancellationToken.None);
    }

    [Fact]
    public async Task Read_PairsEachSearchWithTheOwnersPrecedingMessageOncePerMessage()
    {
        long asked;
        await using (var store = new MessageStore(this.db, NullLogger<MessageStore>.Instance))
        {
            await store.SaveMessageAsync("u", "ch", "user", "an older question", T0.AddMinutes(-30));
            asked = await store.SaveMessageAsync("u", "ch", "user", "when did we book the ferry", T0);
            await store.SaveMessageAsync("u", "ch", "user", "scheduled instruction", T0.AddMinutes(2), category: MessageCategory.Internal);
            await store.SaveMessageAsync("u", "other", "user", "unrelated channel", T0.AddMinutes(1));
            await CallAsync(store, "history_search", "ch", T0.AddSeconds(5), """{"query":"ferry booking","channel":"ch"}""");
            await CallAsync(store, "history_search", "ch", T0.AddSeconds(9), """{"query":"ferry"}""");
            await CallAsync(store, "memory_search", "ch", T0.AddSeconds(7), """{"query":"ferry"}""");
            await CallAsync(store, "history_search", "ch", T0.AddMinutes(3), "{\"query\":\"cut off", redacted: false);
            await store.SaveMessageAsync("u", "ch", "user", "a later question searched without recorded arguments", T0.AddMinutes(3.5));
            await CallAsync(store, "history_search", "ch", T0.AddMinutes(4), """{"query":"secret"}""", redacted: true);
        }

        var seeds = RecallRequestMiner.Read(this.db, null, "history_search");

        var seed = Assert.Single(seeds);
        Assert.Equal(asked.ToString(System.Globalization.CultureInfo.InvariantCulture), seed.RequestMessageId);
        Assert.Equal(("real", "real"), (seed.Category, seed.Stratum));
        Assert.Equal(T0, seed.RequestTime);
        Assert.Equal("""{"query":"ferry booking","channel":"ch"}""", seed.ModelArgs);
        Assert.Empty(seed.SourceIds);
        Assert.Single(RecallRequestMiner.Read(this.db, null, "memory_search"));
        Assert.Empty(RecallRequestMiner.Read(Path.Combine(this.root, "missing.db"), null, "history_search"));
    }
}
