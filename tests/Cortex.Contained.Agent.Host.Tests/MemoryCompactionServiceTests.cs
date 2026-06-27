using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Cortex.Contained.Agent.Host.Memory.MemoryConsolidationService;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Tests for <see cref="MemoryCompactionService"/>. Verifies the periodic dedup
/// sweep logic: scheduling, cluster detection, merge execution, and edge cases.
/// <para>
/// These tests exercise the compaction sweep directly via reflection to call
/// <c>RunCompactionAsync</c>, bypassing the <see cref="BackgroundService"/> loop.
/// </para>
/// </summary>
public class MemoryCompactionServiceTests : IDisposable
{
    private readonly ILlmClient _llmClient = Substitute.For<ILlmClient>();
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly MemoryConsolidationService _consolidation;
    private readonly MemoryExtractionService _extraction;
    private readonly IMemoryManagementService _management = Substitute.For<IMemoryManagementService>();
    private readonly ModelProvider _modelProvider = new();
    private readonly MaintenanceStore _maintenanceStore;
    private readonly MemorySettingsStore _memorySettingsStore = new(); // default: memory enabled
    private readonly string _tempDir;
    private readonly MemoryCompactionOptions _options = new()
    {
        Enabled = true,
        SimilarityThreshold = 0.7f,
        IntervalHours = 24,
    };

    public MemoryCompactionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"compaction-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _maintenanceStore = new MaintenanceStore(_tempDir);

        _consolidation = new MemoryConsolidationService(
            _llmClient, _memoryService,
            NullLogger<MemoryConsolidationService>.Instance);

        _extraction = new MemoryExtractionService(
            _llmClient,
            Substitute.For<IEmbeddingService>(),
            _consolidation,
            NullLogger<MemoryExtractionService>.Instance);
    }

    /// <summary>
    /// Sets up safe default returns on the shared <see cref="_memoryService"/> mock so
    /// that the consolidation service's internal <c>SearchAsync</c> and
    /// <c>IngestAsync</c> calls don't return null.
    /// <para>
    /// Uses <c>ReturnsForAnyArgs</c> which acts as a true fallback in NSubstitute —
    /// any subsequent <c>.Returns()</c> with specific argument matchers will reliably
    /// take precedence over these defaults.
    /// </para>
    /// </summary>
    private void SetupDefaultMocks()
    {
        _memoryService.SearchAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(new List<SearchResult>());

        _memoryService.IngestAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(new IngestResult { Success = true, MemoryId = "m-fallback" });

        _memoryService.DeleteAsync(default!, default)
            .ReturnsForAnyArgs(true);

        _memoryService.UpdateAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(new MemoryResult { MemoryId = "m-fallback", Content = "fallback" });
    }

    public void Dispose()
    {
        _extraction.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _maintenanceStore.Dispose();
        _memorySettingsStore.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private MemoryCompactionService CreateService() =>
        new(_management, _memoryService, _consolidation, _extraction,
            _modelProvider, _maintenanceStore, _memorySettingsStore, Options.Create(_options),
            NullLogger<MemoryCompactionService>.Instance);

    // ── Disabled ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_StopsImmediately()
    {
        _options.Enabled = false;
        var service = CreateService();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        // ExecuteAsync should return immediately because Enabled=false
        await service.StartAsync(cts.Token);
        // Give it a moment to process
        await Task.Delay(100);
        await service.StopAsync(CancellationToken.None);

        // Should NOT have listed any memories
        await _management.DidNotReceive().ListAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Preferred time calculation ─────────────────────────────────────

    [Fact]
    public void Options_DefaultValues_AreCorrect()
    {
        var options = new MemoryCompactionOptions();
        Assert.True(options.Enabled);
        Assert.Null(options.PreferredTimeOfDay);
        Assert.Equal(24, options.IntervalHours);
        Assert.Equal(0.7f, options.SimilarityThreshold);
    }

    // ── Uses agent model via IModelProvider ────────────────────────────

    [Fact]
    public void ModelProvider_DefaultModel_IsGpt4o()
    {
        var provider = new ModelProvider();
        Assert.Equal("gpt-4o", provider.DefaultModel);
    }

    [Fact]
    public void ModelProvider_SetDefaultModel_UpdatesModel()
    {
        var provider = new ModelProvider();
        provider.SetDefaultModel("claude-sonnet-4.6");
        Assert.Equal("claude-sonnet-4.6", provider.DefaultModel);
    }

    // ── Compaction sweep ───────────────────────────────────────────────

    [Fact]
    public async Task CompactionSweep_EmptyMemoryStore_CompletesWithoutError()
    {
        _management.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryListResult { Items = [], TotalCount = 0 });

        var service = CreateService();

        // Call RunCompactionAsync via the private method
        var method = typeof(MemoryCompactionService).GetMethod(
            "RunCompactionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        await (Task)method.Invoke(service, [CancellationToken.None])!;

        // Should have queried for memories
        await _management.Received(1).ListAsync(50, 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompactionSweep_NoDuplicates_DoesNotMerge()
    {
        var items = new List<MemoryItem>
        {
            new() { MemoryId = "m-1", Content = "Unique content 1", Tags = [] },
            new() { MemoryId = "m-2", Content = "Unique content 2", Tags = [] },
        };
        _management.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryListResult { Items = items, TotalCount = 2 });

        // No similar memories found for either
        _memoryService.SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float?>(),
            Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var service = CreateService();
        var method = typeof(MemoryCompactionService).GetMethod(
            "RunCompactionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        // Should NOT have called consolidation (no LLM calls)
        await _llmClient.DidNotReceive().CompleteAsync(
            Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompactionSweep_DuplicateCluster_CallsConsolidation()
    {
        // Default mocks first (consolidation's internal SearchAsync needs a safe return)
        SetupDefaultMocks();

        var items = new List<MemoryItem>
        {
            new() { MemoryId = "m-1", Title = "Pets", Content = "User likes cats", Tags = [] },
        };
        _management.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryListResult { Items = items, TotalCount = 1 });

        // Compaction search (threshold 0.7) finds a duplicate
        _memoryService.SearchAsync(
            "User likes cats", Arg.Any<int>(), 0.7f,
            Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { MemoryId = "m-1", Content = "User likes cats", Score = 1.0f },
                new() { MemoryId = "m-2", Content = "User loves cats", Score = 0.95f },
            });

        // Consolidation's internal search (threshold 0.3) for the duplicate's content
        // must find the original memory so the LLM gets called for a decision.
        _memoryService.SearchAsync(
            "User loves cats", Arg.Any<int>(), SimilarityThreshold,
            Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { MemoryId = "m-1", Content = "User likes cats", Score = 0.9f },
            });

        // LLM consolidation says NOOP (already covered)
        _llmClient.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult
            {
                Success = true,
                Content = """{"action":"NOOP","reason":"already covered"}""",
            });

        var service = CreateService();
        var method = typeof(MemoryCompactionService).GetMethod(
            "RunCompactionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        // Should have called the LLM for consolidation decision
        await _llmClient.Received(1).CompleteAsync(
            Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>());

        // Should have deleted the duplicate (NOOP = already covered → compaction deletes it)
        await _memoryService.Received(1).DeleteAsync("m-2", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompactionSweep_UsesModelFromProvider()
    {
        SetupDefaultMocks();
        _modelProvider.SetDefaultModel("claude-sonnet-4.6");

        var items = new List<MemoryItem>
        {
            new() { MemoryId = "m-1", Content = "Content", Tags = [] },
        };
        _management.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryListResult { Items = items, TotalCount = 1 });

        // Compaction search finds a duplicate
        _memoryService.SearchAsync(
            "Content", Arg.Any<int>(), 0.7f,
            Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { MemoryId = "m-1", Content = "Content", Score = 1.0f },
                new() { MemoryId = "m-dup", Content = "Same content", Score = 0.9f },
            });

        // Consolidation's internal search for the duplicate's content must find the
        // original so that the LLM is invoked for the consolidation decision.
        _memoryService.SearchAsync(
            "Same content", Arg.Any<int>(), SimilarityThreshold,
            Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { MemoryId = "m-1", Content = "Content", Score = 0.85f },
            });

        _llmClient.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult
            {
                Success = true,
                Content = """{"action":"NOOP","reason":"covered"}""",
            });

        var service = CreateService();
        var method = typeof(MemoryCompactionService).GetMethod(
            "RunCompactionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        // Verify the LLM was called with the agent's model
        await _llmClient.Received().CompleteAsync(
            Arg.Is<LlmCompletionRequest>(r => r.Model == "claude-sonnet-4.6"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompactionSweep_SkipsAlreadyProcessedMemories()
    {
        // Default mocks for consolidation's internal calls
        SetupDefaultMocks();

        // Two memories: m-1 finds m-2 as duplicate, so m-2 should be skipped
        // when we iterate to it in the compaction loop.
        var items = new List<MemoryItem>
        {
            new() { MemoryId = "m-1", Content = "Content A", Tags = [] },
            new() { MemoryId = "m-2", Content = "Content A duplicate", Tags = [] },
        };
        _management.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryListResult { Items = items, TotalCount = 2 });

        // Compaction search for m-1 (threshold 0.7) finds m-2 as similar
        _memoryService.SearchAsync(
            "Content A", Arg.Any<int>(), 0.7f,
            Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { MemoryId = "m-1", Content = "Content A", Score = 1.0f },
                new() { MemoryId = "m-2", Content = "Content A duplicate", Score = 0.9f },
            });

        // Consolidation's internal search (threshold 0.3) for the duplicate's content
        // must find the original memory so the LLM gets called for a decision.
        _memoryService.SearchAsync(
            "Content A duplicate", Arg.Any<int>(), SimilarityThreshold,
            Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { MemoryId = "m-1", Content = "Content A", Score = 0.85f },
            });

        _llmClient.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult
            {
                Success = true,
                Content = """{"action":"UPDATE","memoryId":"m-1","mergedContent":"Content A merged","reason":"merged"}""",
            });

        var service = CreateService();
        var method = typeof(MemoryCompactionService).GetMethod(
            "RunCompactionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        // The compaction loop should only call the LLM once: for m-2 as a
        // duplicate of m-1. When the loop reaches m-2, it should be in the
        // processedIds set and skipped entirely (no second LLM call).
        await _llmClient.Received(1).CompleteAsync(
            Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>());

        // The compaction search for m-2 (at threshold 0.7) should NOT have been
        // called — m-2 was already processed as a duplicate of m-1.
        await _memoryService.DidNotReceive().SearchAsync(
            "Content A duplicate", Arg.Any<int>(), 0.7f,
            Arg.Any<List<string>?>(), Arg.Any<CancellationToken>());
    }
}
