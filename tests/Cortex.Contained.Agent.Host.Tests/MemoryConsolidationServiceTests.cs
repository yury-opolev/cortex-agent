using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Contracts.Llm;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using static Cortex.Contained.Agent.Host.Memory.MemoryConsolidationService;

namespace Cortex.Contained.Agent.Host.Tests;

public class MemoryConsolidationServiceTests : IDisposable
{
    private readonly ILlmClient _llmClient = Substitute.For<ILlmClient>();
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly MemoryConsolidationService _sut;
    private const string Model = "test-model";

    public MemoryConsolidationServiceTests()
    {
        _sut = new MemoryConsolidationService(
            _llmClient, _memoryService,
            NullLogger<MemoryConsolidationService>.Instance);
    }

    public void Dispose()
    {
        _sut.Dispose();
        GC.SuppressFinalize(this);
    }

    private static ConsolidationFact MakeFact(string content, string? title = null, List<string>? tags = null) =>
        new() { Content = content, Title = title, Tags = tags };

    private void SetupIngest(string returnId = "new-id")
    {
        _memoryService.IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new IngestResult { Success = true, MemoryId = returnId });
    }

    private void SetupSearchEmpty()
    {
        _memoryService.SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float?>(),
            Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
    }

    private void SetupSearchWithResults(params SearchResult[] results)
    {
        _memoryService.SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float?>(),
            Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(results.ToList());
    }

    private void SetupLlmResponse(string json)
    {
        _llmClient.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult { Success = true, Content = json });
    }

    private void SetupLlmFailure()
    {
        _llmClient.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult { Success = false, ErrorMessage = "connection error" });
    }

    // ── ADD (no similar memories) ──────────────────────────────────────

    [Fact]
    public async Task ConsolidateAsync_NoSimilarMemories_AddsNewMemory()
    {
        SetupSearchEmpty();
        SetupIngest("mem-123");

        var result = await _sut.ConsolidateAsync(
            MakeFact("User likes cats", "Pets"), Model, "conv-1", null, CancellationToken.None);

        Assert.Equal(ConsolidationAction.Added, result.Action);
        Assert.Equal("mem-123", result.MemoryId);
        Assert.Equal("User likes cats", result.Content);

        await _memoryService.Received(1).IngestAsync(
            "User likes cats", "Pets", Arg.Any<List<string>?>(),
            true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsolidateAsync_NoSimilarMemories_AddsToBatchMemories()
    {
        SetupSearchEmpty();
        SetupIngest("mem-456");
        var batch = new List<BatchMemoryEntry>();

        await _sut.ConsolidateAsync(
            MakeFact("User lives in Copenhagen"), Model, "conv-1", batch, CancellationToken.None);

        Assert.Single(batch);
        Assert.Equal("mem-456", batch[0].MemoryId);
        Assert.Equal("User lives in Copenhagen", batch[0].Content);
    }

    // ── LLM decides ADD ────────────────────────────────────────────────

    [Fact]
    public async Task ConsolidateAsync_LlmDecidesAdd_AddsNewMemory()
    {
        SetupSearchWithResults(
            new SearchResult { MemoryId = "old-1", Content = "User likes dogs", Score = 0.4f });
        SetupLlmResponse("""{"action":"ADD","reason":"different topic"}""");
        SetupIngest("new-1");

        var result = await _sut.ConsolidateAsync(
            MakeFact("User's birthday is March 3"), Model, "conv-1", null, CancellationToken.None);

        Assert.Equal(ConsolidationAction.Added, result.Action);
        Assert.Equal("new-1", result.MemoryId);
    }

    // ── LLM decides UPDATE ─────────────────────────────────────────────

    [Fact]
    public async Task ConsolidateAsync_LlmDecidesUpdate_UpdatesExistingMemory()
    {
        SetupSearchWithResults(
            new SearchResult { MemoryId = "existing-1", Content = "User likes blue", Score = 0.85f });
        SetupLlmResponse("""{"action":"UPDATE","memoryId":"existing-1","mergedContent":"User likes blue and green","reason":"merged preferences"}""");
        _memoryService.UpdateAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryResult { MemoryId = "existing-1", Content = "User likes blue and green" });

        var result = await _sut.ConsolidateAsync(
            MakeFact("User also likes green"), Model, "conv-1", null, CancellationToken.None);

        Assert.Equal(ConsolidationAction.Updated, result.Action);
        Assert.Equal("existing-1", result.MemoryId);
        Assert.Equal("User likes blue and green", result.Content);

        await _memoryService.Received(1).UpdateAsync(
            "existing-1", "User likes blue and green",
            Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsolidateAsync_LlmDecidesUpdate_UpdatesBatchEntry()
    {
        var batch = new List<BatchMemoryEntry>
        {
            new("batch-1", "Old title", "Old content"),
        };

        SetupSearchWithResults(
            new SearchResult { MemoryId = "batch-1", Content = "Old content", Score = 0.9f });
        SetupLlmResponse("""{"action":"UPDATE","memoryId":"batch-1","mergedContent":"Merged content","reason":"merged"}""");
        _memoryService.UpdateAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryResult { MemoryId = "batch-1", Content = "Merged content" });

        await _sut.ConsolidateAsync(
            MakeFact("New content"), Model, "conv-1", batch, CancellationToken.None);

        // Batch entry should be updated in-place
        Assert.Single(batch);
        Assert.Equal("batch-1", batch[0].MemoryId);
        Assert.Equal("Merged content", batch[0].Content);
    }

    // ── LLM decides NOOP ───────────────────────────────────────────────

    [Fact]
    public async Task ConsolidateAsync_LlmDecidesNoop_NoActionTaken()
    {
        SetupSearchWithResults(
            new SearchResult { MemoryId = "existing-1", Content = "Same info", Score = 0.95f });
        SetupLlmResponse("""{"action":"NOOP","reason":"already covered"}""");

        var result = await _sut.ConsolidateAsync(
            MakeFact("Same info"), Model, "conv-1", null, CancellationToken.None);

        Assert.Equal(ConsolidationAction.Noop, result.Action);
        Assert.Equal("already covered", result.Reason);

        await _memoryService.DidNotReceive().IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ── LLM decides DELETE ─────────────────────────────────────────────

    [Fact]
    public async Task ConsolidateAsync_LlmDecidesDelete_DeletesAndAddsReplacement()
    {
        SetupSearchWithResults(
            new SearchResult { MemoryId = "old-1", Content = "User lives in Paris", Score = 0.88f });
        SetupLlmResponse("""{"action":"DELETE","memoryId":"old-1","reason":"contradicted"}""");
        _memoryService.DeleteAsync("old-1", Arg.Any<CancellationToken>()).Returns(true);
        SetupIngest("replacement-1");

        var result = await _sut.ConsolidateAsync(
            MakeFact("User moved to Copenhagen"), Model, "conv-1", null, CancellationToken.None);

        Assert.Equal(ConsolidationAction.Replaced, result.Action);
        Assert.Equal("replacement-1", result.MemoryId);

        await _memoryService.Received(1).DeleteAsync("old-1", Arg.Any<CancellationToken>());
        await _memoryService.Received(1).IngestAsync(
            "User moved to Copenhagen", Arg.Any<string?>(), Arg.Any<List<string>?>(),
            true, Arg.Any<CancellationToken>());
    }

    // ── LLM failure / malformed response ───────────────────────────────

    [Fact]
    public async Task ConsolidateAsync_LlmFails_FallbacksToAdd()
    {
        SetupSearchWithResults(
            new SearchResult { MemoryId = "old-1", Content = "Something", Score = 0.5f });
        SetupLlmFailure();
        SetupIngest("fallback-1");

        var result = await _sut.ConsolidateAsync(
            MakeFact("New fact"), Model, "conv-1", null, CancellationToken.None);

        Assert.Equal(ConsolidationAction.Added, result.Action);
        Assert.Equal("fallback-1", result.MemoryId);
        Assert.Contains("fallback", result.Reason);
    }

    [Fact]
    public async Task ConsolidateAsync_LlmReturnsNonJson_ReturnsNoop()
    {
        SetupSearchWithResults(
            new SearchResult { MemoryId = "old-1", Content = "Something", Score = 0.5f });
        SetupLlmResponse("I'm sorry, I can't help with that.");

        var result = await _sut.ConsolidateAsync(
            MakeFact("New fact"), Model, "conv-1", null, CancellationToken.None);

        Assert.Equal(ConsolidationAction.Noop, result.Action);
        Assert.Contains("non-JSON", result.Reason);
    }

    [Fact]
    public async Task ConsolidateAsync_LlmReturnsInvalidJson_ReturnsNoop()
    {
        SetupSearchWithResults(
            new SearchResult { MemoryId = "old-1", Content = "Something", Score = 0.5f });
        SetupLlmResponse("{invalid json!}");

        var result = await _sut.ConsolidateAsync(
            MakeFact("New fact"), Model, "conv-1", null, CancellationToken.None);

        Assert.Equal(ConsolidationAction.Noop, result.Action);
        Assert.Contains("JSON parse error", result.Reason);
    }

    [Fact]
    public async Task ConsolidateAsync_LlmReturnsUnknownAction_ReturnsNoop()
    {
        SetupSearchWithResults(
            new SearchResult { MemoryId = "old-1", Content = "Something", Score = 0.5f });
        SetupLlmResponse("""{"action":"MERGE","reason":"custom"}""");

        var result = await _sut.ConsolidateAsync(
            MakeFact("New fact"), Model, "conv-1", null, CancellationToken.None);

        Assert.Equal(ConsolidationAction.Noop, result.Action);
        Assert.Contains("unknown action", result.Reason);
    }

    // ── Markdown code fence stripping ──────────────────────────────────

    [Fact]
    public async Task ConsolidateAsync_LlmWrapsJsonInCodeFence_StillParses()
    {
        SetupSearchWithResults(
            new SearchResult { MemoryId = "old-1", Content = "Something", Score = 0.5f });
        SetupLlmResponse("```json\n{\"action\":\"NOOP\",\"reason\":\"already stored\"}\n```");

        var result = await _sut.ConsolidateAsync(
            MakeFact("New fact"), Model, "conv-1", null, CancellationToken.None);

        Assert.Equal(ConsolidationAction.Noop, result.Action);
        Assert.Equal("already stored", result.Reason);
    }

    // ── Batch memory inclusion ─────────────────────────────────────────

    [Fact]
    public async Task ConsolidateAsync_IncludesBatchMemoriesNotInSearchResults()
    {
        var batch = new List<BatchMemoryEntry>
        {
            new("batch-1", "Batch title", "Batch content"),
        };

        // Search returns different memory; batch memory should still be in LLM context
        SetupSearchWithResults(
            new SearchResult { MemoryId = "search-1", Content = "Search result", Score = 0.4f });
        SetupLlmResponse("""{"action":"ADD","reason":"new topic"}""");
        SetupIngest("new-1");

        await _sut.ConsolidateAsync(
            MakeFact("New topic entirely"), Model, "conv-1", batch, CancellationToken.None);

        // Verify the LLM was called — the prompt should contain both memories
        var request = await _llmClient.Received(1).CompleteAsync(
            Arg.Is<LlmCompletionRequest>(r => r.Model == Model),
            Arg.Any<CancellationToken>());
    }

    // ── Uses correct model ─────────────────────────────────────────────

    [Fact]
    public async Task ConsolidateAsync_PassesModelToLlmRequest()
    {
        SetupSearchWithResults(
            new SearchResult { MemoryId = "old-1", Content = "Stuff", Score = 0.5f });
        SetupLlmResponse("""{"action":"NOOP","reason":"ok"}""");

        await _sut.ConsolidateAsync(
            MakeFact("Fact"), "claude-sonnet-4.6", "conv-1", null, CancellationToken.None);

        await _llmClient.Received(1).CompleteAsync(
            Arg.Is<LlmCompletionRequest>(r => r.Model == "claude-sonnet-4.6"),
            Arg.Any<CancellationToken>());
    }

    // ── IngestAsync called with force:true ──────────────────────────────

    [Fact]
    public async Task ConsolidateAsync_AlwaysPassesForceTrueToIngest()
    {
        SetupSearchEmpty();
        SetupIngest("new-1");

        await _sut.ConsolidateAsync(
            MakeFact("Some fact"), Model, "conv-1", null, CancellationToken.None);

        await _memoryService.Received(1).IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(),
            true, // force must be true
            Arg.Any<CancellationToken>());
    }

    // ── StripToJson helper ─────────────────────────────────────────────

    [Theory]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("  {\"a\":1}  ", "{\"a\":1}")]
    [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("```\n{\"a\":1}\n```", "{\"a\":1}")]
    public void StripToJson_RemovesCodeFencesAndWhitespace(string input, string expected)
    {
        var result = MemoryConsolidationService.StripToJson(input);
        Assert.Equal(expected, result);
    }

    // ── Trailing text after JSON ────────────────────────────────────────

    [Theory]
    [InlineData(
        "{\"action\":\"UPDATE\",\"reason\":\"overlap\"}\n\nThis updates the memory because...",
        "{\"action\":\"UPDATE\",\"reason\":\"overlap\"}")]
    [InlineData(
        "{\"action\":\"ADD\"} some trailing text",
        "{\"action\":\"ADD\"}")]
    [InlineData(
        "[{\"content\":\"fact\"}]\nHere are the facts I extracted.",
        "[{\"content\":\"fact\"}]")]
    [InlineData(
        "{\"nested\":{\"a\":1},\"b\":2} extra",
        "{\"nested\":{\"a\":1},\"b\":2}")]
    [InlineData(
        "{\"text\":\"value with \\\"escaped quotes\\\"\"} trailing",
        "{\"text\":\"value with \\\"escaped quotes\\\"\"}")]
    [InlineData(
        "{\"text\":\"has } brace in string\"} trailing",
        "{\"text\":\"has } brace in string\"}")]
    [InlineData(
        "```json\n{\"a\":1}\n```\nSome commentary",
        "{\"a\":1}")]
    public void StripToJson_StripsTrailingTextAfterJson(string input, string expected)
    {
        var result = MemoryConsolidationService.StripToJson(input);
        Assert.Equal(expected, result);
    }

    // ── Leading text before JSON ─────────────────────────────────────────

    [Theory]
    [InlineData(
        "Here you go: {\"action\":\"UPDATE\",\"reason\":\"overlap\"}",
        "{\"action\":\"UPDATE\",\"reason\":\"overlap\"}")]
    [InlineData(
        "Sure, here is the result:\n{\"action\":\"ADD\",\"reason\":\"new topic\"}",
        "{\"action\":\"ADD\",\"reason\":\"new topic\"}")]
    [InlineData(
        "The extracted facts are: [{\"content\":\"likes Rust\"}]",
        "[{\"content\":\"likes Rust\"}]")]
    [InlineData(
        "Result:\n\n{\"action\":\"NOOP\",\"reason\":\"already covered\"}",
        "{\"action\":\"NOOP\",\"reason\":\"already covered\"}")]
    public void StripToJson_StripsLeadingTextBeforeJson(string input, string expected)
    {
        var result = MemoryConsolidationService.StripToJson(input);
        Assert.Equal(expected, result);
    }

    // ── Both leading and trailing text ───────────────────────────────────

    [Theory]
    [InlineData(
        "Here you go:\n{\"action\":\"UPDATE\",\"memoryId\":\"abc\"}\nThis merges the two memories.",
        "{\"action\":\"UPDATE\",\"memoryId\":\"abc\"}")]
    [InlineData(
        "Sure!\n\n[{\"content\":\"fact one\"},{\"content\":\"fact two\"}]\n\nLet me know if you need anything else.",
        "[{\"content\":\"fact one\"},{\"content\":\"fact two\"}]")]
    [InlineData(
        "Based on the conversation, here is my analysis:\n{\"action\":\"DELETE\",\"memoryId\":\"x\",\"reason\":\"contradicted\"}\nThe old memory is now obsolete.",
        "{\"action\":\"DELETE\",\"memoryId\":\"x\",\"reason\":\"contradicted\"}")]
    public void StripToJson_StripsLeadingAndTrailingText(string input, string expected)
    {
        var result = MemoryConsolidationService.StripToJson(input);
        Assert.Equal(expected, result);
    }

    // ── Edge cases ───────────────────────────────────────────────────────

    [Fact]
    public void StripToJson_PreservesNonJsonInput()
    {
        // No braces/brackets at all — returned as-is for caller to handle
        var prose = "I think the answer is to update the memory.";
        var result = MemoryConsolidationService.StripToJson(prose);
        Assert.Equal(prose, result);
    }

    [Fact]
    public void StripToJson_HandlesEmptyInput()
    {
        Assert.Equal("", MemoryConsolidationService.StripToJson(""));
        Assert.Equal("", MemoryConsolidationService.StripToJson("   "));
    }

    [Fact]
    public void StripToJson_HandlesJsonWithBracesInStringValues()
    {
        // The JSON contains { and } inside string values — should not confuse the parser
        var input = "Here: {\"msg\":\"use {} for objects\",\"arr\":\"and [] for arrays\"} done";
        var expected = "{\"msg\":\"use {} for objects\",\"arr\":\"and [] for arrays\"}";
        Assert.Equal(expected, MemoryConsolidationService.StripToJson(input));
    }

    // ── Explicit message+json / json+message / json+message+json2 ────────

    [Fact]
    public void StripToJson_MessageThenJson()
    {
        var input = "Here is my decision based on the analysis:\n" +
                    "{\"action\":\"UPDATE\",\"memoryId\":\"abc-123\",\"mergedContent\":\"User switched from Python to Rust\",\"reason\":\"language preference changed\"}";
        var expected = "{\"action\":\"UPDATE\",\"memoryId\":\"abc-123\",\"mergedContent\":\"User switched from Python to Rust\",\"reason\":\"language preference changed\"}";
        Assert.Equal(expected, MemoryConsolidationService.StripToJson(input));
    }

    [Fact]
    public void StripToJson_JsonThenMessage()
    {
        var input = "{\"action\":\"UPDATE\",\"memoryId\":\"abc-123\",\"mergedContent\":\"User switched from Python to Rust\",\"reason\":\"language preference changed\"}\n\n" +
                    "I updated the existing memory because the new fact directly contradicts the previous language preference.";
        var expected = "{\"action\":\"UPDATE\",\"memoryId\":\"abc-123\",\"mergedContent\":\"User switched from Python to Rust\",\"reason\":\"language preference changed\"}";
        Assert.Equal(expected, MemoryConsolidationService.StripToJson(input));
    }

    [Fact]
    public void StripToJson_JsonThenMessageThenSecondJson_DiscardsSecondJson()
    {
        // Only the first JSON object should be extracted; the second one is discarded
        var input = "{\"action\":\"UPDATE\",\"memoryId\":\"abc-123\",\"reason\":\"overlap\"}\n\n" +
                    "Here is an alternative if you prefer:\n" +
                    "{\"action\":\"ADD\",\"reason\":\"different approach\"}";
        var expected = "{\"action\":\"UPDATE\",\"memoryId\":\"abc-123\",\"reason\":\"overlap\"}";
        Assert.Equal(expected, MemoryConsolidationService.StripToJson(input));
    }

    [Fact]
    public void StripToJson_ArrayThenMessageThenSecondArray_DiscardsSecondArray()
    {
        var input = "[{\"content\":\"fact one\"}]\n\nAlternatively:\n[{\"content\":\"fact two\"}]";
        var expected = "[{\"content\":\"fact one\"}]";
        Assert.Equal(expected, MemoryConsolidationService.StripToJson(input));
    }

    [Fact]
    public void StripToJson_MessageThenJsonThenMessageThenSecondJson()
    {
        var input = "After careful analysis:\n" +
                    "{\"action\":\"DELETE\",\"memoryId\":\"old-1\",\"reason\":\"contradicted\"}\n" +
                    "But you could also do:\n" +
                    "{\"action\":\"UPDATE\",\"memoryId\":\"old-1\",\"mergedContent\":\"merged\",\"reason\":\"alternative\"}";
        var expected = "{\"action\":\"DELETE\",\"memoryId\":\"old-1\",\"reason\":\"contradicted\"}";
        Assert.Equal(expected, MemoryConsolidationService.StripToJson(input));
    }
}
