using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Contracts.Coding;
using Microsoft.Data.Sqlite;

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

    [Fact]
    public void Upsert_PendingRequest_RoundTripsThroughStorage()
    {
        using var store = new CodingAgentSessionStore(this.tempRoot);
        var id = Guid.NewGuid().ToString();

        var pending = new PendingCodingRequest
        {
            RequestId = "req-99",
            Kind = PendingCodingRequestKind.Permission,
            ToolName = "Bash",
            InputPreview = "git push origin main",
            RequestedAt = DateTimeOffset.UtcNow,
        };

        store.Upsert(MakeRecord(id, "ch-1", "C:\\repo") with
        {
            State = CodingSessionState.AwaitingPermission,
            PendingRequestJson = CodingAgentSessionStore.SerializePendingRequest(pending),
        });

        var restored = CodingAgentSessionStore.DeserializePendingRequest(store.GetById(id)!.PendingRequestJson);

        Assert.NotNull(restored);
        Assert.Equal("req-99", restored!.RequestId);
        Assert.Equal(PendingCodingRequestKind.Permission, restored.Kind);
        Assert.Equal("Bash", restored.ToolName);
    }

    [Fact]
    public void Upsert_NullPendingRequest_ClearsTheStoredPrompt()
    {
        using var store = new CodingAgentSessionStore(this.tempRoot);
        var id = Guid.NewGuid().ToString();

        store.Upsert(MakeRecord(id, "ch-1", "C:\\repo") with
        {
            State = CodingSessionState.AwaitingPermission,
            PendingRequestJson = CodingAgentSessionStore.SerializePendingRequest(new PendingCodingRequest
            {
                RequestId = "req-99",
                Kind = PendingCodingRequestKind.Permission,
                ToolName = "Bash",
                RequestedAt = DateTimeOffset.UtcNow,
            }),
        });

        // Answered: the prompt is gone. A COALESCE-style merge would wrongly keep the stale id.
        store.Upsert(MakeRecord(id, "ch-1", "C:\\repo") with
        {
            State = CodingSessionState.Idle,
            PendingRequestJson = null,
        });

        Assert.Null(store.GetById(id)!.PendingRequestJson);
    }

    [Fact]
    public void Constructor_LegacyDatabaseWithoutPendingRequestColumn_MigratesAndIsIdempotent()
    {
        // Every existing install has a database created before parked prompts were persisted,
        // so the ALTER path — not the CREATE path — is what actually runs in production.
        var dbPath = Path.Combine(this.tempRoot, "external-agent", "sessions.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        using (var legacy = new SqliteConnection($"Data Source={dbPath}"))
        {
            legacy.Open();
            using var cmd = legacy.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE external_agent_sessions (
                    session_id              TEXT PRIMARY KEY,
                    channel_id              TEXT NOT NULL,
                    working_folder          TEXT NOT NULL,
                    policy                  INTEGER NOT NULL,
                    session_name            TEXT,
                    state                   INTEGER NOT NULL,
                    created_at              TEXT NOT NULL,
                    last_activity_at        TEXT NOT NULL,
                    last_user_message       TEXT,
                    last_assistant_summary  TEXT,
                    last_tool_calls         TEXT,
                    ended_at                TEXT
                );
                """;
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        var id = Guid.NewGuid().ToString();
        using (var migrated = new CodingAgentSessionStore(this.tempRoot))
        {
            migrated.Upsert(MakeRecord(id, "ch-1", "C:\\repo") with
            {
                PendingRequestJson = CodingAgentSessionStore.SerializePendingRequest(new PendingCodingRequest
                {
                    RequestId = "req-legacy",
                    Kind = PendingCodingRequestKind.Plan,
                    Plan = "Step 1",
                    RequestedAt = DateTimeOffset.UtcNow,
                }),
            });
        }

        // Re-opening must not attempt the ALTER a second time.
        using var reopened = new CodingAgentSessionStore(this.tempRoot);
        var restored = CodingAgentSessionStore.DeserializePendingRequest(reopened.GetById(id)!.PendingRequestJson);

        Assert.NotNull(restored);
        Assert.Equal("req-legacy", restored!.RequestId);
    }

    [Fact]
    public void Upsert_ReadModifyWrite_PreservesAnExistingPendingRequest()
    {
        // Tools that update one field do `record with { … }` off a fresh read; the parked prompt
        // must survive, otherwise a send would silently strand the session it just unblocked.
        using var store = new CodingAgentSessionStore(this.tempRoot);
        var id = Guid.NewGuid().ToString();

        store.Upsert(MakeRecord(id, "ch-1", "C:\\repo") with
        {
            State = CodingSessionState.AwaitingPermission,
            PendingRequestJson = CodingAgentSessionStore.SerializePendingRequest(new PendingCodingRequest
            {
                RequestId = "req-keep",
                Kind = PendingCodingRequestKind.Permission,
                ToolName = "Bash",
                RequestedAt = DateTimeOffset.UtcNow,
            }),
        });

        var reread = store.GetById(id)!;
        store.Upsert(reread with { LastUserMessage = "another instruction" });

        Assert.Equal(reread.PendingRequestJson, store.GetById(id)!.PendingRequestJson);
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
