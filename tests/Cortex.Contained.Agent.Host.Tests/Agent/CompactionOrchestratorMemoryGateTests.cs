using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Agent;

public sealed class CompactionOrchestratorMemoryGateTests
{
    private static (CompactionOrchestrator Orchestrator, MemoryExtractionService Extraction) Build(MemorySettingsStore store)
    {
        var llm = Substitute.For<ILlmClient>();
        var embeddings = Substitute.For<IEmbeddingService>();
        var memoryService = Substitute.For<IMemoryService>();
        var consolidation = new MemoryConsolidationService(llm, memoryService, NullLogger<MemoryConsolidationService>.Instance);
        var extraction = new MemoryExtractionService(llm, embeddings, consolidation, NullLogger<MemoryExtractionService>.Instance);

        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.MemoryModel.Returns("test-model");

        var imageAging = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAging.CurrentValue.Returns(new ImageAgingConfig());

        var orchestrator = new CompactionOrchestrator(
            llm,
            modelProvider,
            imageAging,
            NullLogger<CompactionOrchestrator>.Instance,
            memoryExtraction: extraction,
            memorySettingsStore: store);

        return (orchestrator, extraction);
    }

    private static AgentSession SessionWithEntries(int count)
    {
        var session = new AgentSession("conv-1");
        for (var i = 0; i < count; i++)
        {
            session.AppendToExtractionBuffer(new ExtractionEntry
            {
                Role = "user",
                Content = $"entry {i}",
                Timestamp = DateTimeOffset.UtcNow,
            });
        }

        return session;
    }

    [Fact]
    public async Task FlushExtractionBuffer_MemoryDisabled_DrainsWithoutEnqueue()
    {
        using var store = new MemorySettingsStore();
        store.Update(null, null, null, memoryEnabled: false);
        var (orchestrator, extraction) = Build(store);
        using (extraction)
        {
            var session = SessionWithEntries(2);

            orchestrator.FlushExtractionBuffer(session, "conv-1");

            Assert.Equal(0, session.ExtractionBufferCount); // drained, not retained
            // Nothing enqueued: the extraction service reports idle immediately.
            await extraction.WaitForIdleAsync(TimeSpan.FromMilliseconds(200));
        }
    }

    [Fact]
    public async Task FlushExtractionBuffer_MemoryEnabled_Enqueues()
    {
        using var store = new MemorySettingsStore(); // enabled by default
        var (orchestrator, extraction) = Build(store);
        using (extraction)
        {
            var session = SessionWithEntries(2);

            orchestrator.FlushExtractionBuffer(session, "conv-1");

            Assert.Equal(0, session.ExtractionBufferCount); // drained into the background queue
            // Enqueued work is pending — the (unstarted) consumer never drains it, so the idle wait times out.
            await Assert.ThrowsAsync<TimeoutException>(
                () => extraction.WaitForIdleAsync(TimeSpan.FromMilliseconds(200)));
        }
    }
}
