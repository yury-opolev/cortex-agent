using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Tests.Coding;

public class CodingAgentSessionStoreTests : IDisposable
{
    private readonly string tempRoot;

    public CodingAgentSessionStoreTests()
    {
        this.tempRoot = Path.Combine(Path.GetTempPath(), $"eas-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.tempRoot, recursive: true);
        }
        catch
        {
            // ignore
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Upsert_NewRecord_PersistsAndReads()
    {
        using var store = new CodingAgentSessionStore(this.tempRoot);
        var id = Guid.NewGuid().ToString();

        store.Upsert(MakeRecord(id, "ch-1", "C:\\repo"));
        var fetched = store.GetById(id);

        Assert.NotNull(fetched);
        Assert.Equal("ch-1", fetched!.ChannelId);
        Assert.Equal("C:\\repo", fetched.WorkingFolder);
    }

    [Fact]
    public void Upsert_ExistingRecord_UpdatesActivity()
    {
        using var store = new CodingAgentSessionStore(this.tempRoot);
        var id = Guid.NewGuid().ToString();
        store.Upsert(MakeRecord(id, "ch-1", "C:\\repo"));

        var later = DateTimeOffset.UtcNow.AddMinutes(5);
        store.Upsert(MakeRecord(id, "ch-1", "C:\\repo") with
        {
            LastActivityAt = later,
            LastUserMessage = "hello",
        });

        var fetched = store.GetById(id);
        Assert.Equal("hello", fetched!.LastUserMessage);
        Assert.True(fetched.LastActivityAt >= later.AddMilliseconds(-1));
    }

    [Fact]
    public void MarkEnded_HidesFromListActive()
    {
        using var store = new CodingAgentSessionStore(this.tempRoot);
        var id = Guid.NewGuid().ToString();
        store.Upsert(MakeRecord(id, "ch-1", "C:\\repo"));

        store.MarkEnded(id);

        Assert.Empty(store.ListActiveByChannel("ch-1"));
        Assert.NotNull(store.GetById(id));
    }

    [Fact]
    public void ListIdleSince_OnlyNonEnded()
    {
        using var store = new CodingAgentSessionStore(this.tempRoot);
        var live = MakeRecord(Guid.NewGuid().ToString(), "ch-1", "C:\\a") with
        {
            LastActivityAt = DateTimeOffset.UtcNow.AddHours(-10),
        };
        var ended = MakeRecord(Guid.NewGuid().ToString(), "ch-2", "C:\\b") with
        {
            LastActivityAt = DateTimeOffset.UtcNow.AddHours(-10),
            EndedAt = DateTimeOffset.UtcNow.AddHours(-5),
        };
        store.Upsert(live);
        store.Upsert(ended);

        var idle = store.ListIdleSince(DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Single(idle);
        Assert.Equal(live.SessionId, idle[0].SessionId);
    }

    [Fact]
    public void SerializeAndDeserializeToolCalls_RoundTrip()
    {
        var calls = new List<CodingToolCall>
        {
            new() { Name = "Read", ArgsSummary = "{path:\"x.cs\"}", Status = "completed", TimestampUtc = DateTimeOffset.UtcNow },
            new() { Name = "Bash", ArgsSummary = "dotnet build", Status = "started", TimestampUtc = DateTimeOffset.UtcNow },
        };

        var json = CodingAgentSessionStore.SerializeToolCalls(calls);
        var restored = CodingAgentSessionStore.DeserializeToolCalls(json);

        Assert.Equal(2, restored.Count);
        Assert.Equal("Read", restored[0].Name);
        Assert.Equal("Bash", restored[1].Name);
    }

    [Fact]
    public void DeserializeToolCalls_NullOrEmpty_Empty()
    {
        Assert.Empty(CodingAgentSessionStore.DeserializeToolCalls(null));
        Assert.Empty(CodingAgentSessionStore.DeserializeToolCalls(""));
        Assert.Empty(CodingAgentSessionStore.DeserializeToolCalls("not json"));
    }

    [Fact]
    public void ListActiveByChannel_ReturnsAllNonEnded_NewestFirst()
    {
        using var store = new CodingAgentSessionStore(this.tempRoot);
        store.Upsert(MakeRecord("s1", "ch-1", "C:\\a") with { LastActivityAt = DateTimeOffset.UtcNow.AddMinutes(-2) });
        store.Upsert(MakeRecord("s2", "ch-1", "C:\\b") with { LastActivityAt = DateTimeOffset.UtcNow });
        store.Upsert(MakeRecord("s3", "ch-2", "C:\\c") with { LastActivityAt = DateTimeOffset.UtcNow });
        store.MarkEnded("s3");

        var active = store.ListActiveByChannel("ch-1");

        Assert.Equal(2, active.Count);
        Assert.Equal("s2", active[0].SessionId); // newest first
        Assert.Equal("s1", active[1].SessionId);
    }

    private static CodingAgentSessionRecord MakeRecord(string id, string channel, string folder)
    {
        var now = DateTimeOffset.UtcNow;
        return new CodingAgentSessionRecord
        {
            SessionId = id,
            ChannelId = channel,
            WorkingFolder = folder,
            Policy = CodingPolicy.Prompt,
            State = CodingSessionState.Idle,
            CreatedAt = now,
            LastActivityAt = now,
        };
    }
}
