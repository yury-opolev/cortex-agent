using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>A search as the agent's main model would issue it: text plus its filters (history) or tags (memory).</summary>
public sealed record ModelWrittenQuery(string Text, EvalFilters? Filters = null, IReadOnlyList<string>? Tags = null);

/// <summary>
/// The model-written side of "oracle vs model-written filters" (§4.2): the agent's main model gets exactly the tool
/// description and schema it sees in conversation and writes one <c>history_search</c> / <c>memory_search</c> call for
/// the query. Real requests already have one (the recorded arguments, <see cref="FromArgs"/>).
/// </summary>
public sealed class MainModelQueryWriter
{
    private readonly LabellingLlm llm;
    private readonly string model;
    private readonly TimeProvider time;

    public MainModelQueryWriter(LabellingLlm llm, string model, TimeProvider? time = null)
    {
        this.llm = llm;
        this.model = model;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>The main model's call for <paramref name="request"/>, or null when it gave no usable answer.</summary>
    public async Task<ModelWrittenQuery?> WriteAsync(string tool, string request, CancellationToken cancellationToken)
    {
        var memory = string.Equals(tool, "memory-eval", StringComparison.Ordinal);
        var (name, description, schema) = memory
            ? ("memory_search", MemorySearchTool.ToolDescription, MemorySearchTool.ToolSchema)
            : ("history_search", HistorySearchTool.ToolDescription, HistorySearchTool.ToolSchema);
        var user =
            $"Tool description:\n{description}\n\nParameters schema:\n{schema}\n\n" +
            $"Current time (UTC): {this.time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)}\n\nUser request:\n{request}";
        var answer = await this.llm.CompleteJsonAsync<JsonObject>(
            this.model,
            $"You are deciding how to call the tool {name}. Return ONLY a JSON object with the arguments of ONE {name} call.",
            user,
            600,
            a => FromArgs(a.ToJsonString()) is not null,
            cancellationToken).ConfigureAwait(false);
        return answer is null ? null : FromArgs(answer.ToJsonString());
    }

    /// <summary>Text, filters and tags of recorded or written tool arguments; null without a query.</summary>
    public static ModelWrittenQuery? FromArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonObject? args;
        try
        {
            args = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (args?["query"] is not JsonValue query || !query.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var filters = new EvalFilters { Channel = Text(args["channel"]), Since = Time(args["since"]), Until = Time(args["until"]) };
        return new ModelWrittenQuery(
            text.Trim(),
            filters.Channel is null && filters.Since is null && filters.Until is null ? null : filters,
            Tags(args["tags"]));
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static DateTimeOffset? Time(JsonNode? node) =>
        Text(node) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;

    // memory_search takes tags as a JSON array or as a string holding one.
    private static List<string>? Tags(JsonNode? node)
    {
        var array = node as JsonArray;
        if (array is null && Text(node) is { } text)
        {
            try
            {
                array = JsonNode.Parse(text) as JsonArray;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        var tags = array?.Select(Text).OfType<string>().ToList();
        return tags is { Count: > 0 } ? tags : null;
    }
}
