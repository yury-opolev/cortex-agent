using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Llm.Providers;

/// <summary>
/// The production <see cref="ILlmStreamStallObserver"/>: writes one structured Warning per
/// breach and advances the phase-split counters on <see cref="AgentMetrics"/>.
/// <para>
/// Before this existed a breach was logged only as the resulting fault message, which was
/// indistinguishable from a genuine network fault and carried no task, model or context size —
/// diagnosing the 2026-08-15 incident needed a source dive rather than a log query.
/// </para>
/// </summary>
internal sealed partial class LlmStreamStallTelemetry : ILlmStreamStallObserver
{
    private readonly ILogger logger;
    private readonly AgentMetrics? metrics;

    internal LlmStreamStallTelemetry(ILogger logger, AgentMetrics? metrics)
    {
        this.logger = logger;
        this.metrics = metrics;
    }

    public void OnStall(LlmStreamStallReport report)
    {
        this.metrics?.RecordStreamStall(report.Phase);

        this.LogStall(
            report.Phase.ToString(),
            report.Elapsed.TotalSeconds,
            report.Budget.TotalSeconds,
            report.ContentEmitted,
            report.ConversationId ?? "(unknown)",
            report.Model ?? "(unknown)",
            report.Provider ?? "(unknown)",
            report.PromptChars,
            report.ChunksReceived,
            report.KeepAlivesReceived,
            report.ContentCharsReceived,
            report.RequestId ?? "(unknown)");
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "[llm-stream] Inactivity watchdog fired: phase={Phase} idle={IdleSeconds:0.#}s "
            + "budget={BudgetSeconds:0.#}s contentEmitted={ContentEmitted} "
            + "conversation={ConversationId} model={Model} provider={Provider} "
            + "promptChars={PromptChars} chunks={ChunksReceived} keepAlives={KeepAlivesReceived} "
            + "contentChars={ContentCharsReceived} requestId={RequestId}")]
    private partial void LogStall(
        string phase,
        double idleSeconds,
        double budgetSeconds,
        bool contentEmitted,
        string conversationId,
        string model,
        string provider,
        int promptChars,
        int chunksReceived,
        int keepAlivesReceived,
        int contentCharsReceived,
        string requestId);
}
