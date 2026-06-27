using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>Result of streaming one LLM turn (text, tool calls, usage, and outcome).</summary>
internal sealed record StreamedTurn(
    StreamOutcome Outcome,
    string Text,
    IReadOnlyList<LlmToolCall> ToolCalls,
    LlmTokenUsage? Usage,
    int SequenceNumber);
