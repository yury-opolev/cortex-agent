using System.Globalization;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Contracts.Llm;
using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Builds a subagent worker's context (recalled memories, bootstrap context, skill) and drives the
/// <see cref="SubagentRunner"/> registered for the task, dispatching by the persisted
/// <see cref="SubagentTask.RunMode"/>. It returns the terminal <see cref="SubagentExecutionResult"/>
/// but never persists it — terminal persistence is owned exclusively by the
/// <see cref="SubagentExecutionCoordinator"/>.
/// </summary>
internal sealed partial class SubagentExecutor : ISubagentExecutor
{
    private readonly SubagentRunnerRegistry registry;
    private readonly ILlmClient llmClient;
    private readonly IModelProvider modelProvider;
    private readonly string bootstrapContextPath;
    private readonly IMemoryService? memoryService;
    private readonly SkillRegistry? skillRegistry;
    private readonly SystemPromptStore? systemPromptStore;
    private readonly ILogger<SubagentExecutor> logger;

    /// <summary>Maximum memories to inject into subagent context.</summary>
    private const int MaxMemoryResults = 6;

    /// <summary>Minimum similarity score for memory results.</summary>
    private const float MinMemoryScore = 0.3f;

    public SubagentExecutor(
        SubagentRunnerRegistry registry,
        ILlmClient llmClient,
        IModelProvider modelProvider,
        string stateRoot,
        ILogger<SubagentExecutor> logger,
        IMemoryService? memoryService = null,
        SkillRegistry? skillRegistry = null,
        SystemPromptStore? systemPromptStore = null)
    {
        this.registry = registry;
        this.llmClient = llmClient;
        this.modelProvider = modelProvider;
        this.bootstrapContextPath = Path.Combine(stateRoot, "context-bootstrap.md");
        this.logger = logger;
        this.memoryService = memoryService;
        this.skillRegistry = skillRegistry;
        this.systemPromptStore = systemPromptStore;
    }

    /// <inheritdoc />
    public async Task<SubagentExecutionResult> ExecuteAsync(
        SubagentTask task, CancellationToken cancellationToken)
    {
        // Use ONLY the runner the coordinator registered for this task, so a concurrent
        // sub_agent_send injecting into registry.TryGet(taskId) reaches the executing loop.
        // The coordinator always registers a runner before dispatching; a missing runner is
        // a programming error, surfaced as a Failed task by the coordinator's crash handler.
        var runner = this.registry.TryGet(task.TaskId)
            ?? throw new InvalidOperationException(
                $"No runner registered for task '{task.TaskId}' — register one before dispatching.");
        var model = this.modelProvider.DefaultModel;

        switch (task.RunMode)
        {
            case SubagentRunMode.New:
            {
                var memories = await this.RetrieveSubagentContextAsync(
                    task.Prompt, cancellationToken).ConfigureAwait(false);
                var bootstrapContext = this.LoadBootstrapContext();
                var systemPrompt = this.BuildSubagentSystemPrompt(memories, bootstrapContext, task.SkillName);

                return await runner.RunAsync(
                    model,
                    systemPrompt,
                    task.Prompt,
                    task.TaskId,
                    cancellationToken).ConfigureAwait(false);
            }

            case SubagentRunMode.Resume when task.Messages.Count > 0:
                return await runner.ResumeAsync(
                    model,
                    [.. task.Messages],
                    task.TaskId,
                    cancellationToken).ConfigureAwait(false);

            case SubagentRunMode.Resume:
                return new SubagentExecutionResult(
                    SubagentTaskState.Failed,
                    "Cannot resume a subagent without persisted messages.");

            default:
                throw new InvalidOperationException($"Unknown run mode {task.RunMode}.");
        }
    }

    private const string ContextQueryPrompt = """
        You are preparing context for a worker who will execute a task autonomously. The worker has
        access to tools (bash, python, file operations, memory search) but starts with NO context
        about the user, project, or environment. Your job is to generate precise search queries
        that will retrieve the most useful context from the assistant's long-term memory.

        Think carefully about what the worker needs to succeed. Cover both:

        **Facts, configurations, and preferences:**
        - Who is the user? What are their preferences for this type of work?
        - What project/codebase/environment is this task about?
        - Are there specific tools, APIs, accounts, or configurations already set up?
        - Has the user expressed preferences about HOW this kind of work should be done?
        - Are there related past decisions or constraints the worker should respect?

        **Techniques, approaches, and lessons learned:**
        - Has a similar task been done before? What approach worked?
        - Are there known pitfalls, workarounds, or best practices for this type of work?
        - What problem-solving strategies or workflows apply here?

        Write each query as a natural phrase (5-20 words) that would match relevant memory content
        via semantic similarity. Be specific — "user's email notification preferences" is better
        than "email settings". Think about what you'd want to know if YOU were about to do this task
        with no prior context.

        Generate 3-5 queries total covering both categories. If the task is trivial and unlikely
        to benefit from context, return an empty array.

        Respond with exactly one JSON object:
        {"queries": ["...", "...", "..."]}
        """;

    /// <summary>
    /// Uses an LLM call to generate targeted search queries, then retrieves relevant
    /// memories from the agent's long-term store for the subagent's task.
    /// </summary>
    private async Task<string> RetrieveSubagentContextAsync(
        string taskPrompt, CancellationToken cancellationToken)
    {
        if (this.memoryService is null)
        {
            return string.Empty;
        }

        var memories = string.Empty;

        try
        {
            // Step 1: LLM generates targeted search queries
            var queries = await this.GenerateContextQueriesAsync(
                taskPrompt, cancellationToken).ConfigureAwait(false);

            // Step 2: Execute memory searches across the entire memory store (no tag filter)
            var memoryCandidates = await this.SearchMemoriesAsync(
                queries, MaxMemoryResults, MinMemoryScore, tags: null, cancellationToken).ConfigureAwait(false);

            // Log candidates before filtering
            if (memoryCandidates.Count > 0 && this.logger.IsEnabled(LogLevel.Information))
            {
                var items = string.Join(" | ", memoryCandidates.Select(r => r.Content?[..Math.Min(r.Content.Length, 80)]));
                this.LogSubAgentMemoryCandidates(memoryCandidates.Count, items);
            }

            // Step 3: LLM evaluates which candidates are actually relevant
            if (memoryCandidates.Count > 0)
            {
                var filteredMemories = await this.FilterRelevantContextAsync(
                    taskPrompt, memoryCandidates, cancellationToken).ConfigureAwait(false);

                if (filteredMemories.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var m in filteredMemories)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"- {m}");
                    }

                    memories = sb.ToString();
                    this.LogSubAgentMemoriesRetrieved(filteredMemories.Count);
                }
            }
        }
#pragma warning disable CA1031 // Context retrieval must never prevent subagent from starting
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogSubAgentContextRetrievalFailed(ex.Message);
        }

        return memories;
    }

    /// <summary>Execute search queries against the memory store, deduplicating by content.</summary>
    private async Task<List<MemoryMcp.Core.Models.SearchResult>> SearchMemoriesAsync(
        List<string> queries, int maxResults, float minScore,
        List<string>? tags, CancellationToken cancellationToken)
    {
        if (queries.Count == 0 || this.memoryService is null)
        {
            return [];
        }

        var allResults = new Dictionary<string, MemoryMcp.Core.Models.SearchResult>(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            var results = await this.memoryService.SearchAsync(
                query, 3, minScore, tags, cancellationToken).ConfigureAwait(false);

            foreach (var r in results)
            {
                allResults.TryAdd(r.Content ?? string.Empty, r);
            }
        }

        return allResults.Values
            .OrderByDescending(r => r.Score)
            .Take(maxResults)
            .ToList();
    }

    private const string FilterPrompt = """
        You are selecting context for a worker who will execute a task autonomously.
        The worker starts with no knowledge about the user, project, or environment.

        For each candidate, ask: "Could this help the worker do a better job?"
        When in doubt, include it — some context is better than none.

        Drop items that are:
        - Clearly about a completely unrelated topic
        - Duplicating information already present in another item
        - Too generic to be useful (e.g., "write clean code")

        Keep items that:
        - Tell something about the user (preferences, habits, who they are)
        - Provide actionable context (tool configs, API keys, established patterns)
        - Contain techniques or lessons learned from similar past work
        - Name specific files, services, or conventions relevant to the task

        Respond with JSON:
        {
          "relevant_memories": ["exact text of each relevant memory item"]
        }

        Preserve the exact text of kept items. Return an empty array only if nothing is relevant.
        """;

    /// <summary>
    /// Calls the LLM to filter retrieved memory candidates,
    /// keeping only items genuinely relevant to the task.
    /// </summary>
    private async Task<List<string>> FilterRelevantContextAsync(
        string taskPrompt,
        List<MemoryMcp.Core.Models.SearchResult> memoryCandidates,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Task: {taskPrompt}");

        if (memoryCandidates.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Memory candidates:");
            foreach (var r in memoryCandidates)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {r.Content}");
            }
        }

        var filterInput = sb.ToString();
        this.LogSubAgentFilterInput(filterInput);

        var request = new LlmCompletionRequest
        {
            Model = this.modelProvider.DefaultModel,
            Messages =
            [
                new LlmMessage { Role = "system", Content = FilterPrompt },
                new LlmMessage { Role = "user", Content = sb.ToString() },
            ],
            Temperature = 0.0,
            MaxTokens = TokenLimits.ResolveMaxOutput(this.modelProvider),
            RequestId = Guid.NewGuid().ToString("N"),
            ConversationId = "subagent-context-filter",
        };

        var result = await this.llmClient.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Content))
        {
            this.LogSubAgentFilterFailed(result.ErrorMessage ?? "empty response");
            // Fall back to unfiltered — better to have noisy context than none
            return memoryCandidates.Select(r => r.Content ?? string.Empty).ToList();
        }

        try
        {
            var json = Memory.MemoryConsolidationService.StripToJson(result.Content);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var filteredMemories = root.TryGetProperty("relevant_memories", out var rm)
                ? rm.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList()
                : [];

            this.LogSubAgentContextFiltered(memoryCandidates.Count, filteredMemories.Count);

            return filteredMemories;
        }
#pragma warning disable CA1031 // Malformed LLM output — fall back to unfiltered
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogSubAgentFilterParseFailed(ex.Message);
            return memoryCandidates.Select(r => r.Content ?? string.Empty).ToList();
        }
    }

    /// <summary>
    /// Calls the LLM to generate targeted search queries for retrieving
    /// relevant memories for the subagent's task.
    /// </summary>
    private async Task<List<string>> GenerateContextQueriesAsync(
        string taskPrompt, CancellationToken cancellationToken)
    {
        var request = new LlmCompletionRequest
        {
            Model = this.modelProvider.DefaultModel,
            Messages =
            [
                new LlmMessage { Role = "system", Content = ContextQueryPrompt },
                new LlmMessage { Role = "user", Content = $"Task:\n{taskPrompt}" },
            ],
            Temperature = 0.0,
            MaxTokens = TokenLimits.ResolveMaxOutput(this.modelProvider),
            RequestId = Guid.NewGuid().ToString("N"),
            ConversationId = "subagent-context-query",
        };

        var result = await this.llmClient.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Content))
        {
            this.LogSubAgentQueryGenerationFailed(result.ErrorMessage ?? "empty response");
            return [];
        }

        try
        {
            var json = Memory.MemoryConsolidationService.StripToJson(result.Content);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var queries = root.TryGetProperty("queries", out var q)
                ? q.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList()
                : [];

            this.LogSubAgentQueriesGenerated(queries.Count);
            return queries;
        }
#pragma warning disable CA1031 // Malformed LLM output should not crash
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogSubAgentQueryParseFailed(ex.Message);
            return [];
        }
    }

    private string? LoadBootstrapContext()
    {
        try
        {
            if (File.Exists(this.bootstrapContextPath))
            {
                var content = File.ReadAllText(this.bootstrapContextPath).Trim();
                return content.Length > 0 ? content : null;
            }
        }
#pragma warning disable CA1031 // Bootstrap is optional context, never crash
        catch { /* ignore */ }
#pragma warning restore CA1031
        return null;
    }

    private string BuildSubagentSystemPrompt(string memories, string? bootstrapContext, string? skillName = null)
    {
        var config = this.systemPromptStore?.Read() ?? Cortex.Contained.Contracts.SystemPrompt.SystemPromptDefaults.Create();

        var skillValue = string.Empty;
        if (skillName is not null && this.skillRegistry is not null)
        {
            var skillContent = this.skillRegistry.ReadSkillContent(skillName);
            if (skillContent is not null)
            {
                skillValue = $"## Skill: {skillName}\n\n{skillContent}\n\n";
            }
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["personality"] = string.Empty,
            ["skill"] = skillValue,
            ["instructions"] = config.SubagentInstructions,
            ["skills"] = this.skillRegistry?.FormatForSystemPrompt() ?? string.Empty,
            ["bootstrap_context"] = string.IsNullOrWhiteSpace(bootstrapContext)
                ? string.Empty
                : $"\n## User context\n{bootstrapContext}",
            ["recalled_memories"] = string.IsNullOrWhiteSpace(memories)
                ? string.Empty
                : $"\n## Recalled context\n{memories}",
        };

        return SystemPromptRenderer.Render(config.SubagentTemplate, values);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-exec] Retrieved {Count} memories for subagent context")]
    private partial void LogSubAgentMemoriesRetrieved(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent-exec] Context retrieval failed: {ErrorMessage}")]
    private partial void LogSubAgentContextRetrievalFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-exec] Generated {Count} context queries")]
    private partial void LogSubAgentQueriesGenerated(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent-exec] Query generation failed: {ErrorMessage}")]
    private partial void LogSubAgentQueryGenerationFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent-exec] Query parse failed: {ErrorMessage}")]
    private partial void LogSubAgentQueryParseFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-exec] Context filtered: {Candidates}->{Kept} memories")]
    private partial void LogSubAgentContextFiltered(int candidates, int kept);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent-exec] Context filter failed: {ErrorMessage}")]
    private partial void LogSubAgentFilterFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent-exec] Context filter parse failed: {ErrorMessage}")]
    private partial void LogSubAgentFilterParseFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[subagent-exec] Filter input:\n{FilterInput}")]
    private partial void LogSubAgentFilterInput(string filterInput);

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-exec] Memory candidates ({Count}): {Items}")]
    private partial void LogSubAgentMemoryCandidates(int count, string items);
}
