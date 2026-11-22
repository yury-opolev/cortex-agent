using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.History.Evaluation.Smoke;

/// <summary>
/// System (b) of the smoke evaluation: asks the main model to write one <c>history_search</c> call
/// for each natural-language request, given exactly the tool description and schema the agent sees.
/// </summary>
public sealed class ModelQueryWriter
{
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    private readonly ILlmClient llm;
    private readonly string model;

    public ModelQueryWriter(ILlmClient llm, string model)
    {
        this.llm = llm;
        this.model = model;
    }

    /// <summary>Writes a query for <paramref name="request"/>; null when the model's answer is unusable.</summary>
    public async Task<SmokeQuerySpec?> WriteAsync(string request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var prompt =
            $"You are deciding how to call the tool history_search.\n\nTool description:\n{HistorySearchTool.ToolDescription}\n\n" +
            $"Parameters schema:\n{HistorySearchTool.ToolSchema}\n\nCurrent time (UTC): {now.ToString("O", CultureInfo.InvariantCulture)}\n\n" +
            $"User request:\n{request}\n\n" +
            "Return ONLY a JSON object with the arguments of ONE history_search call: {\"query\": string, \"order\": \"relevance\"|\"time\", \"since\": ISO 8601 or null, \"until\": ISO 8601 or null, \"channel\": string or null}.";

        var result = await this.llm.CompleteAsync(
            new LlmCompletionRequest
            {
                Model = this.model,
                Messages = [new LlmMessage { Role = "user", Content = prompt }],
                Temperature = 0,
                MaxTokens = 300,
                RequestId = "smoke-query-" + Guid.NewGuid().ToString("N"),
                ConversationId = "history-smoke-eval",
            },
            cancellationToken).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Content))
        {
            return null;
        }

        try
        {
            var spec = JsonSerializer.Deserialize<SmokeQuerySpec>(MemoryConsolidationService.StripToJson(result.Content), json);
            return string.IsNullOrWhiteSpace(spec?.Query) ? null : spec;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
