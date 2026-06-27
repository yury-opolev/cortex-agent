using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class TodoToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteTodoStore _sqliteStore;
    private readonly InMemoryTodoStore _inMemoryStore;
    private readonly TodoStoreResolver _resolver;
    private readonly ToolExecutionContext _mainContext;
    private readonly ToolExecutionContext _subagentContext;

    public TodoToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "todo-tool-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _sqliteStore = new SqliteTodoStore(_tempDir, NullLogger<SqliteTodoStore>.Instance);
        _inMemoryStore = new InMemoryTodoStore(NullLogger<InMemoryTodoStore>.Instance);
        _resolver = new TodoStoreResolver(_sqliteStore, _inMemoryStore);

        _mainContext = new ToolExecutionContext
        {
            ConversationId = "discord-dm",
            ChannelId = "discord-dm",
        };
        _subagentContext = new ToolExecutionContext
        {
            ConversationId = "subagent-sa-001",
            ChannelId = "subagent",
        };
    }

    public void Dispose()
    {
        _sqliteStore.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    // ── todos_write ──────────────────────────────────────────────────────

    [Fact]
    public async Task Write_MainAgent_PersistsToSqlite()
    {
        var tool = new TodosWriteTool(_resolver, NullLogger<TodosWriteTool>.Instance);

        var result = await tool.ExecuteAsync(
            """{"name":"plan","todos":"- [ ] Step 1\n- [-] Step 2"}""",
            _mainContext, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("0/2 done", result.Content);
        Assert.NotNull(_sqliteStore.Read("discord-dm", "plan"));
    }

    [Fact]
    public async Task Write_Subagent_UsesInMemory()
    {
        var tool = new TodosWriteTool(_resolver, NullLogger<TodosWriteTool>.Instance);

        var result = await tool.ExecuteAsync(
            """{"todos":"- [ ] Step 1\n- [x] Step 2"}""",
            _subagentContext, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(_inMemoryStore.Read("subagent-sa-001", "default"));
        Assert.Null(_sqliteStore.Read("subagent-sa-001", "default")); // not in SQLite
    }

    [Fact]
    public async Task Write_NoName_DefaultsToDefault()
    {
        var tool = new TodosWriteTool(_resolver, NullLogger<TodosWriteTool>.Instance);

        await tool.ExecuteAsync(
            """{"todos":"- [ ] Item"}""",
            _mainContext, CancellationToken.None);

        Assert.NotNull(_sqliteStore.Read("discord-dm", "default"));
    }

    [Fact]
    public async Task Write_EmptyTodos_ReturnsError()
    {
        var tool = new TodosWriteTool(_resolver, NullLogger<TodosWriteTool>.Instance);

        var result = await tool.ExecuteAsync(
            """{"name":"plan","todos":""}""",
            _mainContext, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Write_NoCheckboxes_ReturnsError()
    {
        var tool = new TodosWriteTool(_resolver, NullLogger<TodosWriteTool>.Instance);

        var result = await tool.ExecuteAsync(
            """{"name":"plan","todos":"Just plain text"}""",
            _mainContext, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("checkbox", result.Error!);
    }

    // ── todos_read ───────────────────────────────────────────────────────

    [Fact]
    public async Task Read_ExistingList_ReturnsMarkdown()
    {
        _sqliteStore.Write("discord-dm", "plan", "- [x] Done\n- [ ] Pending");
        var tool = new TodosReadTool(_resolver, NullLogger<TodosReadTool>.Instance);

        var result = await tool.ExecuteAsync(
            """{"name":"plan"}""",
            _mainContext, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("plan", result.Content);
        Assert.Contains("1/2 done", result.Content);
    }

    [Fact]
    public async Task Read_AllLists_ReturnsSummaries()
    {
        _sqliteStore.Write("discord-dm", "plan-a", "- [ ] A");
        _sqliteStore.Write("discord-dm", "plan-b", "- [x] B");
        var tool = new TodosReadTool(_resolver, NullLogger<TodosReadTool>.Instance);

        var result = await tool.ExecuteAsync("""{}""", _mainContext, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("plan-a", result.Content);
        Assert.Contains("plan-b", result.Content);
    }

    [Fact]
    public async Task Read_NotFound_ReturnsError()
    {
        var tool = new TodosReadTool(_resolver, NullLogger<TodosReadTool>.Instance);

        var result = await tool.ExecuteAsync(
            """{"name":"nonexistent"}""",
            _mainContext, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Read_NoLists_ReturnsMessage()
    {
        var tool = new TodosReadTool(_resolver, NullLogger<TodosReadTool>.Instance);

        var result = await tool.ExecuteAsync("""{}""", _mainContext, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("No todo lists", result.Content);
    }

    // ── todos_delete ─────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ExistingList_Removes()
    {
        _sqliteStore.Write("discord-dm", "plan", "- [ ] Item");
        var tool = new TodosDeleteTool(_resolver, NullLogger<TodosDeleteTool>.Instance);

        var result = await tool.ExecuteAsync(
            """{"name":"plan"}""",
            _mainContext, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(_sqliteStore.Read("discord-dm", "plan"));
    }

    [Fact]
    public async Task Delete_NotFound_ReturnsError()
    {
        var tool = new TodosDeleteTool(_resolver, NullLogger<TodosDeleteTool>.Instance);

        var result = await tool.ExecuteAsync(
            """{"name":"nonexistent"}""",
            _mainContext, CancellationToken.None);

        Assert.False(result.Success);
    }

    // ── Store resolver ───────────────────────────────────────────────────

    [Fact]
    public void Resolver_MainAgent_ReturnsSqlite()
    {
        var store = _resolver.Resolve("discord-dm");

        Assert.IsType<SqliteTodoStore>(store);
    }

    [Fact]
    public void Resolver_Subagent_ReturnsInMemory()
    {
        var store = _resolver.Resolve("subagent-sa-001");

        Assert.IsType<InMemoryTodoStore>(store);
    }
}
