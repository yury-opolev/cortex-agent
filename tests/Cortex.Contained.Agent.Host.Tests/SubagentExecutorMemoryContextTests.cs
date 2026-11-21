using System.Reflection;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Llm;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Subagent context injection must use the dense-only <see cref="IMemoryService.FindSimilarAsync(string, int, float, CancellationToken)"/>:
/// its 0.3 score floor is a cosine similarity and must keep that meaning when memory_search turns hybrid.
/// </summary>
public class SubagentExecutorMemoryContextTests
{
    [Fact]
    public async Task SearchMemoriesAsync_UsesDenseFindSimilarPerQuery()
    {
        var memoryService = Substitute.For<IMemoryService>();
        memoryService.FindSimilarAsync("user editor preferences", 3, 0.3f, Arg.Any<CancellationToken>())
            .Returns([new SearchResult { MemoryId = "m-1", Content = "Prefers Rider", Score = 0.8f }]);
        memoryService.FindSimilarAsync("deployment workflow", 3, 0.3f, Arg.Any<CancellationToken>())
            .Returns([new SearchResult { MemoryId = "m-2", Content = "Deploys with Self-Update", Score = 0.6f }]);
        var executor = NewExecutor(memoryService);
        var method = typeof(SubagentExecutor).GetMethod("SearchMemoriesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var task = (Task<List<SearchResult>>)method.Invoke(
            executor, [new List<string> { "user editor preferences", "deployment workflow" }, 6, 0.3f, CancellationToken.None])!;
        var results = await task;

        Assert.Equal(["m-1", "m-2"], results.Select(r => r.MemoryId));
        await memoryService.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default, default, default);
    }

    private static SubagentExecutor NewExecutor(IMemoryService memoryService)
    {
        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.DefaultModel.Returns("test-model");

        return new SubagentExecutor(
            new SubagentRunnerRegistry(1, NullLogger<SubagentRunnerRegistry>.Instance),
            Substitute.For<ILlmClient>(),
            modelProvider,
            AppContext.BaseDirectory,
            NullLogger<SubagentExecutor>.Instance,
            memoryService);
    }
}
