using System.Text;
using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Tests for the OpenAI Responses protocol parsers: <see cref="OpenAiResponsesResponse.Parse"/>
/// (non-streaming) and <see cref="OpenAiResponsesSseReader.ReadAsync"/> (streaming SSE).
/// Streaming tool-call deltas are verified against a replica of
/// <c>AgentRuntime</c>'s accumulator so the emitted deltas remain compatible.
/// </summary>
public class OpenAiResponsesParserTests
{
    private static ProviderState BuildProvider() => new(new LlmProviderCredential
    {
        Name = "github-copilot",
        Api = "github-copilot-api",
        Kind = CredentialKind.GitHubCopilotBearer,
        AccessToken = "bearer",
        Models = ["gpt-5.6-sol"],
    });

    // ── Non-streaming ────────────────────────────────────────────────

    [Fact]
    public void Parse_TextAndFunctionCall_MapsResultToolCallFinishAndUsage()
    {
        const string json =
            """
            {
              "id": "resp_1",
              "status": "completed",
              "output": [
                { "type": "message", "role": "assistant", "content": [ { "type": "output_text", "text": "Hello world" } ] },
                { "type": "function_call", "call_id": "call_abc", "name": "get_weather", "arguments": "{\"city\":\"Paris\"}" }
              ],
              "usage": { "input_tokens": 12, "output_tokens": 7, "total_tokens": 19 }
            }
            """;

        var result = OpenAiResponsesResponse.Parse(json, BuildProvider());

        Assert.True(result.Success);
        Assert.Equal("Hello world", result.Content);

        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("call_abc", call.Id);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("{\"city\":\"Paris\"}", call.Arguments);

        Assert.Equal("tool_calls", result.FinishReason);

        Assert.NotNull(result.Usage);
        Assert.Equal(12, result.Usage!.PromptTokens);
        Assert.Equal(7, result.Usage.CompletionTokens);
        Assert.Equal(19, result.Usage.TotalTokens);

        Assert.Equal("github-copilot", result.ProviderId);
    }

    [Fact]
    public void Parse_MultipleOutputTextParts_AreJoined()
    {
        const string json =
            """
            {
              "status": "completed",
              "output": [
                { "type": "message", "role": "assistant", "content": [
                    { "type": "output_text", "text": "part one " },
                    { "type": "output_text", "text": "part two" }
                ] }
              ],
              "usage": { "input_tokens": 1, "output_tokens": 1, "total_tokens": 2 }
            }
            """;

        var result = OpenAiResponsesResponse.Parse(json, BuildProvider());

        Assert.True(result.Success);
        Assert.Equal("part one part two", result.Content);
    }

    [Fact]
    public void Parse_TextOnly_FinishReasonStop()
    {
        const string json =
            """
            {
              "status": "completed",
              "output": [
                { "type": "message", "role": "assistant", "content": [ { "type": "output_text", "text": "Just text." } ] }
              ],
              "usage": { "input_tokens": 3, "output_tokens": 2, "total_tokens": 5 }
            }
            """;

        var result = OpenAiResponsesResponse.Parse(json, BuildProvider());

        Assert.True(result.Success);
        Assert.Equal("stop", result.FinishReason);
        Assert.Null(result.ToolCalls);
    }

    [Fact]
    public void Parse_FailedResponse_ProducesCleanFailureWithErrorMessage()
    {
        const string json =
            """
            {
              "status": "failed",
              "error": { "code": "server_error", "message": "the model exploded" }
            }
            """;

        var result = OpenAiResponsesResponse.Parse(json, BuildProvider());

        Assert.False(result.Success);
        Assert.Contains("the model exploded", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal("github-copilot", result.ProviderId);
    }

    // ── Streaming ────────────────────────────────────────────────────

    [Fact]
    public async Task Stream_OutputTextDelta_YieldsContentDeltas()
    {
        // Second event uses "data:" with no following space to exercise both forms.
        const string sse =
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"Hel\"}\n" +
            "\n" +
            "data:{\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"lo\"}\n" +
            "\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":4,\"output_tokens\":2,\"total_tokens\":6}}}\n" +
            "\n";

        var chunks = await CollectAsync(sse);

        Assert.Equal("Hel", chunks[0].ContentDelta);
        Assert.Equal("lo", chunks[1].ContentDelta);

        var terminal = Assert.Single(chunks, c => c.IsComplete);
        Assert.Equal("stop", terminal.FinishReason);
        Assert.Null(terminal.ErrorMessage);
        Assert.Equal(6, terminal.Usage!.TotalTokens);
    }

    [Fact]
    public async Task Stream_FunctionCall_EmitsAccumulatorCompatibleDeltasWithoutDuplication()
    {
        const string sse =
            "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"do_it\",\"arguments\":\"\"}}\n" +
            "\n" +
            "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"delta\":\"{\\\"a\\\":\"}\n" +
            "\n" +
            "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"delta\":\"1}\"}\n" +
            "\n" +
            // done repeats the FULL arguments — must not be re-emitted.
            "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"do_it\",\"arguments\":\"{\\\"a\\\":1}\"}}\n" +
            "\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":5,\"output_tokens\":3,\"total_tokens\":8}}}\n" +
            "\n";

        var chunks = await CollectAsync(sse);

        var toolCall = Assert.Single(AccumulateToolCalls(chunks));
        Assert.Equal("call_1", toolCall.Id);
        Assert.Equal("do_it", toolCall.Name);
        Assert.Equal("{\"a\":1}", toolCall.Arguments); // not "{\"a\":1}{\"a\":1}"

        var terminal = Assert.Single(chunks, c => c.IsComplete);
        Assert.Equal("tool_calls", terminal.FinishReason);
        Assert.Null(terminal.ErrorMessage);
        Assert.Equal(8, terminal.Usage!.TotalTokens);
    }

    [Fact]
    public async Task Stream_FunctionCall_ArgumentsOnlyInDone_EmittedOnce()
    {
        // Some providers emit no argument deltas and only the full arguments on done.
        const string sse =
            "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"call_id\":\"call_2\",\"name\":\"f\",\"arguments\":\"\"}}\n" +
            "\n" +
            "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"call_id\":\"call_2\",\"name\":\"f\",\"arguments\":\"{\\\"k\\\":9}\"}}\n" +
            "\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}\n" +
            "\n";

        var chunks = await CollectAsync(sse);

        var toolCall = Assert.Single(AccumulateToolCalls(chunks));
        Assert.Equal("call_2", toolCall.Id);
        Assert.Equal("f", toolCall.Name);
        Assert.Equal("{\"k\":9}", toolCall.Arguments);
    }

    [Fact]
    public async Task Stream_Completed_EmitsExactlyOneTerminalChunk()
    {
        const string sse =
            "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"x\"}\n" +
            "\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}\n" +
            "\n" +
            // Anything after the terminal event must be ignored.
            "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"ignored\"}\n" +
            "\n";

        var chunks = await CollectAsync(sse);

        Assert.Single(chunks, c => c.IsComplete);
        Assert.DoesNotContain(chunks, c => c.ContentDelta == "ignored");
    }

    [Fact]
    public async Task Stream_IncompleteMaxOutputTokens_TerminalFinishReasonLength()
    {
        const string sse =
            "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"partial\"}\n" +
            "\n" +
            "data: {\"type\":\"response.incomplete\",\"response\":{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}\n" +
            "\n";

        var chunks = await CollectAsync(sse);

        Assert.Equal("partial", chunks[0].ContentDelta);

        var terminal = Assert.Single(chunks, c => c.IsComplete);
        Assert.Equal("length", terminal.FinishReason);
        Assert.Null(terminal.ErrorMessage);
    }

    [Fact]
    public async Task Stream_FailedNestedError_TerminalErrorChunk()
    {
        const string sse =
            "data: {\"type\":\"response.failed\",\"response\":{\"status\":\"failed\",\"error\":{\"code\":\"server_error\",\"message\":\"boom nested\"}}}\n" +
            "\n";

        var chunks = await CollectAsync(sse);

        var terminal = Assert.Single(chunks, c => c.IsComplete);
        Assert.NotNull(terminal.ErrorMessage);
        Assert.Contains("boom nested", terminal.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_TopLevelError_TerminalErrorChunk()
    {
        const string sse =
            "data: {\"type\":\"error\",\"code\":\"rate_limit\",\"message\":\"slow down\"}\n" +
            "\n";

        var chunks = await CollectAsync(sse);

        var terminal = Assert.Single(chunks, c => c.IsComplete);
        Assert.NotNull(terminal.ErrorMessage);
        Assert.Contains("slow down", terminal.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_EofWithoutTerminalEvent_EmitsTerminalErrorAndNoCleanSuccess()
    {
        const string sse =
            "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"hi\"}\n" +
            "\n" +
            "data: {\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"type\":\"function_call\",\"call_id\":\"call_z\",\"name\":\"g\",\"arguments\":\"\"}}\n" +
            "\n" +
            "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":1,\"delta\":\"{}\"}\n";
        // NOTE: no response.completed / .incomplete / .failed — stream truncated.

        var chunks = await CollectAsync(sse);

        var terminal = Assert.Single(chunks, c => c.IsComplete);
        Assert.NotNull(terminal.ErrorMessage);

        // Must not silently succeed: no terminal chunk that is a clean (error-free) completion.
        Assert.DoesNotContain(chunks, c => c.IsComplete && c.ErrorMessage is null);
    }

    [Fact]
    public async Task Stream_IgnoresCommentsBlankLinesDoneAndUnknownEvents()
    {
        const string sse =
            ": this is an SSE comment / heartbeat\n" +
            "\n" +
            "data: {\"type\":\"response.created\",\"response\":{\"status\":\"in_progress\"}}\n" +
            "\n" +
            "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"ok\"}\n" +
            "\n" +
            "data: [DONE]\n" +
            "\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}\n" +
            "\n";

        var chunks = await CollectAsync(sse);

        var content = Assert.Single(chunks, c => c.ContentDelta is not null);
        Assert.Equal("ok", content.ContentDelta);

        var terminal = Assert.Single(chunks, c => c.IsComplete);
        Assert.Equal("stop", terminal.FinishReason);
        Assert.Null(terminal.ErrorMessage);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static async Task<List<LlmStreamChunk>> CollectAsync(string sse)
    {
        using var reader = new StringReader(sse);
        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in OpenAiResponsesSseReader.ReadAsync(reader, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    /// <summary>
    /// Replica of <c>AgentRuntime</c>'s streaming tool-call accumulator (keyed by
    /// <see cref="LlmToolCallDelta.Index"/>) so the reader's emitted deltas are proven
    /// compatible with the real consumer.
    /// </summary>
    private static List<LlmToolCall> AccumulateToolCalls(IEnumerable<LlmStreamChunk> chunks)
    {
        var accumulators = new Dictionary<int, (string? Id, string? Name, StringBuilder Args)>();

        foreach (var chunk in chunks)
        {
            if (chunk.ToolCallDeltas is not { Count: > 0 } deltas)
            {
                continue;
            }

            foreach (var delta in deltas)
            {
                if (!accumulators.TryGetValue(delta.Index, out var acc))
                {
                    acc = (null, null, new StringBuilder());
                    accumulators[delta.Index] = acc;
                }

                if (delta.Id is not null)
                {
                    acc.Id = delta.Id;
                }

                if (delta.Name is not null)
                {
                    acc.Name = delta.Name;
                }

                if (delta.ArgumentsDelta is not null)
                {
                    acc.Args.Append(delta.ArgumentsDelta);
                }

                accumulators[delta.Index] = acc;
            }
        }

        return accumulators
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => new LlmToolCall
            {
                Id = kvp.Value.Id ?? $"call_{kvp.Key}",
                Name = kvp.Value.Name ?? "unknown",
                Arguments = kvp.Value.Args.ToString(),
            })
            .ToList();
    }
}
