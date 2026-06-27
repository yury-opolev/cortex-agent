using System.Text.Json;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class HistoryReadToolTests : IAsyncDisposable
{
    private static readonly ToolExecutionContext _context = new()
    {
        ConversationId = "conv-test",
        ChannelId = "webchat-default",
    };

    private readonly string _tempDir;
    private readonly MessageStore _store;

    public HistoryReadToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"history-read-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new MessageStore(
            Path.Combine(_tempDir, "messages.db"),
            NullLogger<MessageStore>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static string Args(object obj) => JsonSerializer.Serialize(obj);

    [Fact]
    public void ToolName_IsHistoryRead()
    {
        var tool = new HistoryReadTool(_store);
        Assert.Equal("history_read", tool.Name);
    }

    [Fact]
    public async Task ResolvedThroughToolRegistry_ByName()
    {
        var listTool = new HistoryListChannelsTool(_store);
        var readTool = new HistoryReadTool(_store);

        var registry = new ToolRegistry(
            [listTool, readTool],
            new Cortex.Contained.Agent.Host.Agent.ActiveChannelStore(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolRegistry>.Instance);

        Assert.NotNull(registry.GetTool("history_list_channels"));
        Assert.NotNull(registry.GetTool("history_read"));

        var defs = registry.GetDefinitions();
        Assert.Contains(defs, d => d.Name == "history_list_channels");
        Assert.Contains(defs, d => d.Name == "history_read");

        // Sanity: the read tool's schema is well-formed JSON Schema (no compile-time validation).
        using var doc = System.Text.Json.JsonDocument.Parse(readTool.ParametersSchema);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task SeededChannel_ReturnsFormattedTranscript()
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-3);
        var t1 = t0.AddMinutes(1);
        var t2 = t1.AddMinutes(1);

        await _store.SaveMessageAsync("u", "ch", "user", "find my notes", t0);
        await _store.SaveMessageAsync(
            "assistant", "ch", "assistant", "Let me check.", t1,
            toolCalls: """[{"name":"memory_search","args":"\"notes\"","ok":true,"pos":"after"}]""");
        await _store.SaveMessageAsync("u", "ch", "user", "thanks", t2);

        var tool = new HistoryReadTool(_store);
        var result = await tool.ExecuteAsync(Args(new { channelId = "ch" }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("[0] Visitor: find my notes", result.Content);
        Assert.Contains("[1] Consultant: Let me check.", result.Content);
        Assert.Contains("Tools used (1):", result.Content);
        Assert.Contains("- memory_search(\"notes\") ✓", result.Content);
        Assert.Contains("[2] Visitor: thanks", result.Content);
    }

    [Fact]
    public async Task UnknownChannel_ReturnsEmptyStringAndSuccess()
    {
        var tool = new HistoryReadTool(_store);

        var result = await tool.ExecuteAsync(Args(new { channelId = "does-not-exist" }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.Content);
    }

    [Fact]
    public async Task SinceUntilBounds_FilterByTimestamp()
    {
        var t0 = DateTimeOffset.UtcNow.AddHours(-3);
        var t1 = t0.AddHours(1);
        var t2 = t1.AddHours(1);

        await _store.SaveMessageAsync("u", "ch", "user", "first", t0);
        await _store.SaveMessageAsync("u", "ch", "user", "middle", t1);
        await _store.SaveMessageAsync("u", "ch", "user", "last", t2);

        var tool = new HistoryReadTool(_store);

        // Since is inclusive on t1, until is exclusive on t2 — only "middle" qualifies.
        var result = await tool.ExecuteAsync(
            Args(new
            {
                channelId = "ch",
                since = t1.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                until = t2.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            }),
            _context,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("middle", result.Content);
        Assert.DoesNotContain("first", result.Content);
        Assert.DoesNotContain("last", result.Content);
    }

    [Fact]
    public async Task UntilLessThanOrEqualToSince_ReturnsEmpty()
    {
        await _store.SaveMessageAsync("u", "ch", "user", "msg", DateTimeOffset.UtcNow);

        var tool = new HistoryReadTool(_store);
        var t = DateTimeOffset.UtcNow;

        var result = await tool.ExecuteAsync(
            Args(new
            {
                channelId = "ch",
                since = t.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                until = t.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            }),
            _context,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.Content);
    }

    [Fact]
    public async Task Limit_RestrictsRowCount()
    {
        var baseTime = DateTimeOffset.UtcNow.AddHours(-1);
        for (int i = 0; i < 10; i++)
        {
            await _store.SaveMessageAsync("u", "ch", "user", $"m{i}", baseTime.AddMinutes(i));
        }

        var tool = new HistoryReadTool(_store);

        var result = await tool.ExecuteAsync(
            Args(new { channelId = "ch", limit = 3 }),
            _context,
            CancellationToken.None);

        Assert.True(result.Success);

        // Limit of 3 picks the 3 newest, oldest-first: m7, m8, m9.
        Assert.Contains("[0] Visitor: m7", result.Content);
        Assert.Contains("[1] Visitor: m8", result.Content);
        Assert.Contains("[2] Visitor: m9", result.Content);
        Assert.DoesNotContain("m6", result.Content);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    [InlineData(-1)]
    public async Task LimitOutOfRange_ReturnsError(int badLimit)
    {
        var tool = new HistoryReadTool(_store);

        var result = await tool.ExecuteAsync(
            Args(new { channelId = "ch", limit = badLimit }),
            _context,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("limit", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingChannelId_ReturnsError()
    {
        var tool = new HistoryReadTool(_store);

        var result = await tool.ExecuteAsync("{}", _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("channelId", result.Error);
    }

    [Fact]
    public async Task EmptyChannelId_ReturnsError()
    {
        var tool = new HistoryReadTool(_store);

        var result = await tool.ExecuteAsync(Args(new { channelId = "  " }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("channelId", result.Error);
    }

    [Fact]
    public async Task MalformedSince_ReturnsError()
    {
        var tool = new HistoryReadTool(_store);

        var result = await tool.ExecuteAsync(
            Args(new { channelId = "ch", since = "not-a-date" }),
            _context,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("since", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedUntil_ReturnsError()
    {
        var tool = new HistoryReadTool(_store);

        var result = await tool.ExecuteAsync(
            Args(new { channelId = "ch", until = "not-a-date" }),
            _context,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("until", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
