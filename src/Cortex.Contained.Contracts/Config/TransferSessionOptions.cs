using System.ComponentModel.DataAnnotations;

namespace Cortex.Contained.Contracts.Config;

/// <summary>
/// Tuning knobs for the <c>transfer_session</c> tool's internal LLM slicer.
/// Bound from configuration so deployments can tune model, temperature, and prompt
/// without recompiling.
/// </summary>
public sealed class TransferSessionOptions
{
    /// <summary>
    /// Override the model used by the slicer. When null (default), the slicer uses
    /// <c>IModelProvider.MemoryModel</c> — the same model the conversation-compaction
    /// path uses. Set this to use a different model just for transfer slicing
    /// (e.g. a cheaper model for cost-sensitive deployments, or a larger model when
    /// transfers must be very accurate).
    /// </summary>
    public string? SlicerModel { get; set; }

    /// <summary>
    /// Sampling temperature for the slicer LLM call. Lower values produce more
    /// deterministic boundary detection; higher values may surface novel topic
    /// boundaries but risk inconsistency. Default: 0.3.
    /// </summary>
    [Range(0.0, 2.0)]
    public double SlicerTemperature { get; set; } = 0.3;

    /// <summary>
    /// Override for the slicer system prompt. When null (default), the slicer uses
    /// its baked-in prompt (see <c>LlmTopicSlicer.SystemPrompt</c>). Setting this
    /// allows A/B testing or per-tenant prompt customization without rebuilding.
    /// The override prompt must still elicit a JSON response with the same fields
    /// (boundaryIndex, topicOneLine, priorSummary) — the parser is strict.
    /// </summary>
    public string? SlicerSystemPromptOverride { get; set; }
}
