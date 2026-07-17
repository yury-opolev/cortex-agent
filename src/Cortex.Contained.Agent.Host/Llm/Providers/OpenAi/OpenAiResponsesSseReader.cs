using System.Runtime.CompilerServices;
using System.Text.Json;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>
/// Parses an OpenAI Responses API server-sent event (SSE) stream into
/// <see cref="LlmStreamChunk"/> values. Dispatch is on each event payload's
/// <c>type</c> field (the <c>event:</c> line is ignored). Comments, blank lines,
/// and <c>[DONE]</c> are skipped; <c>data:</c> is accepted with or without a
/// following space.
/// <para>
/// Function-call arguments are tracked per <c>output_index</c> so the full
/// arguments repeated on <c>output_item.done</c> are never re-emitted (the
/// emitted <see cref="LlmToolCallDelta"/>s stay compatible with
/// <c>AgentRuntime</c>'s index-keyed accumulator). Exactly one terminal chunk is
/// produced: a completion (<c>stop</c>/<c>tool_calls</c>/<c>length</c>) or an
/// error. A stream that ends without a terminal event yields a terminal error
/// chunk rather than silently succeeding, matching <c>DirectLlmClient</c>'s
/// terminal-error-driven retry/failover contract.
/// </para>
/// </summary>
internal static class OpenAiResponsesSseReader
{
    internal static async IAsyncEnumerable<LlmStreamChunk> ReadAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // output_index -> number of argument characters already emitted for that
        // function call, so output_item.done's full-argument repeat is de-duplicated.
        var emittedArgLengths = new Dictionary<int, int>();
        var sawFunctionCall = false;
        var terminalEmitted = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0 || line[0] == ':')
            {
                continue; // event boundary (blank) or comment/heartbeat
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue; // event:/id:/retry: — dispatch is on the payload's "type"
            }

            var data = line["data:".Length..];
            if (data.Length > 0 && data[0] == ' ')
            {
                data = data[1..];
            }

            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }

            OpenAiResponsesStreamEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<OpenAiResponsesStreamEvent>(
                    data, ProviderClientHelpers.JsonOptions);
            }
            catch (JsonException)
            {
                continue; // skip a malformed event rather than aborting the stream
            }

            if (evt?.Type is not { Length: > 0 } type)
            {
                continue;
            }

            switch (type)
            {
                case "response.output_text.delta":
                    if (!string.IsNullOrEmpty(evt.Delta))
                    {
                        yield return new LlmStreamChunk { ContentDelta = evt.Delta };
                    }

                    break;

                case "response.output_item.added":
                    if (evt.Item?.Type == "function_call")
                    {
                        sawFunctionCall = true;
                        var index = evt.OutputIndex ?? 0;
                        var seedArgs = evt.Item.Arguments ?? string.Empty;
                        emittedArgLengths[index] = seedArgs.Length;
                        yield return new LlmStreamChunk
                        {
                            ToolCallDeltas =
                            [
                                new LlmToolCallDelta
                                {
                                    Index = index,
                                    Id = evt.Item.CallId,
                                    Name = evt.Item.Name,
                                    ArgumentsDelta = seedArgs.Length > 0 ? seedArgs : null,
                                },
                            ],
                        };
                    }

                    break;

                case "response.function_call_arguments.delta":
                    if (!string.IsNullOrEmpty(evt.Delta))
                    {
                        sawFunctionCall = true;
                        var index = evt.OutputIndex ?? 0;
                        emittedArgLengths[index] = emittedArgLengths.GetValueOrDefault(index) + evt.Delta.Length;
                        yield return new LlmStreamChunk
                        {
                            ToolCallDeltas =
                            [
                                new LlmToolCallDelta { Index = index, ArgumentsDelta = evt.Delta },
                            ],
                        };
                    }

                    break;

                case "response.output_item.done":
                    if (evt.Item?.Type == "function_call")
                    {
                        sawFunctionCall = true;
                        var index = evt.OutputIndex ?? 0;
                        var seenAdded = emittedArgLengths.TryGetValue(index, out var alreadyEmitted);
                        var fullArgs = evt.Item.Arguments ?? string.Empty;
                        var suffix = fullArgs.Length > alreadyEmitted ? fullArgs[alreadyEmitted..] : null;

                        // Only emit when there is something new: the id/name (if we never
                        // saw "added") or the tail of arguments not yet streamed.
                        if (!seenAdded || suffix is not null)
                        {
                            yield return new LlmStreamChunk
                            {
                                ToolCallDeltas =
                                [
                                    new LlmToolCallDelta
                                    {
                                        Index = index,
                                        Id = seenAdded ? null : evt.Item.CallId,
                                        Name = seenAdded ? null : evt.Item.Name,
                                        ArgumentsDelta = suffix,
                                    },
                                ],
                            };
                        }

                        emittedArgLengths[index] = Math.Max(alreadyEmitted, fullArgs.Length);
                    }

                    break;

                case "response.completed":
                    yield return new LlmStreamChunk
                    {
                        IsComplete = true,
                        FinishReason = sawFunctionCall ? "tool_calls" : "stop",
                        Usage = evt.Response?.Usage?.ToTokenUsage(),
                    };
                    terminalEmitted = true;
                    yield break;

                case "response.incomplete":
                    var reason = evt.Response?.IncompleteDetails?.Reason;
                    yield return new LlmStreamChunk
                    {
                        IsComplete = true,

                        // max_output_tokens maps to Chat Completions' "length" so the
                        // rest of the pipeline sees a single, uniform truncation signal.
                        FinishReason = reason == "max_output_tokens"
                            ? "length"
                            : sawFunctionCall ? "tool_calls" : "stop",
                        Usage = evt.Response?.Usage?.ToTokenUsage(),
                    };
                    terminalEmitted = true;
                    yield break;

                case "response.failed":
                    var failMessage = evt.Response?.Error?.Message ?? evt.Message ?? "Responses stream failed.";
                    yield return new LlmStreamChunk
                    {
                        IsComplete = true,
                        ErrorMessage = ProviderClientHelpers.TruncateError(failMessage),
                    };
                    terminalEmitted = true;
                    yield break;

                case "error":
                    var errorMessage = evt.Message ?? evt.Response?.Error?.Message ?? "Responses stream error.";
                    yield return new LlmStreamChunk
                    {
                        IsComplete = true,
                        ErrorMessage = ProviderClientHelpers.TruncateError(errorMessage),
                    };
                    terminalEmitted = true;
                    yield break;

                default:
                    // response.created / .in_progress / content_part.* / reasoning.* etc.
                    break;
            }
        }

        if (!terminalEmitted)
        {
            yield return new LlmStreamChunk
            {
                IsComplete = true,
                ErrorMessage = "Responses stream ended without a terminal event.",
            };
        }
    }
}
