using Cortex.Contained.Agent.Host.Agent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class SqliteTodoStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteTodoStore _store;

    public SqliteTodoStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "todo-store-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new SqliteTodoStore(_tempDir, NullLogger<SqliteTodoStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Write_Read_RoundTrips()
    {
        _store.Write("conv-1", "plan", "- [ ] Step 1\n- [x] Step 2");

        var list = _store.Read("conv-1", "plan");

        Assert.NotNull(list);
        Assert.Equal("plan", list.Name);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(TodoStatus.Pending, list.Items[0].Status);
        Assert.Equal(TodoStatus.Completed, list.Items[1].Status);
    }

    [Fact]
    public void Write_Updates_ExistingList()
    {
        _store.Write("conv-1", "plan", "- [ ] Step 1");
        _store.Write("conv-1", "plan", "- [x] Step 1\n- [ ] Step 2");

        var list = _store.Read("conv-1", "plan");

        Assert.NotNull(list);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(TodoStatus.Completed, list.Items[0].Status);
    }

    [Fact]
    public void Read_NotFound_ReturnsNull()
    {
        Assert.Null(_store.Read("conv-1", "nonexistent"));
    }

    [Fact]
    public void ReadAll_MultipleListsAndConversations()
    {
        _store.Write("conv-1", "plan-a", "- [ ] A1");
        _store.Write("conv-1", "plan-b", "- [ ] B1");
        _store.Write("conv-2", "plan-c", "- [ ] C1");

        var conv1Lists = _store.ReadAll("conv-1");
        var conv2Lists = _store.ReadAll("conv-2");

        Assert.Equal(2, conv1Lists.Count);
        Assert.Single(conv2Lists);
    }

    [Fact]
    public void Delete_RemovesList()
    {
        _store.Write("conv-1", "plan", "- [ ] Step 1");

        var deleted = _store.Delete("conv-1", "plan");

        Assert.True(deleted);
        Assert.Null(_store.Read("conv-1", "plan"));
    }

    [Fact]
    public void Delete_NotFound_ReturnsFalse()
    {
        Assert.False(_store.Delete("conv-1", "nonexistent"));
    }

    [Fact]
    public void GetSummaries_ReturnsCorrectCounts()
    {
        _store.Write("conv-1", "migration", "- [x] Research\n- [-] Build\n- [ ] Test");

        var summaries = _store.GetSummaries("conv-1");

        Assert.Single(summaries);
        Assert.Equal("migration", summaries[0].Name);
        Assert.Equal(3, summaries[0].TotalCount);
        Assert.Equal(1, summaries[0].DoneCount);
    }

    [Fact]
    public void Write_ExceedsMaxItems_Rejected()
    {
        var markdown = string.Join("\n", Enumerable.Range(1, 25).Select(i => $"- [ ] Item {i}"));

        _store.Write("conv-1", "big", markdown);

        Assert.Null(_store.Read("conv-1", "big"));
    }

    [Fact]
    public void Write_ExceedsMaxLists_AutoRemovesCompleted()
    {
        // Fill up to max
        for (var i = 0; i < SqliteTodoStore.MaxListsPerConversation; i++)
        {
            _store.Write("conv-1", $"list-{i}", "- [ ] Item");
        }

        // Mark one as completed
        _store.Write("conv-1", "list-0", "- [x] Item");

        // New list should succeed (completed one auto-removed)
        _store.Write("conv-1", "new-list", "- [ ] New item");

        Assert.NotNull(_store.Read("conv-1", "new-list"));
        Assert.Null(_store.Read("conv-1", "list-0")); // auto-removed
    }

    [Fact]
    public void Cleanup_RemovesOldCompletedLists()
    {
        _store.Write("conv-1", "done", "- [x] All done\n- [~] Skipped");
        _store.Write("conv-1", "active", "- [ ] Still pending");

        // Manually backdate the "done" list
        SetUpdatedAt("conv-1", "done", DateTimeOffset.UtcNow - TimeSpan.FromHours(25));

        var purged = _store.Cleanup();

        Assert.Equal(1, purged);
        Assert.Null(_store.Read("conv-1", "done"));
        Assert.NotNull(_store.Read("conv-1", "active"));
    }

    [Fact]
    public void Cleanup_PreservesRecentCompletedLists()
    {
        _store.Write("conv-1", "done", "- [x] All done");

        var purged = _store.Cleanup();

        Assert.Equal(0, purged);
        Assert.NotNull(_store.Read("conv-1", "done"));
    }

    private void SetUpdatedAt(string conversationId, string name, DateTimeOffset updatedAt)
    {
        var dbPath = Path.Combine(_tempDir, "todos", "todos.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE todo_lists SET updated_at = $updatedAt WHERE name = $name AND conversation_id = $conversationId";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$conversationId", conversationId);
        cmd.Parameters.AddWithValue("$updatedAt", updatedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }
}

public class InMemoryTodoStoreTests
{
    private readonly InMemoryTodoStore _store = new(NullLogger<InMemoryTodoStore>.Instance);

    [Fact]
    public void Write_Read_RoundTrips()
    {
        _store.Write("sa-001", "steps", "- [ ] Step 1\n- [x] Step 2");

        var list = _store.Read("sa-001", "steps");

        Assert.NotNull(list);
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void Write_SecondList_ReplacesFirst()
    {
        _store.Write("sa-001", "first", "- [ ] Item");
        _store.Write("sa-001", "second", "- [ ] Item");

        Assert.Null(_store.Read("sa-001", "first")); // replaced
        Assert.NotNull(_store.Read("sa-001", "second"));
    }

    [Fact]
    public void Write_SameNameUpdate_Keeps()
    {
        _store.Write("sa-001", "steps", "- [ ] Step 1");
        _store.Write("sa-001", "steps", "- [x] Step 1\n- [ ] Step 2");

        var list = _store.Read("sa-001", "steps");

        Assert.NotNull(list);
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void Delete_RemovesList()
    {
        _store.Write("sa-001", "steps", "- [ ] Item");

        Assert.True(_store.Delete("sa-001", "steps"));
        Assert.Null(_store.Read("sa-001", "steps"));
    }

    [Fact]
    public void Clear_RemovesAllForConversation()
    {
        _store.Write("sa-001", "steps", "- [ ] Item");
        _store.Write("sa-002", "steps", "- [ ] Other");

        _store.Clear("sa-001");

        Assert.Empty(_store.ReadAll("sa-001"));
        Assert.Single(_store.ReadAll("sa-002")); // untouched
    }

    [Fact]
    public void GetSummaries_Works()
    {
        _store.Write("sa-001", "steps", "- [x] Done\n- [ ] Pending");

        var summaries = _store.GetSummaries("sa-001");

        Assert.Single(summaries);
        Assert.Equal(2, summaries[0].TotalCount);
        Assert.Equal(1, summaries[0].DoneCount);
    }

    [Fact]
    public void IsolatedBetweenConversations()
    {
        _store.Write("sa-001", "steps", "- [ ] A");
        _store.Write("sa-002", "steps", "- [ ] B");

        Assert.Single(_store.ReadAll("sa-001"));
        Assert.Single(_store.ReadAll("sa-002"));
        Assert.Equal("A", _store.Read("sa-001", "steps")!.Items[0].Description);
        Assert.Equal("B", _store.Read("sa-002", "steps")!.Items[0].Description);
    }
}
