using System.Text;
using Cortex.Contained.Contracts.Llm;
using Cortex.Contained.ScenarioEvals.Abstractions;
using Cortex.Contained.ScenarioEvals.Model;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.ScenarioEvals.Actor;

/// <summary>
/// Generates in-character messages for synthetic personas using a separate LLM instance.
/// Adapts prompting strategy based on segment type (conversation, task, schedule, pause).
/// </summary>
public sealed class ActorService : IActorService
{
    private readonly ILlmClient _llmClient;
    private readonly string _model;
    private readonly ILogger<ActorService> _logger;

    public ActorService(ILlmClient llmClient, string model, ILogger<ActorService> logger)
    {
        _llmClient = llmClient;
        _model = model;
        _logger = logger;
    }

    public async Task<(string Message, TokenUsageInfo Tokens)> GenerateMessageAsync(
        PersonaDefinition persona,
        Dictionary<string, string[]> facts,
        SegmentDefinition segment,
        IReadOnlyList<Exchange> conversationHistory,
        CancellationToken ct)
    {
        var systemPrompt = BuildSystemPrompt(persona, facts, segment);
        var messages = BuildMessages(systemPrompt, segment, conversationHistory);

        var request = new LlmCompletionRequest
        {
            Model = _model,
            Messages = messages,
            Temperature = 0.8,
            MaxTokens = 512,
            RequestId = $"actor-{Guid.NewGuid():N}",
            ConversationId = "scenario-eval-actor"
        };

        var result = await _llmClient.CompleteAsync(request, ct);

        if (!result.Success)
        {
            _logger.LogError("Actor LLM call failed: {Error}", result.ErrorMessage);
            throw new InvalidOperationException($"Actor LLM call failed: {result.ErrorMessage}");
        }

        var tokens = new TokenUsageInfo
        {
            PromptTokens = result.Usage?.PromptTokens ?? 0,
            CompletionTokens = result.Usage?.CompletionTokens ?? 0,
            TotalTokens = result.Usage?.TotalTokens ?? 0
        };

        var message = result.Content?.Trim() ?? "";
        _logger.LogDebug("Actor generated: {Message}", message[..Math.Min(100, message.Length)]);

        return (message, tokens);
    }

    private static string BuildSystemPrompt(
        PersonaDefinition persona,
        Dictionary<string, string[]> facts,
        SegmentDefinition segment)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are {persona.Name}. {persona.Background}");
        sb.AppendLine($"Personality: {persona.Personality}");
        sb.AppendLine();
        sb.AppendLine("You know these facts about yourself:");

        foreach (var (category, items) in facts)
        {
            sb.AppendLine($"  {category}:");
            foreach (var item in items)
                sb.AppendLine($"    - {item}");
        }

        sb.AppendLine();

        switch (segment.Type)
        {
            case "task":
                sb.AppendLine($"You need the AI assistant to help you with: {segment.Topic}");
                sb.AppendLine("Give clear directives. You are asking for help, not having a casual chat.");
                break;
            case "schedule":
                sb.AppendLine($"You want to set up: {segment.Topic}");
                sb.AppendLine("Be specific about the schedule details (day, time, what to remind about).");
                break;
            default:
                sb.AppendLine($"Current topic: {segment.Topic}");
                sb.AppendLine("Generate a single natural message as this person would say it.");
                sb.AppendLine("Do NOT dump all facts at once — reveal them naturally as the topic calls for.");
                break;
        }

        if (segment.Hints.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Weave in these details naturally:");
            foreach (var hint in segment.Hints)
                sb.AppendLine($"  - {hint}");
        }

        return sb.ToString();
    }

    private static List<LlmMessage> BuildMessages(
        string systemPrompt,
        SegmentDefinition segment,
        IReadOnlyList<Exchange> history)
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = systemPrompt }
        };

        // Include prior exchanges for context
        foreach (var exchange in history)
        {
            messages.Add(new LlmMessage { Role = "assistant", Content = exchange.User });
            messages.Add(new LlmMessage { Role = "user", Content = exchange.Agent });
        }

        // Final instruction
        var instruction = history.Count == 0
            ? "Generate your opening message to the AI assistant."
            : "Generate your next message to the AI assistant. Continue naturally from the conversation.";

        messages.Add(new LlmMessage { Role = "user", Content = instruction });

        return messages;
    }
}
