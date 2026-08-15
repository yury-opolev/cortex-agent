using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Cortex.Contained.Agent.Host.Llm.Providers;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

// ── Configuration ────────────────────────────────────────────────────

/// <summary>
/// Configuration for an <see cref="AgentLoop"/> execution.
/// </summary>
public sealed record AgentLoopConfig
{
    /// <summary>Maximum tool-loop rounds before terminating.</summary>
    public int MaxRounds { get; init; } = 200;

    /// <summary>Tool names to exclude from the agent's tool definitions.</summary>
    public FrozenSet<string>? ExcludedTools { get; init; }

    /// <summary>LLM model ID.</summary>
    public required string Model { get; init; }

    /// <summary>Sampling temperature. Null = use provider default.</summary>
    public double? Temperature { get; init; }

    /// <summary>Max output tokens per LLM call.</summary>
    public int MaxOutputTokens { get; init; } = 8192;

    /// <summary>
    /// Requested reasoning effort for this loop's calls. Null = leave the provider on its own
    /// default. Resolved per provider and per model before it reaches the wire.
    /// </summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>Total context window for the model.</summary>
    public int ContextWindow { get; init; } = 128_000;

    /// <summary>Conversation ID for LLM request tracking and tool context.</summary>
    public required string ConversationId { get; init; }

    /// <summary>Channel ID for tool execution context.</summary>
    public required string ChannelId { get; init; }

    /// <summary>
    /// How many times a round may be re-issued after the provider stream faulted with a
    /// TRANSIENT fault (a dropped connection, or the inactivity watchdog firing) AFTER content
    /// had already been streamed. <c>DirectLlmClient</c> cannot retry that case — its retry and
    /// failover are both pre-content only — so the loop covers it by discarding the dead attempt
    /// and re-sending the identical request.
    /// <para>
    /// Defaults to 0 - OFF. Re-issuing a round is only safe for a consumer that DISCARDS
    /// content deltas, because a consumer that displayed them would show the text twice. That
    /// invariant cannot be checked here, so the caller must opt in deliberately:
    /// <see cref="SubagentRunner"/> does (subagent deltas go nowhere), and any future
    /// user-facing consumer inherits the safe behaviour by saying nothing.
    /// </para>
    /// </summary>
    public int MaxTransientStreamRetries { get; init; }
}

// ── Result ───────────────────────────────────────────────────────────

/// <summary>
/// How the <see cref="AgentLoop"/> terminated.
/// </summary>
public enum AgentLoopOutcome
{
    /// <summary>LLM returned a final text response (no tool calls).</summary>
    Completed,

    /// <summary>LLM returned an error during streaming.</summary>
    Error,

    /// <summary>Doom loop detected — same tool called repeatedly.</summary>
    DoomLoop,

    /// <summary>Exhausted all allowed rounds without a final response.</summary>
    MaxRoundsExceeded,
}

/// <summary>
/// Result of an <see cref="AgentLoop"/> execution.
/// </summary>
public sealed record AgentLoopResult
{
    public required AgentLoopOutcome Outcome { get; init; }
    public string ResponseText { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
    public LlmTokenUsage? Usage { get; init; }
    public int RoundsExecuted { get; init; }
}

// ── Callbacks ──────────────────────────────────────────��─────────────

/// <summary>
/// Strategy interface that customizes <see cref="AgentLoop"/> behavior per caller.
/// Main agent and subagent provide different implementations.
/// Methods are called at well-defined points in the loop — implementations
/// can be no-ops for features not needed by that agent type.
/// </summary>
public interface IAgentLoopCallbacks
{
    /// <summary>
    /// Prepare messages for an LLM call. Called at the start of each round.
    /// Responsible for: reading conversation history, building system prompt,
    /// running ContextManager.PrepareMessages, injecting todos, etc.
    /// Returns the prepared message list to send to the LLM.
    /// </summary>
    Task<List<LlmMessage>> PrepareMessagesAsync(int round, CancellationToken cancellationToken);

    /// <summary>
    /// Drain any externally injected messages into the conversation.
    /// Called before PrepareMessages each round. Subagents drain from
    /// their <see cref="AgentSession"/> pending queue; main agent is
    /// typically a no-op (main agent drains between tool rounds instead).
    /// </summary>
    void DrainInjectedMessages();

    /// <summary>Called with each content delta during LLM streaming.</summary>
    Task OnContentDeltaAsync(string delta, int sequenceNumber, CancellationToken ct);

    /// <summary>Called when a tool execution starts.</summary>
    Task OnToolStartAsync(LlmToolCall toolCall, CancellationToken ct);

    /// <summary>Called when a tool execution completes.</summary>
    Task OnToolCompleteAsync(LlmToolCall toolCall, AgentToolResult result, TimeSpan duration, CancellationToken ct);

    /// <summary>
    /// Called after all tools in a round have been executed.
    /// Use for: compaction checks, state persistence, token tracking.
    /// </summary>
    Task OnRoundCompleteAsync(int round, LlmTokenUsage? usage, CancellationToken ct);

    /// <summary>
    /// Called when the LLM returns a context overflow error.
    /// Return true if recovery was performed (e.g., emergency compaction) and the round should retry.
    /// </summary>
    Task<bool> OnContextOverflowAsync(string errorMessage, CancellationToken ct);

    /// <summary>Called when the LLM returns a non-overflow error.</summary>
    Task OnErrorAsync(string errorMessage, CancellationToken ct);

    /// <summary>Called when doom loop is detected.</summary>
    Task OnDoomLoopAsync(string toolName, CancellationToken ct);

    /// <summary>Called when the loop terminates (any outcome).</summary>
    Task OnLoopCompleteAsync(AgentLoopResult result, CancellationToken ct);

    /// <summary>
    /// Record that an assistant message (with tool calls) was added to the conversation.
    /// Called after the message is built but before tool execution.
    /// </summary>
    void OnAssistantMessage(LlmMessage message);

    /// <summary>
    /// Record that a tool result message was added to the conversation.
    /// </summary>
    void OnToolResultMessage(LlmMessage message);
}

// ── The Loop ─────────────────────────────────────────────────────────

/// <summary>
/// Unified LLM tool loop used by both the main agent and subagents.
/// Stateless execution engine — all state management is delegated to
/// <see cref="IAgentLoopCallbacks"/>. The loop streams LLM responses,
/// accumulates tool calls, executes them, and repeats until the LLM
/// returns a final text response or a termination condition is met.
/// </summary>
public sealed partial class AgentLoop
{
    private readonly ILlmClient llmClient;
    private readonly ToolRegistry toolRegistry;
    private readonly ILogger<AgentLoop> logger;

    public AgentLoop(ILlmClient llmClient, ToolRegistry toolRegistry, ILogger<AgentLoop> logger)
    {
        this.llmClient = llmClient;
        this.toolRegistry = toolRegistry;
        this.logger = logger;
    }

    /// <summary>
    /// Execute the tool loop with the given configuration and callbacks.
    /// </summary>
    public async Task<AgentLoopResult> ExecuteAsync(
        AgentLoopConfig config,
        IAgentLoopCallbacks callbacks,
        CancellationToken cancellationToken)
    {
        var toolDefinitions = config.ExcludedTools is { Count: > 0 }
            ? this.toolRegistry.GetDefinitionsExcluding(config.ExcludedTools)
            : this.toolRegistry.GetDefinitions();

        var doomLoopDetector = new DoomLoopDetector();
        var toolContext = new ToolExecutionContext
        {
            ConversationId = config.ConversationId,
            ChannelId = config.ChannelId,
        };

        for (var round = 0; round < config.MaxRounds; round++)
        {
            // Drain externally injected messages
            callbacks.DrainInjectedMessages();

            // Build prepared messages for this round
            var prepared = await callbacks.PrepareMessagesAsync(round, cancellationToken).ConfigureAwait(false);

            var request = new LlmCompletionRequest
            {
                Model = config.Model,
                Messages = prepared,
                Tools = toolDefinitions.Count > 0 ? toolDefinitions : null,
                MaxTokens = config.MaxOutputTokens,
                RequestId = Guid.NewGuid().ToString("N"),
                ConversationId = config.ConversationId,
                ReasoningEffort = config.ReasoningEffort,
            };

            if (config.Temperature.HasValue)
            {
                request = request with { Temperature = config.Temperature.Value };
            }

            // Stream the response.
            //
            // The inner attempt loop exists because DirectLlmClient's same-provider retry and
            // its provider failover are BOTH gated on "nothing yielded yet" — it cannot
            // un-stream, so a fault raised after the first chunk escapes as a terminal error.
            // The most common such fault is LlmStreamIdleGuard's inactivity breach on a large
            // context, and losing a whole subagent run to one stall is not acceptable. The loop
            // CAN recover: no partial text has been committed to history, so it simply discards
            // the dead attempt and re-issues the identical request.
            //
            // Safe only while every AgentLoop consumer discards content deltas (subagents do).
            // A user-facing consumer must set MaxTransientStreamRetries = 0, because re-issuing
            // after deltas have been shown would duplicate visible text.
            var fullResponse = new StringBuilder();
            var sequenceNumber = 0;
            var toolCallAccumulators = new Dictionary<int, ToolCallAccumulator>();
            string? finishReason = null;
            LlmTokenUsage? usage = null;
            var retryAfterRecovery = false;
            AgentLoopResult? errorResult = null;
            var transientRetries = 0;

            while (true)
            {
                // Every attempt starts from a clean slate so a partial response can never be
                // concatenated onto the retry's response.
                // A retried attempt is a genuinely separate provider request, so it gets its own
                // correlation id - otherwise every attempt (and every stall report raised by
                // one) shares an id and the telemetry cannot tell them apart.
                request = request with { RequestId = Guid.NewGuid().ToString("N") };

                fullResponse.Clear();
                sequenceNumber = 0;
                toolCallAccumulators.Clear();
                finishReason = null;
                usage = null;
                retryAfterRecovery = false;
                errorResult = null;
                var retryStream = false;
                string? transientFault = null;

                await foreach (var chunk in this.llmClient.StreamCompleteAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (chunk.ErrorMessage is not null)
                    {
                        // Check for context overflow — give callbacks a chance to recover
                        if (ContextManager.IsContextOverflow(chunk.ErrorMessage))
                        {
                            var recovered = await callbacks.OnContextOverflowAsync(
                                chunk.ErrorMessage, cancellationToken).ConfigureAwait(false);
                            if (recovered)
                            {
                                retryAfterRecovery = true;
                                break;
                            }
                        }
                        else if (transientRetries < config.MaxTransientStreamRetries
                            && LlmStreamFault.IsTransientFaultMessage(chunk.ErrorMessage))
                        {
                            transientRetries++;
                            retryStream = true;
                            transientFault = chunk.ErrorMessage;
                            break;
                        }

                        await callbacks.OnErrorAsync(chunk.ErrorMessage, cancellationToken).ConfigureAwait(false);
                        errorResult = new AgentLoopResult
                        {
                            Outcome = AgentLoopOutcome.Error,
                            ErrorMessage = chunk.ErrorMessage,
                            RoundsExecuted = round + 1,
                        };
                        break;
                    }

                    if (chunk.ContentDelta is not null)
                    {
                        fullResponse.Append(chunk.ContentDelta);
                        await callbacks.OnContentDeltaAsync(
                            chunk.ContentDelta, sequenceNumber++, cancellationToken).ConfigureAwait(false);
                    }

                    AccumulateToolCallDeltas(chunk, toolCallAccumulators);

                    if (chunk.IsComplete)
                    {
                        finishReason = chunk.FinishReason;
                        usage = chunk.Usage;
                    }
                }

                if (!retryStream)
                {
                    break;
                }

                this.LogTransientStreamRetry(
                    config.ConversationId, round + 1, transientRetries,
                    config.MaxTransientStreamRetries, transientFault ?? string.Empty);
                await Task.Delay(TransientRetryBackoff(transientRetries), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (errorResult is not null)
            {
                await callbacks.OnLoopCompleteAsync(errorResult, cancellationToken).ConfigureAwait(false);
                return errorResult;
            }

            if (retryAfterRecovery)
            {
                continue;
            }

            // Check if the LLM wants to call tools
            var hasToolCalls = toolCallAccumulators.Count > 0 ||
                               string.Equals(finishReason, "tool_calls", StringComparison.OrdinalIgnoreCase);

            if (!hasToolCalls || toolCallAccumulators.Count == 0)
            {
                // No tool calls — final response
                var responseText = fullResponse.ToString();
                var completedResult = new AgentLoopResult
                {
                    Outcome = AgentLoopOutcome.Completed,
                    ResponseText = responseText,
                    Usage = usage,
                    RoundsExecuted = round + 1,
                };
                await callbacks.OnLoopCompleteAsync(completedResult, cancellationToken).ConfigureAwait(false);
                return completedResult;
            }

            // Build completed tool calls
            var toolCalls = BuildToolCalls(toolCallAccumulators);

            // Add assistant message with tool calls to history
            var assistantContent = fullResponse.Length > 0 ? fullResponse.ToString() : null;
            var assistantMessage = new LlmMessage
            {
                Role = "assistant",
                Content = assistantContent,
                ToolCalls = toolCalls,
            };
            callbacks.OnAssistantMessage(assistantMessage);

            this.LogToolCalls(config.ConversationId, round + 1, toolCalls.Count);

            // Doom loop detection
            if (doomLoopDetector.Check(toolCalls))
            {
                await callbacks.OnDoomLoopAsync(toolCalls[^1].Name, cancellationToken).ConfigureAwait(false);
                var doomResult = new AgentLoopResult
                {
                    Outcome = AgentLoopOutcome.DoomLoop,
                    ErrorMessage = $"Doom loop detected on tool '{toolCalls[^1].Name}'",
                    RoundsExecuted = round + 1,
                };
                await callbacks.OnLoopCompleteAsync(doomResult, cancellationToken).ConfigureAwait(false);
                return doomResult;
            }

            // Execute each tool call
            foreach (var toolCall in toolCalls)
            {
                await callbacks.OnToolStartAsync(toolCall, cancellationToken).ConfigureAwait(false);

                var stopwatch = Stopwatch.StartNew();
                var result = await this.toolRegistry.ExecuteAsync(toolCall, toolContext, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                await callbacks.OnToolCompleteAsync(toolCall, result, stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);

                var toolContent = result.Success
                    ? result.Content
                    : string.Create(CultureInfo.InvariantCulture, $"Error: {result.Error}");

                var toolMessage = new LlmMessage
                {
                    Role = "tool",
                    Content = toolContent,
                    ToolCallId = toolCall.Id,
                };
                callbacks.OnToolResultMessage(toolMessage);
            }

            // Post-round hook (compaction, persistence, etc.)
            await callbacks.OnRoundCompleteAsync(round + 1, usage, cancellationToken).ConfigureAwait(false);
        }

        // Exhausted all rounds
        var maxRoundsResult = new AgentLoopResult
        {
            Outcome = AgentLoopOutcome.MaxRoundsExceeded,
            ErrorMessage = $"Reached maximum tool call rounds ({config.MaxRounds})",
            RoundsExecuted = config.MaxRounds,
        };
        await callbacks.OnLoopCompleteAsync(maxRoundsResult, cancellationToken).ConfigureAwait(false);
        return maxRoundsResult;
    }

    // ── Shared helpers ───────────────────────────────────────────────────

    /// <summary>Accumulate tool call deltas from a streaming chunk.</summary>
    internal static void AccumulateToolCallDeltas(LlmStreamChunk chunk, Dictionary<int, ToolCallAccumulator> accumulators)
    {
        if (chunk.ToolCallDeltas is not { Count: > 0 } deltas)
        {
            return;
        }

        foreach (var delta in deltas)
        {
            if (!accumulators.TryGetValue(delta.Index, out var acc))
            {
                acc = new ToolCallAccumulator { Index = delta.Index };
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
                acc.Arguments.Append(delta.ArgumentsDelta);
            }
        }
    }

    /// <summary>Build completed tool calls from accumulators.</summary>
    internal static List<LlmToolCall> BuildToolCalls(Dictionary<int, ToolCallAccumulator> accumulators)
    {
        return accumulators.Values
            .OrderBy(acc => acc.Index)
            .Select(acc => new LlmToolCall
            {
                Id = acc.Id ?? $"call_{acc.Index}",
                Name = acc.Name ?? "unknown",
                Arguments = acc.Arguments.ToString(),
            })
            .ToList();
    }

    /// <summary>Accumulates streamed tool call deltas into a complete tool call.</summary>
    internal sealed class ToolCallAccumulator
    {
        public int Index { get; init; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }

    // ── Logging ──────────────────────────────────────────────────────────

    /// <summary>
    /// Backoff before transient stream retry N: ~0.5s, then ~2s. Same sequence as
    /// <c>DirectLlmClient.RetryBackoff</c>, which produces it from a 0-based attempt index
    /// while this one is called with a 1-based retry count.
    /// </summary>
    private static TimeSpan TransientRetryBackoff(int retryNumber)
        => TimeSpan.FromMilliseconds(500 * retryNumber * retryNumber);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "[agent-loop] {ConversationId} round {Round}: transient stream fault, re-issuing "
            + "({Attempt}/{MaxAttempts}): {Fault}")]
    private partial void LogTransientStreamRetry(
        string conversationId, int round, int attempt, int maxAttempts, string fault);

    [LoggerMessage(Level = LogLevel.Information, Message = "[agent-loop] {ConversationId} round {Round}: {ToolCount} tool calls")]
    private partial void LogToolCalls(string conversationId, int round, int toolCount);

}
