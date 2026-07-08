using System.Globalization;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Spawns an async subagent that runs in the background. Returns a task_id immediately
/// so the main agent can respond to the user without waiting. The subagent will
/// deliver its results via <see cref="AgentRuntime.ProcessSubagentCompletionAsync"/>.
/// </summary>
public sealed partial class SubAgentStartTool : IAgentTool
{
    private readonly ILlmClient llmClient;
    private readonly Func<ToolRegistry> toolRegistryFactory;
    private readonly IModelProvider modelProvider;
    private readonly IOptionsMonitor<AgentConfig> agentConfig;
    private readonly SubagentSessionStore store;
    private readonly SubagentRunnerRegistry registry;
    private readonly Func<string, string, Task> onCompletion;
    private readonly IMemoryService? memoryService;
    private readonly InMemoryTodoStore? todoStore;
    private readonly SkillRegistry? skillRegistry;
    private readonly string bootstrapContextPath;
    private readonly ILogger<SubAgentStartTool> logger;
    private readonly IOptionsMonitor<ImageAgingConfig>? imageAgingOptions;
    private readonly IImageDescriber? imageDescriber;
    private readonly SystemPromptStore? systemPromptStore;

    /// <summary>Maximum memories to inject into subagent context.</summary>
    private const int MaxMemoryResults = 6;

    /// <summary>Minimum similarity score for memory results.</summary>
    private const float MinMemoryScore = 0.3f;

    public SubAgentStartTool(
        ILlmClient llmClient,
        Func<ToolRegistry> toolRegistryFactory,
        IModelProvider modelProvider,
        IOptionsMonitor<AgentConfig> agentConfig,
        SubagentSessionStore store,
        SubagentRunnerRegistry registry,
        Func<string, string, Task> onCompletion,
        ILogger<SubAgentStartTool> logger,
        string stateRoot,
        IMemoryService? memoryService = null,
        InMemoryTodoStore? todoStore = null,
        SkillRegistry? skillRegistry = null,
        IOptionsMonitor<ImageAgingConfig>? imageAgingOptions = null,
        IImageDescriber? imageDescriber = null,
        SystemPromptStore? systemPromptStore = null)
    {
        this.llmClient = llmClient;
        this.toolRegistryFactory = toolRegistryFactory;
        this.modelProvider = modelProvider;
        this.agentConfig = agentConfig;
        this.store = store;
        this.registry = registry;
        this.registry.SetSlotsOpenedCallback(this.StartQueuedTasks);
        this.onCompletion = onCompletion;
        this.logger = logger;
        this.bootstrapContextPath = Path.Combine(stateRoot, "context-bootstrap.md");
        this.memoryService = memoryService;
        this.todoStore = todoStore;
        this.skillRegistry = skillRegistry;
        this.imageAgingOptions = imageAgingOptions;
        this.imageDescriber = imageDescriber;
        this.systemPromptStore = systemPromptStore;
    }

    public string Name => "sub_agent_start";

    public string Description =>
        "Spawn an async subagent to perform a multi-step task in the background. " +
        "Returns a task_id immediately. When the task completes, you will receive a " +
        "[Background task completed] message with the results to review. " +
        "In the prompt, describe the task and tell to respond with results. " +
        "Never ask the subagent to send, deliver, or message the user. " +
        "Use this for complex, multi-step work that would require many tool calls " +
        "(e.g., researching across multiple files, writing and revising a document, " +
        "performing a series of file operations). " +
        "Do NOT use for simple tasks that need only 1-2 tool calls — do those directly. " +
        "Use sub_agent_read to check status, sub_agent_send to provide additional input.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "description": {
              "type": "string",
              "description": "A short (3-5 words) description of the task"
            },
            "prompt": {
              "type": "string",
              "description": "Detailed instructions for the subagent. Be specific about what to do and what to return."
            },
            "skill": {
              "type": "string",
              "description": "Optional skill name. The skill's SKILL.md content is prepended to the subagent's system prompt for structured guidance."
            }
          },
          "required": ["description", "prompt"]
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(
        string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string description;
        string prompt;

        string? skillName;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            description = root.GetProperty("description").GetString() ?? string.Empty;
            prompt = root.GetProperty("prompt").GetString() ?? string.Empty;
            skillName = root.TryGetProperty("skill", out var skillProp)
                ? skillProp.GetString()
                : null;
        }
#pragma warning disable CA1031 // Bad arguments should not crash the agent
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Task.FromResult(AgentToolResult.Fail($"Invalid arguments: {ex.Message}"));
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Task.FromResult(AgentToolResult.Fail("Missing required parameter: prompt"));
        }

        var taskId = string.Create(CultureInfo.InvariantCulture, $"sa-{Guid.NewGuid():N}");

        // Create the task record
        var task = new SubagentTask
        {
            TaskId = taskId,
            ParentConversation = context.ConversationId,
            ParentChannel = context.ChannelId,
            Description = description,
            Prompt = prompt,
            State = SubagentTaskState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        this.store.Create(task);

        // Try to acquire a concurrency slot
        if (this.registry.HasAvailableSlot)
        {
            this.store.UpdateState(taskId, SubagentTaskState.Running);
            FireRunner(taskId, description, prompt, skillName, context.ConversationId, cancellationToken);

            this.LogSubAgentStarted(taskId, description);
            return Task.FromResult(AgentToolResult.Ok($"Subagent started.\n" +
                          $"Task ID: {taskId}\n" +
                          $"Status: running\n\n" +
                          $"You will receive a [Background task completed] message when it finishes. " +
                          $"Use sub_agent_read('{taskId}') to check progress."));
        }

        this.LogSubAgentQueued(taskId, description);
        return Task.FromResult(AgentToolResult.Ok($"Subagent queued (all concurrency slots in use).\n" +
                      $"Task ID: {taskId}\n" +
                      $"Status: queued\n\n" +
                      $"It will start automatically when a slot opens. " +
                      $"Use sub_agent_read('{taskId}') to check status."));
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
            var queries = await GenerateContextQueriesAsync(
                taskPrompt, cancellationToken).ConfigureAwait(false);

            // Step 2: Execute memory searches across the entire memory store (no tag filter)
            var memoryCandidates = await SearchMemoriesAsync(
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
                var filteredMemories = await FilterRelevantContextAsync(
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

    /// <summary>Fire a SubagentRunner on a background task.</summary>
    internal void FireRunner(string taskId, string description, string prompt, string? skillName, string conversationId, CancellationToken cancellationToken)
    {
        var maxRounds = this.agentConfig.CurrentValue.MaxSubagentRounds;
        var runner = new SubagentRunner(
            this.llmClient, this.toolRegistryFactory(), maxRounds, this.logger,
            this.store, taskId, OnRunnerCompletedAsync, this.modelProvider, this.todoStore,
            this.imageAgingOptions, this.imageDescriber);

        if (!this.registry.TryRegister(taskId, runner))
        {
            this.store.UpdateState(taskId, SubagentTaskState.Queued);
            this.LogSubAgentQueued(taskId, description);
            return;
        }

        var token = this.registry.GetCancellationToken(taskId);

        _ = Task.Run(async () =>
        {
            try
            {
                // Retrieve relevant context before starting the loop
                var memories = await RetrieveSubagentContextAsync(
                    prompt, token).ConfigureAwait(false);

                var bootstrapContext = LoadBootstrapContext();
                var systemPrompt = BuildSubagentSystemPrompt(memories, bootstrapContext, skillName);

                await runner.RunAsync(
                    this.modelProvider.DefaultModel, systemPrompt, prompt,
                    $"subagent-{taskId}", token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                this.LogSubAgentCancelled(taskId);
                this.store.UpdateState(taskId, SubagentTaskState.Cancelled, result: "[Subagent stopped]");
            }
#pragma warning disable CA1031 // Background task must not crash the process
            catch (Exception ex)
#pragma warning restore CA1031
            {
                this.LogSubAgentCrashed(taskId, ex.Message);
                this.store.UpdateState(taskId, SubagentTaskState.Failed, result: $"[Subagent crashed: {ex.Message}]");
            }
            finally
            {
                // Remove frees the slot (count decreases). Always safe to call.
                this.registry.Remove(taskId);

                // On stop/crash: notify main agent and dequeue next task.
                var task = this.store.GetById(taskId);
                if (task?.State is SubagentTaskState.Failed or SubagentTaskState.Cancelled)
                {
                    try
                    {
                        await this.onCompletion(taskId, task.Result ?? "[Subagent stopped]").ConfigureAwait(false);
                    }
#pragma warning disable CA1031
                    catch { /* must not throw */ }
#pragma warning restore CA1031

                    DequeueNext();
                }
            }
        }, CancellationToken.None); // Task.Run lifetime is independent; cancellation is via the per-task token.
    }

    private async Task OnRunnerCompletedAsync(string taskId, string result)
    {
        // Remove frees the slot (count decreases)
        this.registry.Remove(taskId);

        await this.onCompletion(taskId, result).ConfigureAwait(false);

        DequeueNext();
    }

    /// <summary>
    /// Start any queued tasks that survived a container restart.
    /// Called once during startup after crash recovery.
    /// </summary>
    public void StartQueuedTasks()
    {
        while (this.registry.HasAvailableSlot)
        {
            var next = this.store.TryClaimOldestQueued();
            if (next is null)
            {
                break;
            }

            this.LogSubAgentDequeued(next.TaskId, next.Description);
            FireRunner(next.TaskId, next.Description, next.Prompt, skillName: null, next.ParentConversation, CancellationToken.None);
        }
    }

    private void DequeueNext()
    {
        if (!this.registry.HasAvailableSlot)
        {
            return;
        }

        var next = this.store.TryClaimOldestQueued();
        if (next is not null)
        {
            this.LogSubAgentDequeued(next.TaskId, next.Description);
            FireRunner(next.TaskId, next.Description, next.Prompt, skillName: null, next.ParentConversation, CancellationToken.None);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_start] Started: {TaskId} — {Description}")]
    private partial void LogSubAgentStarted(string taskId, string description);

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_start] Queued: {TaskId} — {Description}")]
    private partial void LogSubAgentQueued(string taskId, string description);

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_start] Dequeued: {TaskId} — {Description}")]
    private partial void LogSubAgentDequeued(string taskId, string description);

    [LoggerMessage(Level = LogLevel.Error, Message = "[sub_agent_start] Subagent crashed: {TaskId} — {ErrorMessage}")]
    private partial void LogSubAgentCrashed(string taskId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_start] Subagent stopped: {TaskId}")]
    private partial void LogSubAgentCancelled(string taskId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_start] Retrieved {Count} memories for subagent context")]
    private partial void LogSubAgentMemoriesRetrieved(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[sub_agent_start] Context retrieval failed: {ErrorMessage}")]
    private partial void LogSubAgentContextRetrievalFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_start] Generated {Count} context queries")]
    private partial void LogSubAgentQueriesGenerated(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[sub_agent_start] Query generation failed: {ErrorMessage}")]
    private partial void LogSubAgentQueryGenerationFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[sub_agent_start] Query parse failed: {ErrorMessage}")]
    private partial void LogSubAgentQueryParseFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_start] Context filtered: {Candidates}→{Kept} memories")]
    private partial void LogSubAgentContextFiltered(int candidates, int kept);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[sub_agent_start] Context filter failed: {ErrorMessage}")]
    private partial void LogSubAgentFilterFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[sub_agent_start] Context filter parse failed: {ErrorMessage}")]
    private partial void LogSubAgentFilterParseFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[sub_agent_start] Filter input:\n{FilterInput}")]
    private partial void LogSubAgentFilterInput(string filterInput);

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_start] Memory candidates ({Count}): {Items}")]
    private partial void LogSubAgentMemoryCandidates(int count, string items);
}
