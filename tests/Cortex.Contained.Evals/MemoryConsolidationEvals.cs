using System.Diagnostics;
using Cortex.Contained.Agent.Host.Memory;

namespace Cortex.Contained.Evals;

/// <summary>
/// Eval tests for the memory extraction + consolidation pipeline.
/// These tests use REAL LLM calls and REAL Ollama embeddings to verify
/// that the pipeline correctly extracts facts, deduplicates, merges overlapping
/// memories, and avoids creating duplicates.
///
/// Results (including full LLM transcripts) are written to
/// <c>tests/Cortex.Contained.Evals/eval-results/{timestamp}.json</c> for historical comparison.
///
/// Run with: dotnet test tests/Cortex.Contained.Evals --filter "Category=MemoryConsolidation"
/// </summary>
[Trait("Category", "MemoryConsolidation")]
[Collection("Evals")]
public sealed class MemoryConsolidationEvals
{
    private readonly EvalFixture _fixture;
    private readonly ITestOutputHelper _output;

    public MemoryConsolidationEvals(EvalFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // ── Scenario 1: Basic fact extraction ────────────────────────────────

    [Fact(DisplayName = "Extracts basic user facts from conversation")]
    public async Task ExtractsBasicFacts()
    {
        await RunScenarioAsync("Extracts basic user facts from conversation", async env =>
        {
            var userMsg = "My name is Alex and I live in Berlin. I work as a software engineer at a startup.";
            var assistantMsg = "Nice to meet you, Alex! Berlin is a great city for tech startups. What kind of software do you work on?";

            await RunExtractionAsync(env, userMsg, assistantMsg);

            var memories = await env.GetAllMemoriesAsync();

            _output.WriteLine($"Extracted {memories.Count} memories:");
            foreach (var (id, content) in memories)
            {
                _output.WriteLine($"  [{id[..8]}] {content}");
            }

            // Should extract at least 1 fact (name, location, or occupation)
            Assert.True(memories.Count >= 1, $"Expected at least 1 memory, got {memories.Count}");
            // Should extract at most 4 (name, location, occupation, company type)
            Assert.True(memories.Count <= 4, $"Expected at most 4 memories, got {memories.Count}");

            // At least one memory should mention "Alex" or "Berlin" or "software engineer"
            var allContent = string.Join(" ", memories.Select(m => m.Content));
            Assert.True(
                allContent.Contains("Alex", StringComparison.OrdinalIgnoreCase) ||
                allContent.Contains("Berlin", StringComparison.OrdinalIgnoreCase) ||
                allContent.Contains("software", StringComparison.OrdinalIgnoreCase),
                "Expected at least one memory to mention user details");

            return (memories, new[]
            {
                new EvalExtractionInput { UserMessage = userMsg, AssistantResponse = assistantMsg },
            });
        });
    }

    // ── Scenario 2: Duplicate prevention (same conversation) ─────────────

    [Fact(DisplayName = "Does not create duplicate memories from repeated mentions")]
    public async Task NoDuplicatesFromRepeatedMentions()
    {
        await RunScenarioAsync("Does not create duplicate memories from repeated mentions", async env =>
        {
            var inputs = new List<EvalExtractionInput>();

            // First conversation mentions location
            var u1 = "I live in Copenhagen, Denmark.";
            var a1 = "Copenhagen is beautiful! How long have you lived there?";
            await RunExtractionAsync(env, u1, a1);
            inputs.Add(new EvalExtractionInput { UserMessage = u1, AssistantResponse = a1 });

            var afterFirst = await env.GetAllMemoriesAsync();
            _output.WriteLine($"After first extraction: {afterFirst.Count} memories");
            foreach (var (id, content) in afterFirst)
            {
                _output.WriteLine($"  [{id[..8]}] {content}");
            }

            // Second conversation mentions the same location with more detail
            var u2 = "I've been in Copenhagen for about 3 years now. I live near Nørrebro.";
            var a2 = "Nørrebro is a great neighborhood! Very lively area.";
            await RunExtractionAsync(env, u2, a2);
            inputs.Add(new EvalExtractionInput { UserMessage = u2, AssistantResponse = a2 });

            var afterSecond = await env.GetAllMemoriesAsync();
            _output.WriteLine($"After second extraction: {afterSecond.Count} memories");
            foreach (var (id, content) in afterSecond)
            {
                _output.WriteLine($"  [{id[..8]}] {content}");
            }

            // Should have merged or updated, not duplicated.
            // At most 2 memories about location (one merged, possibly one about duration)
            var locationMemories = afterSecond
                .Where(m => m.Content.Contains("Copenhagen", StringComparison.OrdinalIgnoreCase))
                .ToList();

            _output.WriteLine($"Location-related memories: {locationMemories.Count}");

            Assert.True(locationMemories.Count <= 2,
                $"Expected at most 2 location memories (merged), got {locationMemories.Count}: " +
                string.Join(" | ", locationMemories.Select(m => m.Content)));

            return (afterSecond, inputs.ToArray());
        });
    }

    // ── Scenario 3: Within-batch dedup (C2 strategy) ─────────────────────

    [Fact(DisplayName = "Within-batch facts about same topic are merged, not duplicated")]
    public async Task WithinBatchDedup()
    {
        await RunScenarioAsync("Within-batch facts about same topic are merged, not duplicated", async env =>
        {
            // This message naturally produces multiple overlapping facts
            var userMsg = "I have REMA 1000 very close to my home. Lidl, Føtex, and Netto are also nearby in BIG Herlev Center. I'd love to find their weekly discount campaigns.";
            var assistantMsg = "I can help you track grocery discounts! I'll look into weekly campaigns at REMA 1000, Lidl, Føtex, and Netto near BIG Herlev Center for you.";

            await RunExtractionAsync(env, userMsg, assistantMsg);

            var memories = await env.GetAllMemoriesAsync();

            _output.WriteLine($"Extracted {memories.Count} memories:");
            foreach (var (id, content) in memories)
            {
                _output.WriteLine($"  [{id[..8]}] {content}");
            }

            // The key assertion: we should NOT get 3+ separate memories for:
            // - "interested in grocery discounts"
            // - "nearby stores: REMA, Lidl, Føtex, Netto"
            // - "lives near BIG Herlev Center"
            // Ideally these merge into 1-2 memories.
            Assert.True(memories.Count <= 3,
                $"Expected at most 3 memories (merged related facts), got {memories.Count}: " +
                string.Join(" | ", memories.Select(m => m.Content)));

            return (memories, new[]
            {
                new EvalExtractionInput { UserMessage = userMsg, AssistantResponse = assistantMsg },
            });
        });
    }

    // ── Scenario 4: Update existing memory with new info ─────────────────

    [Fact(DisplayName = "Updates existing memory when new related info arrives")]
    public async Task UpdatesExistingMemory()
    {
        await RunScenarioAsync("Updates existing memory when new related info arrives", async env =>
        {
            // Seed an existing memory
            var seedId = await env.SeedMemoryAsync(
                "User's name is Alex and he lives in Berlin",
                "User identity",
                ["personal"]);

            _output.WriteLine($"Seeded memory: [{seedId[..8]}] User's name is Alex and he lives in Berlin");

            // New conversation adds more detail about the user
            var userMsg = "By the way, I recently moved from Berlin to Munich for a new job at BMW.";
            var assistantMsg = "That's a big move! Munich is a great city. How are you finding it at BMW?";
            await RunExtractionAsync(env, userMsg, assistantMsg);

            var memories = await env.GetAllMemoriesAsync();
            _output.WriteLine($"After extraction: {memories.Count} memories");
            foreach (var (id, content) in memories)
            {
                _output.WriteLine($"  [{id[..8]}] {content}");
            }

            // The original "lives in Berlin" should be updated or replaced, not kept alongside
            var berlinMemories = memories.Where(m =>
                m.Content.Contains("Berlin", StringComparison.OrdinalIgnoreCase) &&
                !m.Content.Contains("Munich", StringComparison.OrdinalIgnoreCase) &&
                !m.Content.Contains("moved", StringComparison.OrdinalIgnoreCase)).ToList();

            Assert.True(berlinMemories.Count == 0,
                $"Expected no stale 'lives in Berlin' memory after move to Munich, found: " +
                string.Join(" | ", berlinMemories.Select(m => m.Content)));

            // Should have mention of Munich somewhere
            var munichMemories = memories.Where(m =>
                m.Content.Contains("Munich", StringComparison.OrdinalIgnoreCase)).ToList();

            Assert.True(munichMemories.Count >= 1,
                "Expected at least one memory mentioning Munich");

            return (memories, new[]
            {
                new EvalExtractionInput { UserMessage = userMsg, AssistantResponse = assistantMsg },
            });
        });
    }

    // ── Scenario 5: No extraction from trivial conversation ──────────────

    [Fact(DisplayName = "Does not extract facts from trivial/greeting conversations")]
    public async Task NoExtractionFromTrivial()
    {
        await RunScenarioAsync("Does not extract facts from trivial/greeting conversations", async env =>
        {
            var userMsg = "Hey, how are you?";
            var assistantMsg = "I'm doing well, thanks for asking! How can I help you today?";
            await RunExtractionAsync(env, userMsg, assistantMsg);

            var memories = await env.GetAllMemoriesAsync();
            _output.WriteLine($"Extracted {memories.Count} memories from trivial conversation");

            Assert.True(memories.Count == 0,
                $"Expected 0 memories from greeting, got {memories.Count}: " +
                string.Join(" | ", memories.Select(m => m.Content)));

            return (memories, new[]
            {
                new EvalExtractionInput { UserMessage = userMsg, AssistantResponse = assistantMsg },
            });
        });
    }

    // ── Scenario 6: Contradicting information replaces old ───────────────

    [Fact(DisplayName = "Contradicting information updates or replaces old memory")]
    public async Task ContradictingInfoReplacesOld()
    {
        await RunScenarioAsync("Contradicting information updates or replaces old memory", async env =>
        {
            // Seed: user prefers Python
            await env.SeedMemoryAsync(
                "User prefers Python as their primary programming language",
                "Language preference",
                ["preferences", "technical"]);

            // New conversation: user now prefers Rust
            var userMsg = "I've completely switched to Rust now. Python was too slow for what I'm building.";
            var assistantMsg = "Rust is excellent for performance-critical applications! The learning curve is steep but worth it.";
            await RunExtractionAsync(env, userMsg, assistantMsg);

            var memories = await env.GetAllMemoriesAsync();
            _output.WriteLine($"After extraction: {memories.Count} memories");
            foreach (var (id, content) in memories)
            {
                _output.WriteLine($"  [{id[..8]}] {content}");
            }

            // Should NOT have a standalone "prefers Python" memory anymore
            var pythonOnlyMemories = memories.Where(m =>
                m.Content.Contains("Python", StringComparison.OrdinalIgnoreCase) &&
                m.Content.Contains("prefer", StringComparison.OrdinalIgnoreCase) &&
                !m.Content.Contains("Rust", StringComparison.OrdinalIgnoreCase) &&
                !m.Content.Contains("switched", StringComparison.OrdinalIgnoreCase)).ToList();

            Assert.True(pythonOnlyMemories.Count == 0,
                $"Expected stale 'prefers Python' memory to be updated/deleted, found: " +
                string.Join(" | ", pythonOnlyMemories.Select(m => m.Content)));

            // Should mention Rust
            var rustMemories = memories.Where(m =>
                m.Content.Contains("Rust", StringComparison.OrdinalIgnoreCase)).ToList();

            Assert.True(rustMemories.Count >= 1,
                "Expected at least one memory mentioning Rust");

            return (memories, new[]
            {
                new EvalExtractionInput { UserMessage = userMsg, AssistantResponse = assistantMsg },
            });
        });
    }

    // ── Infrastructure ───────────────────────────────────────────────────

    /// <summary>
    /// Wraps a scenario in structured recording: captures LLM calls, timing,
    /// final memory state, and pass/fail status into the <see cref="EvalRecorder"/>.
    /// </summary>
    private async Task RunScenarioAsync(
        string scenarioName,
        Func<EvalMemoryEnv, Task<(List<(string MemoryId, string Content)> Memories, EvalExtractionInput[] Inputs)>> scenarioAction)
    {
        using var env = _fixture.CreateMemoryEnv();

        // Clear any LLM calls from previous scenarios
        _fixture.RecordingClient.Clear();

        var sw = Stopwatch.StartNew();
        string? failureMessage = null;
        List<(string MemoryId, string Content)> finalMemories = [];
        EvalExtractionInput[] inputs = [];

        try
        {
            (finalMemories, inputs) = await scenarioAction(env).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failureMessage = ex.Message;
            // Still capture final memory state for diagnosis
            try
            {
                finalMemories = await env.GetAllMemoriesAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best-effort memory capture on failure
            catch
            {
                // Ignore — we're already in a failure path
            }
#pragma warning restore CA1031

            throw; // Re-throw to let xUnit mark the test as failed
        }
        finally
        {
            // Stop the extraction service before recording results / disposing.
            // This closes the channel and drains the consumer loop cleanly, so the
            // BackgroundService doesn't interfere with temp directory cleanup.
            await env.StopExtractionServiceAsync().ConfigureAwait(false);

            sw.Stop();

            var result = new EvalScenarioResult
            {
                Name = scenarioName,
                Passed = failureMessage is null,
                FailureMessage = failureMessage,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                MemoryCount = finalMemories.Count,
                FinalMemories = finalMemories.Select(m => m.Content).ToList(),
                LlmCalls = [.. _fixture.RecordingClient.GetCalls()],
                ExtractionInputs = [.. inputs],
            };

            _fixture.Recorder.RecordScenario(result);

            // Summary output for test runner
            _output.WriteLine($"--- Scenario: {scenarioName} ---");
            _output.WriteLine($"  Duration: {sw.Elapsed.TotalSeconds:F1}s");
            _output.WriteLine($"  LLM calls: {result.LlmCalls.Count}");
            _output.WriteLine($"  Final memories: {finalMemories.Count}");
            if (failureMessage is not null)
            {
                _output.WriteLine($"  FAILED: {failureMessage}");
            }
        }
    }

    /// <summary>
    /// Runs the full extraction pipeline (enqueue + wait for processing)
    /// using the real LLM and embedding services.
    /// <para>
    /// The background service is started lazily and kept alive across multiple
    /// calls within the same scenario (the channel must stay open for subsequent enqueues).
    /// Call <see cref="StopExtractionServiceAsync"/> after all extractions are done.
    /// </para>
    /// </summary>
    private async Task RunExtractionAsync(EvalMemoryEnv env, string userMessage, string assistantResponse)
    {
        // Start the background service if not already running
        if (!env.ExtractionServiceStarted)
        {
            await env.ExtractionService.StartAsync(env.ServiceCts.Token).ConfigureAwait(false);
            env.ExtractionServiceStarted = true;
        }

        // Enqueue the message pair
        var enqueued = env.ExtractionService.EnqueueBatch(
            [
                new Cortex.Contained.Agent.Host.Agent.ExtractionEntry { Role = "user", Content = userMessage, Timestamp = DateTimeOffset.UtcNow },
                new Cortex.Contained.Agent.Host.Agent.ExtractionEntry { Role = "assistant", Content = assistantResponse, Timestamp = DateTimeOffset.UtcNow },
            ], _fixture.Model, "eval-test");
        Assert.True(enqueued, "Failed to enqueue message pair for extraction");

        // Wait for all enqueued items to be fully processed (up to 60s for LLM calls)
        await env.ExtractionService.WaitForIdleAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
    }
}
