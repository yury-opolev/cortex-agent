using System.Text;
using System.Text.Json;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Cortex.Contained.ScenarioEvals.Abstractions;
using Cortex.Contained.ScenarioEvals.Model;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.ScenarioEvals.Scoring;

/// <summary>
/// LLM-based judge: evaluates naturalness and hallucination using a separate LLM call.
/// Returns structured JSON with scores and reasoning.
/// </summary>
public sealed class JudgeScorer : IScorer
{
    private readonly ILlmClient _llmClient;
    private readonly string _model;
    private readonly ILogger _logger;

    public JudgeScorer(ILlmClient llmClient, string model, ILogger logger)
    {
        _llmClient = llmClient;
        _model = model;
        _logger = logger;
    }

    public async Task<List<ScoreResult>> ScoreSegmentAsync(
        SegmentDefinition segment,
        IReadOnlyList<Exchange> exchanges,
        MemoryListResult memories,
        Dictionary<string, string[]> facts,
        CancellationToken ct)
    {
        var results = new List<ScoreResult>();
        var scoring = segment.Scoring;
        if (scoring is null)
            return results;

        if (!scoring.NoHallucination && !scoring.Naturalness && !scoring.TaskCompleted)
            return results;

        var prompt = BuildJudgePrompt(segment, exchanges, facts, scoring);

        var request = new LlmCompletionRequest
        {
            Model = _model,
            Messages =
            [
                new LlmMessage
                {
                    Role = "system",
                    Content = "You are an evaluation judge. Analyze the conversation and return a JSON object with your assessment. Return ONLY valid JSON, no markdown fences."
                },
                new LlmMessage { Role = "user", Content = prompt }
            ],
            Temperature = 0.1,
            MaxTokens = 1024,
            RequestId = $"judge-{Guid.NewGuid():N}",
            ConversationId = "scenario-eval-judge"
        };

        var result = await _llmClient.CompleteAsync(request, ct);

        if (!result.Success)
        {
            _logger.LogWarning("Judge LLM call failed: {Error}", result.ErrorMessage);
            return results;
        }

        var json = StripToJson(result.Content ?? "");
        try
        {
            var judgment = JsonSerializer.Deserialize<JudgmentResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (judgment is null)
                return results;

            if (scoring.NoHallucination)
            {
                results.Add(new ScoreResult
                {
                    Dimension = "no_hallucination",
                    Label = scoring.Label ?? "hallucination_check",
                    Value = judgment.NoHallucination ? 1.0 : 0.0,
                    Details = judgment.HallucinationReasoning
                });
            }

            if (scoring.Naturalness)
            {
                results.Add(new ScoreResult
                {
                    Dimension = "naturalness",
                    Label = scoring.Label ?? "naturalness",
                    Value = judgment.Naturalness,
                    Details = judgment.NaturalnessReasoning
                });
            }

            if (scoring.TaskCompleted)
            {
                results.Add(new ScoreResult
                {
                    Dimension = "task_completed",
                    Label = scoring.Label ?? "task_completion",
                    Value = judgment.TaskCompleted ? 1.0 : 0.0,
                    Details = judgment.TaskReasoning
                });
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Failed to parse judge response: {Error}. Raw: {Raw}", ex.Message, json);
        }

        return results;
    }

    private static string BuildJudgePrompt(
        SegmentDefinition segment,
        IReadOnlyList<Exchange> exchanges,
        Dictionary<string, string[]> facts,
        ScoringCriteria scoring)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Ground Truth Facts");
        foreach (var (category, items) in facts)
        {
            sb.AppendLine($"**{category}**:");
            foreach (var item in items)
                sb.AppendLine($"  - {item}");
        }

        sb.AppendLine();
        sb.AppendLine($"## Segment Topic: {segment.Topic}");
        sb.AppendLine();
        sb.AppendLine("## Conversation Transcript");
        foreach (var exchange in exchanges)
        {
            sb.AppendLine($"**User**: {exchange.User}");
            sb.AppendLine($"**Agent**: {exchange.Agent}");
            sb.AppendLine();
        }

        sb.AppendLine("## Evaluation Instructions");
        sb.AppendLine("Return a JSON object with the following fields:");

        if (scoring.NoHallucination)
            sb.AppendLine("- \"noHallucination\": boolean — true if the agent did NOT state anything contradicting the ground truth facts");

        if (scoring.Naturalness)
            sb.AppendLine("- \"naturalness\": number 1-5 — how natural and helpful the agent's responses were (1=robotic/unhelpful, 5=perfectly natural)");

        if (scoring.TaskCompleted)
            sb.AppendLine("- \"taskCompleted\": boolean — true if the agent successfully completed what was asked");

        sb.AppendLine("- Include reasoning fields (\"hallucinationReasoning\", \"naturalnessReasoning\", \"taskReasoning\") explaining each judgment");

        return sb.ToString();
    }

    /// <summary>Strip markdown code fences from LLM output to get raw JSON.</summary>
    private static string StripToJson(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
                trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```", StringComparison.Ordinal))
                trimmed = trimmed[..^3];
        }
        return trimmed.Trim();
    }

    private sealed class JudgmentResult
    {
        public bool NoHallucination { get; init; }
        public string? HallucinationReasoning { get; init; }
        public double Naturalness { get; init; }
        public string? NaturalnessReasoning { get; init; }
        public bool TaskCompleted { get; init; }
        public string? TaskReasoning { get; init; }
    }
}
