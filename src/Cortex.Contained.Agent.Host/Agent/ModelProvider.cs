namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Provides access to the current default LLM model name and its limits.
/// Shared between <see cref="AgentRuntime"/> (which sets the model when credentials
/// arrive from the Bridge) and memory services / tools that need to make LLM calls
/// using the same model.
/// <para>
/// This exists as a separate service to avoid circular dependencies between
/// <see cref="AgentRuntime"/> and tools registered in <c>ToolRegistry</c>.
/// </para>
/// </summary>
public interface IModelProvider
{
    /// <summary>Get the current default model name.</summary>
    string DefaultModel { get; }

    /// <summary>
    /// Model to use for memory-related tasks (context steering, extraction, compaction).
    /// Falls back to <see cref="DefaultModel"/> if not set.
    /// </summary>
    string MemoryModel { get; }

    /// <summary>Context window size (total tokens) for the current model.</summary>
    int ContextWindow { get; }

    /// <summary>Maximum output tokens for the current model.</summary>
    int MaxOutputTokens { get; }

    /// <summary>Set the current default model name and its limits.</summary>
    void SetDefaultModel(string model, int contextWindow = 128_000, int maxOutputTokens = 8_192);

    /// <summary>Set the model used for memory tasks. Null = fall back to default model.</summary>
    void SetMemoryModel(string? model);
}

/// <inheritdoc />
public sealed class ModelProvider : IModelProvider
{
    private volatile string model = "gpt-4o";
    private volatile string? memoryModel;
    private volatile int contextWindow = 128_000;
    private volatile int maxOutputTokens = 8_192;

    /// <inheritdoc />
    public string DefaultModel => this.model;

    /// <inheritdoc />
    public string MemoryModel => this.memoryModel ?? this.model;

    /// <inheritdoc />
    public int ContextWindow => this.contextWindow;

    /// <inheritdoc />
    public int MaxOutputTokens => this.maxOutputTokens;

    /// <inheritdoc />
    public void SetDefaultModel(string model, int contextWindow = 128_000, int maxOutputTokens = 8_192)
    {
        this.model = model;
        this.contextWindow = contextWindow;
        this.maxOutputTokens = maxOutputTokens;
    }

    /// <inheritdoc />
    public void SetMemoryModel(string? model)
    {
        this.memoryModel = model;
    }
}
