using System.Diagnostics;
using System.Text.Json;
using Cortex.Contained.ScenarioEvals.Abstractions;
using Cortex.Contained.ScenarioEvals.Model;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.ScenarioEvals.Orchestration;

/// <summary>
/// Drives a single scenario end-to-end: phases → segments → exchanges → lifecycle → scoring.
/// Records results to the store and writes a human-readable transcript file.
/// </summary>
public sealed class ScenarioOrchestrator
{
    private readonly IBridgeApiClient _bridgeClient;
    private readonly IActorService _actorService;
    private readonly IScorer _scorer;
    private readonly IResultStore _resultStore;
    private readonly long _runId;
    private readonly string _transcriptDir;
    private readonly ILogger<ScenarioOrchestrator> _logger;

    public ScenarioOrchestrator(
        IBridgeApiClient bridgeClient,
        IActorService actorService,
        IScorer scorer,
        IResultStore resultStore,
        long runId,
        string transcriptDir,
        ILogger<ScenarioOrchestrator> logger)
    {
        _bridgeClient = bridgeClient;
        _actorService = actorService;
        _scorer = scorer;
        _resultStore = resultStore;
        _runId = runId;
        _transcriptDir = transcriptDir;
        _logger = logger;
    }

    public async Task<ScenarioResult> RunScenarioAsync(ScenarioDefinition scenario, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();
        var transcript = new List<TranscriptEntry>();
        var allPhaseResults = new List<PhaseResult>();
        var allTokenUsage = new List<TokenUsageSummary>();
        var totalExchanges = 0;

        _logger.LogInformation("Starting scenario: {ScenarioId}", scenario.Id);

        // 1. Clean slate
        await _bridgeClient.ResetAllAsync(ct);
        transcript.Add(new TranscriptEntry { Type = "lifecycle", Event = "reset_all" });

        // 2. Run each phase
        foreach (var phase in scenario.Phases)
        {
            var phaseResult = await RunPhaseAsync(scenario, phase, transcript, allTokenUsage, ct);
            allPhaseResults.Add(phaseResult);
            totalExchanges += phaseResult.Exchanges.Count;
        }

        totalSw.Stop();

        // 3. Get final memory count
        var finalMemories = await _bridgeClient.ListMemoriesAsync(ct: ct);

        var scenarioResult = new ScenarioResult
        {
            ScenarioId = scenario.Id,
            Passed = true, // Caller determines pass/fail based on thresholds
            DurationMs = totalSw.ElapsedMilliseconds,
            TotalExchanges = totalExchanges,
            FinalMemoryCount = finalMemories.TotalCount,
            Phases = allPhaseResults,
            TokenUsage = allTokenUsage
        };

        // 4. Persist to SQLite
        var scenarioResultId = await _resultStore.RecordScenarioAsync(_runId, scenarioResult);

        foreach (var phase in allPhaseResults)
        {
            var phaseResultId = await _resultStore.RecordPhaseAsync(scenarioResultId, phase);
            if (phase.Scores.Count > 0)
                await _resultStore.RecordScoresAsync(phaseResultId, phase.Scores);
        }

        foreach (var usage in allTokenUsage)
            await _resultStore.RecordTokenUsageAsync(_runId, usage);

        // 5. Write transcript
        await WriteTranscriptAsync(scenario.Id, transcript, ct);

        _logger.LogInformation("Scenario {ScenarioId} completed in {Duration}ms with {Exchanges} exchanges, {Memories} memories",
            scenario.Id, totalSw.ElapsedMilliseconds, totalExchanges, finalMemories.TotalCount);

        return scenarioResult;
    }

    private async Task<PhaseResult> RunPhaseAsync(
        ScenarioDefinition scenario,
        PhaseDefinition phase,
        List<TranscriptEntry> transcript,
        List<TokenUsageSummary> tokenUsage,
        CancellationToken ct)
    {
        var phaseSw = Stopwatch.StartNew();
        var phaseExchanges = new List<Exchange>();
        var phaseScores = new List<ScoreResult>();

        _logger.LogInformation("  Phase: {PhaseName}", phase.Name);
        transcript.Add(new TranscriptEntry { Type = "phase_start", Phase = phase.Name });

        // Run each segment
        foreach (var segment in phase.Segments)
        {
            if (segment.Type == "pause")
            {
                _logger.LogInformation("    Segment: pause (triggering compact)");
                transcript.Add(new TranscriptEntry { Type = "lifecycle", Phase = phase.Name, Event = "pause_compact" });
                await _bridgeClient.CompactAsync(_bridgeClient.ChannelId, ct);
                continue;
            }

            _logger.LogInformation("    Segment: {Topic} ({Exchanges} exchanges)", segment.Topic, segment.Exchanges);

            var segmentExchanges = new List<Exchange>();

            for (var i = 0; i < segment.Exchanges; i++)
            {
                // Actor generates user message
                var (userMessage, actorTokens) = await _actorService.GenerateMessageAsync(
                    scenario.Persona, scenario.Facts, segment, segmentExchanges, ct);

                transcript.Add(new TranscriptEntry
                {
                    Type = "exchange",
                    Phase = phase.Name,
                    Segment = segment.Topic,
                    UserMessage = userMessage
                });

                // Send to agent via API
                var (agentResponse, agentTokens) = await _bridgeClient.SendMessageAsync(userMessage, ct);

                var exchange = new Exchange
                {
                    User = userMessage,
                    Agent = agentResponse,
                    AgentTokens = agentTokens
                };

                segmentExchanges.Add(exchange);

                transcript.Add(new TranscriptEntry
                {
                    Type = "exchange",
                    Phase = phase.Name,
                    Segment = segment.Topic,
                    AgentResponse = agentResponse
                });

                // Track actor token usage
                tokenUsage.Add(new TokenUsageSummary
                {
                    ScenarioId = scenario.Id,
                    PhaseName = phase.Name,
                    Role = "actor",
                    TokensIn = actorTokens.PromptTokens,
                    TokensOut = actorTokens.CompletionTokens,
                    TokensTotal = actorTokens.TotalTokens
                });

                if (agentTokens is not null)
                {
                    tokenUsage.Add(new TokenUsageSummary
                    {
                        ScenarioId = scenario.Id,
                        PhaseName = phase.Name,
                        Role = "agent",
                        TokensIn = agentTokens.PromptTokens,
                        TokensOut = agentTokens.CompletionTokens,
                        TokensTotal = agentTokens.TotalTokens
                    });
                }
            }

            phaseExchanges.AddRange(segmentExchanges);

            // Score the segment if it has scoring criteria
            if (segment.Scoring is not null)
            {
                var memories = await _bridgeClient.ListMemoriesAsync(ct: ct);
                var scores = await _scorer.ScoreSegmentAsync(segment, segmentExchanges, memories, scenario.Facts, ct);
                phaseScores.AddRange(scores);

                transcript.Add(new TranscriptEntry
                {
                    Type = "scoring",
                    Phase = phase.Name,
                    Segment = segment.Topic,
                    Scores = scores
                });
            }
        }

        // Execute lifecycle events
        foreach (var afterEvent in phase.After)
        {
            _logger.LogInformation("    Lifecycle: {Event}", afterEvent);
            transcript.Add(new TranscriptEntry { Type = "lifecycle", Phase = phase.Name, Event = afterEvent });

            switch (afterEvent)
            {
                case "compact":
                    await _bridgeClient.CompactAsync(_bridgeClient.ChannelId, ct);
                    break;
                case "compact-memories":
                    await _bridgeClient.CompactMemoriesAsync(ct);
                    break;
                case "reset-session":
                    await _bridgeClient.ResetSessionAsync(_bridgeClient.ChannelId, ct);
                    break;
            }
        }

        // Memory snapshot after phase
        var memoriesAfter = await _bridgeClient.ListMemoriesAsync(ct: ct);
        var memorySnapshots = memoriesAfter.Items.Select(m => new MemorySnapshot
        {
            MemoryId = m.MemoryId,
            Title = m.Title,
            Content = m.Content,
            Tags = [.. m.Tags]
        }).ToList();

        transcript.Add(new TranscriptEntry
        {
            Type = "memory_snapshot",
            Phase = phase.Name,
            Memories = memorySnapshots
        });

        phaseSw.Stop();

        return new PhaseResult
        {
            PhaseName = phase.Name,
            DurationMs = phaseSw.ElapsedMilliseconds,
            Exchanges = phaseExchanges,
            MemoriesAfter = memorySnapshots,
            Scores = phaseScores
        };
    }

    private async Task WriteTranscriptAsync(string scenarioId, List<TranscriptEntry> transcript, CancellationToken ct)
    {
        Directory.CreateDirectory(_transcriptDir);
        var path = Path.Combine(_transcriptDir, $"{_runId}-{scenarioId}.transcript.json");

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(transcript, options);
        await File.WriteAllTextAsync(path, json, ct);

        _logger.LogInformation("Transcript written to {Path}", path);
    }
}
