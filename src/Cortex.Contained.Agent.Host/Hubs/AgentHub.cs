using System.Globalization;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Scheduler;
using MemoryMcp.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary>
/// SignalR hub that the Bridge connects to. Dispatches messages to the agent runtime
/// and streams responses back. The Bridge pushes LLM credentials so the agent
/// can call providers directly.
/// </summary>
[Authorize]
public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
{
    private readonly IAgentRuntime runtime;
    private readonly DirectLlmClient llmClient;
    private readonly BridgeClientAccessor bridgeClientAccessor;
    private readonly IMemoryManagementService memoryService;
    private readonly MemorySettingsStore memorySettingsStore;
    private readonly SelfNotesStore selfNotesStore;
    private readonly IOptionsMonitor<MemoryMcpOptions> memoryMcpOptions;
    private readonly IOptionsMonitor<MemoryCompactionOptions> compactionOptions;
    private readonly IOptionsMonitor<ConversationCompactionConfig> conversationCompactionOptions;
    private readonly MemoryCompactionService compactionService;
    private readonly Storage.MessageStore messageStore;
    private readonly HistoryPurgeService historyPurge;
    private readonly MemoryQuarantine memoryQuarantine;
    private readonly HistoryToolsSwitch historyTools;
    private readonly SchedulerService scheduler;
    private readonly CodingAgentEventBus externalAgentBus;
    private readonly Cortex.Contained.Speech.SpeakerId.IVoiceprintStore voiceprintStore;
    private readonly Cortex.Contained.Agent.Host.SpeakerId.SpeakerIdSettingsStore speakerIdSettingsStore;
    private readonly Cortex.Contained.Agent.Host.SpeakerId.EnrollmentOrchestrator? enrollmentOrchestrator;
    private readonly Cortex.Contained.Agent.Host.Events.KnownSecrets? knownSecrets;
    private readonly Cortex.Contained.Agent.Host.Events.EventBackfillService? eventBackfill;
    private readonly Storage.StorageKeyRing? storageKeys;
    private readonly Cortex.Contained.Agent.Host.Reranking.RerankerAccess? rerankerAccess;
    private readonly Cortex.Contained.Agent.Host.Recall.RecallControlService? recall;
    private readonly Storage.StorageEncryptionService? storageEncryption;
    private readonly Cortex.Contained.Agent.Host.Evaluation.EvaluationJobService? evaluationJobs;
    private readonly Cortex.Contained.Agent.Host.Evaluation.EvaluationFiles? evaluationFiles;
    private readonly Cortex.Contained.Agent.Host.Mcp.McpToolStore mcpToolStore;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly AgentMetrics metrics;
    private readonly SubagentExecutionCoordinator subagentCoordinator;
    private readonly SubagentObservabilityService subagentObservability;
    private readonly ILogger<AgentHub> logger;

    public AgentHub(
        IAgentRuntime runtime,
        DirectLlmClient llmClient,
        BridgeClientAccessor bridgeClientAccessor,
        IMemoryManagementService memoryService,
        MemorySettingsStore memorySettingsStore,
        SelfNotesStore selfNotesStore,
        IOptionsMonitor<MemoryMcpOptions> memoryMcpOptions,
        IOptionsMonitor<MemoryCompactionOptions> compactionOptions,
        IOptionsMonitor<ConversationCompactionConfig> conversationCompactionOptions,
        MemoryCompactionService compactionService,
        Storage.MessageStore messageStore,
        HistoryPurgeService historyPurge,
        MemoryQuarantine memoryQuarantine,
        HistoryToolsSwitch historyTools,
        SchedulerService scheduler,
        CodingAgentEventBus externalAgentBus,
        Cortex.Contained.Speech.SpeakerId.IVoiceprintStore voiceprintStore,
        Cortex.Contained.Agent.Host.SpeakerId.SpeakerIdSettingsStore speakerIdSettingsStore,
        Cortex.Contained.Agent.Host.Mcp.McpToolStore mcpToolStore,
        IHttpClientFactory httpClientFactory,
        AgentMetrics metrics,
        SubagentExecutionCoordinator subagentCoordinator,
        SubagentObservabilityService subagentObservability,
        ILogger<AgentHub> logger,
        Cortex.Contained.Agent.Host.SpeakerId.EnrollmentOrchestrator? enrollmentOrchestrator = null,
        Cortex.Contained.Agent.Host.Events.KnownSecrets? knownSecrets = null,
        Cortex.Contained.Agent.Host.Events.EventBackfillService? eventBackfill = null,
        Storage.StorageKeyRing? storageKeys = null,
        Cortex.Contained.Agent.Host.Reranking.RerankerAccess? rerankerAccess = null,
        Cortex.Contained.Agent.Host.Recall.RecallControlService? recall = null,
        Storage.StorageEncryptionService? storageEncryption = null,
        Cortex.Contained.Agent.Host.Evaluation.EvaluationJobService? evaluationJobs = null,
        Cortex.Contained.Agent.Host.Evaluation.EvaluationFiles? evaluationFiles = null)
    {
        this.runtime = runtime;
        this.llmClient = llmClient;
        this.bridgeClientAccessor = bridgeClientAccessor;
        this.memoryService = memoryService;
        this.memorySettingsStore = memorySettingsStore;
        this.selfNotesStore = selfNotesStore;
        this.memoryMcpOptions = memoryMcpOptions;
        this.compactionOptions = compactionOptions;
        this.conversationCompactionOptions = conversationCompactionOptions;
        this.compactionService = compactionService;
        this.messageStore = messageStore;
        this.historyPurge = historyPurge;
        this.memoryQuarantine = memoryQuarantine;
        this.historyTools = historyTools;
        this.scheduler = scheduler;
        this.externalAgentBus = externalAgentBus;
        this.voiceprintStore = voiceprintStore;
        this.speakerIdSettingsStore = speakerIdSettingsStore;
        this.enrollmentOrchestrator = enrollmentOrchestrator;
        this.knownSecrets = knownSecrets;
        this.eventBackfill = eventBackfill;
        this.storageKeys = storageKeys;
        this.rerankerAccess = rerankerAccess;
        this.recall = recall;
        this.storageEncryption = storageEncryption;
        this.evaluationJobs = evaluationJobs;
        this.evaluationFiles = evaluationFiles;
        this.mcpToolStore = mcpToolStore;
        this.httpClientFactory = httpClientFactory;
        this.metrics = metrics;
        this.subagentCoordinator = subagentCoordinator;
        this.subagentObservability = subagentObservability;
        this.logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var existing = this.bridgeClientAccessor.CurrentConnectionId;
        if (existing is not null && !string.Equals(existing, Context.ConnectionId, StringComparison.Ordinal))
        {
            this.LogBridgeConnectionRejected(Context.ConnectionId, existing);
            Context.Abort();
            return;
        }

        this.LogBridgeConnected(Context.ConnectionId);
        this.bridgeClientAccessor.SetConnectionId(Context.ConnectionId);

        // Readiness gate: the Bridge is connected, but this signal also RESETS credential
        // and MCP-catalog readiness — the new connection must push both before queued or
        // recovered subagent work is allowed to dispatch.
        this.subagentCoordinator.OnBridgeConnected();

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        this.LogBridgeDisconnected(Context.ConnectionId, exception?.Message);

        // Close the dispatch/recovery gate only when the ACTIVE bridge connection drops —
        // a rejected duplicate connection's disconnect must not stall the live bridge.
        var wasActiveBridge = string.Equals(
            this.bridgeClientAccessor.CurrentConnectionId, Context.ConnectionId, StringComparison.Ordinal);
        this.bridgeClientAccessor.ClearConnection(Context.ConnectionId);
        if (wasActiveBridge)
        {
            this.subagentCoordinator.OnBridgeDisconnected();
        }

        return base.OnDisconnectedAsync(exception);
    }

    /// <inheritdoc />
    public async Task<SendMessageResult> SendMessage(HubInboundMessage message)
    {
        var correlationId = message.CorrelationId ?? Guid.NewGuid().ToString("N");

        using (this.logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["ConversationId"] = message.ConversationId,
        }))
        {
            this.LogMessageReceived(message.ConversationId);

            try
            {
                // Ensure the message carries a correlation ID into the runtime
                var enrichedMessage = message.CorrelationId is null
                    ? message with { CorrelationId = correlationId }
                    : message;

                return await this.runtime.HandleMessageAsync(enrichedMessage, Context.ConnectionAborted)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.LogMessageError(message.ConversationId, ex.Message);
                return new SendMessageResult
                {
                    Accepted = false,
                    RejectionReason = $"Internal error: {ex.Message}",
                };
            }
        }
    }

    /// <inheritdoc />
    public Task AbortGeneration(string conversationId)
    {
        this.LogAbortRequested(conversationId);
        return this.runtime.AbortGenerationAsync(conversationId);
    }

    /// <inheritdoc />
    public Task<AgentStatusInfo> GetStatus()
    {
        return this.runtime.GetStatusAsync(Context.ConnectionAborted);
    }

    /// <inheritdoc />
    public Task UpdateConfig(AgentConfigUpdate config)
    {
        this.LogConfigUpdate();
        return this.runtime.UpdateConfigAsync(config, Context.ConnectionAborted);
    }

    /// <inheritdoc />
    public Task<bool> ProvideStorageKey(StorageKeyGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var outcome = this.storageKeys?.Provide(grant.Key) ?? Storage.StorageKeyAcceptance.Rejected;
        this.LogStorageKeyReceived(outcome);
        return Task.FromResult(outcome != Storage.StorageKeyAcceptance.Rejected);
    }

    /// <inheritdoc />
    public Task ProvideRerankerGrant(RerankerGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        this.rerankerAccess?.Apply(grant);
        var hasKey = !string.IsNullOrWhiteSpace(grant.ApiKey);
        this.LogRerankerGrantReceived(grant.Enabled, hasKey);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ProvideCredentials(LlmCredentials credentials)
    {
        this.LogCredentialsReceived(credentials.Providers.Count);

        // The pushed credentials are the known-secret set that persistence redaction replaces exactly.
        this.knownSecrets?.Load(credentials);

        // ConfigureCredentials must be called BEFORE wiring the callback so that any
        // Anthropic OAuth ProviderState objects exist when the callback fires.
        var result = this.llmClient.ConfigureCredentials(credentials);

        // Wire up the token-refresh request callback, capturing this connection's caller.
        // When DirectLlmClient detects an expiring Anthropic OAuth token it invokes this,
        // which signals the Bridge to refresh and return fresh tokens directly via
        // SignalR Client Results — avoiding the deadlock that occurs when the Bridge
        // tries to push credentials as a separate hub method while SendMessage is running.
        var caller = Clients.Caller;
        this.llmClient.SetRequestTokenRefreshCallback(async providerName =>
        {
            try
            {
                return await caller.OnTokenRefreshRequested(providerName).ConfigureAwait(false);
            }
            catch
            {
                return new TokenRefreshResult { Success = false, Error = "Bridge connection lost" };
            }
        });

        this.llmClient.SetRequestTokenReloadCallback(async providerName =>
        {
            try
            {
                return await caller.OnTokenReloadRequested(providerName).ConfigureAwait(false);
            }
            catch
            {
                return new TokenRefreshResult { Success = false, Error = "Bridge connection lost" };
            }
        });

        // Update runtime with the default model and its limits derived from the first provider
        if (result.DefaultModel is not null)
        {
            this.runtime.SetDefaultModel(result.DefaultModel, result.ContextWindow, result.MaxOutputTokens);
        }

        // Set the memory model (may be null, in which case the default model is used)
        this.runtime.SetMemoryModel(result.MemoryModel);

        // Signal readiness AFTER the credentials are applied so recovered subagent work
        // never dispatches into a credential-less LLM client. An empty push marks NOT ready.
        this.subagentCoordinator.MarkCredentialsReady(this.llmClient.HasCredentials);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<HealthInfo> Ping()
    {
        return Task.FromResult(new HealthInfo
        {
            Healthy = true,
            Timestamp = DateTimeOffset.UtcNow,
            Version = typeof(AgentHub).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Metrics = this.metrics.Snapshot(),
        });
    }

    /// <inheritdoc />
    public Task<string> GetPersonality()
    {
        return this.runtime.GetPersonalityAsync(Context.ConnectionAborted);
    }

    /// <inheritdoc />
    public Task SetPersonality(string personality)
    {
        return this.runtime.SetPersonalityAsync(personality, Context.ConnectionAborted);
    }

    /// <inheritdoc />
    public Task<string> GetSelfNotes()
    {
        return Task.FromResult(this.selfNotesStore.Read());
    }

    /// <inheritdoc />
    public Task SetSelfNotes(string content)
    {
        this.selfNotesStore.Write(content);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string> ResetSelfNotes()
    {
        this.selfNotesStore.Write(SelfNotesStore.DefaultContent);
        return Task.FromResult(SelfNotesStore.DefaultContent);
    }

    /// <inheritdoc />
    public Task<Contracts.SystemPrompt.SystemPromptConfig> GetSystemPromptConfig()
    {
        return this.runtime.GetSystemPromptConfigAsync(Context.ConnectionAborted);
    }

    /// <inheritdoc />
    public Task<Contracts.SystemPrompt.SystemPromptValidationResult> SetSystemPromptConfig(Contracts.SystemPrompt.SystemPromptConfig config)
    {
        return this.runtime.SetSystemPromptConfigAsync(config, Context.ConnectionAborted);
    }

    /// <inheritdoc />
    public Task<Contracts.SystemPrompt.SystemPromptConfig> ResetSystemPromptConfig()
    {
        return this.runtime.ResetSystemPromptConfigAsync(Context.ConnectionAborted);
    }

    /// <inheritdoc />
    public Task<string> GetSystemPromptPreview(string channelId, bool isVoice)
    {
        return this.runtime.GetSystemPromptPreviewAsync(channelId, isVoice, Context.ConnectionAborted);
    }

    /// <inheritdoc />
    public Task SeedHistory(string channelId, HubChatMessage[] messages)
    {
        this.LogSeedHistory(channelId, messages.Length);
        return this.runtime.SeedHistoryAsync(channelId, messages, Context.ConnectionAborted);
    }

    /// <inheritdoc />
    public Task ResetSession(string channelId)
    {
        this.LogResetSession(channelId);
        return this.runtime.ResetSessionAsync(channelId, Context.ConnectionAborted);
    }

    /// <inheritdoc />
    public Task ResetAllSessions()
    {
        this.LogResetAllSessions();
        return this.runtime.ResetAllSessionsAsync(Context.ConnectionAborted);
    }

    /// <inheritdoc />
    public Task OnTurnInterrupted(TurnInterruptedNotification notification)
        => this.runtime.RecordInterruptedAssistantTurnAsync(
            notification.ConversationId, notification.PlayedText);

    /// <inheritdoc />
    public Task SetActiveChannels(string[] channelIds)
    {
        this.LogActiveChannelsReceived(channelIds.Length);
        this.runtime.SetActiveChannels(channelIds);
        return Task.CompletedTask;
    }

    // --- History Management ---

    /// <inheritdoc />
    public async Task<ConversationListResult> GetConversations(string? channelId, int limit, int offset)
    {
        var conversations = await this.messageStore.GetConversationsAsync(channelId, limit, offset, Context.ConnectionAborted)
            .ConfigureAwait(false);

        var totalCount = await this.messageStore.GetConversationCountAsync(channelId, Context.ConnectionAborted)
            .ConfigureAwait(false);

        return new ConversationListResult
        {
            Conversations = conversations.Select(c => new ConversationSummaryDto
            {
                ConversationId = c.ConversationId,
                ChannelId = c.ChannelId,
                Title = c.Title,
                MessageCount = c.MessageCount,
                LastMessageAt = c.LastMessageAt,
            }).ToList(),
            TotalCount = (int)totalCount,
        };
    }

    /// <inheritdoc />
    public async Task<MessageListResult> GetMessages(string conversationId, int limit, int offset)
    {
        // Fetch (limit+offset) newest messages. The store returns them in chronological
        // (oldest-first) order, so the NEWEST are at the end of the list.
        // offset=0 => page is the newest `limit` messages (end of list)
        // offset=limit => page is the next `limit` older messages
        var messages = await this.messageStore.GetMessagesAsync(
            conversationId, limit + offset,
            visibility: Storage.MessageVisibility.History,
            cancellationToken: Context.ConnectionAborted).ConfigureAwait(false);

        // Take the slice ending `offset` from the end, of size `limit`.
        var windowEnd = messages.Count - offset;
        var windowStart = Math.Max(0, windowEnd - limit);
        var page = windowEnd <= 0
            ? new List<Storage.MessageRecord>()
            : messages.GetRange(windowStart, windowEnd - windowStart);

        var totalCount = await this.messageStore.GetMessageCountAsync(
            conversationId, Context.ConnectionAborted).ConfigureAwait(false);

        return new MessageListResult
        {
            Messages = page.Select(m => new MessageEntryDto
            {
                MessageId = m.MessageId ?? m.Id.ToString(CultureInfo.InvariantCulture),
                Role = m.Role,
                Content = m.Content,
                Timestamp = m.Timestamp,
                ChannelId = m.ChannelId,
                Category = m.Category,
            }).ToList(),
            TotalCount = (int)totalCount,
        };
    }

    /// <inheritdoc />
    public async Task<MessageListResult> SearchMessages(string query, int limit)
    {
        var messages = await this.messageStore.SearchMessagesAsync(query, limit, Context.ConnectionAborted)
            .ConfigureAwait(false);

        return new MessageListResult
        {
            Messages = messages.Select(m => new MessageEntryDto
            {
                MessageId = m.MessageId ?? m.Id.ToString(CultureInfo.InvariantCulture),
                Role = m.Role,
                Content = m.Content,
                Timestamp = m.Timestamp,
                ChannelId = m.ChannelId,
                Category = m.Category,
            }).ToList(),
            TotalCount = messages.Count,
        };
    }

    /// <inheritdoc />
    public async Task<int> DeleteConversation(string conversationId)
    {
        var result = await this.historyPurge.PurgeAsync(new HistoryPurgeScope(conversationId, null), null, Context.ConnectionAborted)
            .ConfigureAwait(false);
        await this.ResetSessionsAfterChannelPurgeAsync(conversationId).ConfigureAwait(false);
        return result.Deleted;
    }

    // Deleted text also survives as recall-tool output in the context of other channels' sessions.
    private async Task ResetSessionsAfterChannelPurgeAsync(string channelId)
    {
        await this.runtime.ResetSessionAsync(channelId, Context.ConnectionAborted).ConfigureAwait(false);
        await this.runtime.ResetRecallExposedSessionsAsync(Context.ConnectionAborted).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> ClearAllMessages()
    {
        var result = await this.historyPurge.PurgeAsync(HistoryPurgeScope.All, null, Context.ConnectionAborted)
            .ConfigureAwait(false);
        await this.runtime.ResetAllSessionsAsync(Context.ConnectionAborted)
            .ConfigureAwait(false);
        return result.Deleted;
    }

    /// <inheritdoc />
    public async Task<int> DeleteMessagesOlderThan(DateTimeOffset olderThan)
    {
        var result = await this.historyPurge.PurgeAsync(new HistoryPurgeScope(null, olderThan), null, Context.ConnectionAborted)
            .ConfigureAwait(false);
        // Reset all sessions so they rebuild context from remaining messages
        await this.runtime.ResetAllSessionsAsync(Context.ConnectionAborted)
            .ConfigureAwait(false);
        return result.Deleted;
    }

    /// <inheritdoc />
    public async Task<int> DeleteChannelMessagesOlderThan(string channelId, DateTimeOffset olderThan)
    {
        var result = await this.historyPurge.PurgeAsync(new HistoryPurgeScope(channelId, olderThan), null, Context.ConnectionAborted)
            .ConfigureAwait(false);
        await this.ResetSessionsAfterChannelPurgeAsync(channelId).ConfigureAwait(false);
        return result.Deleted;
    }

    /// <inheritdoc />
    public async Task<HistoryPurgeResultDto> PurgeHistory(HistoryPurgeRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = new HistoryPurgeScope(string.IsNullOrWhiteSpace(request.ChannelId) ? null : request.ChannelId, request.OlderThan, request.From);
        if (scope.Problem() is { } problem)
        {
            throw new HubException(problem);
        }

        if (!DependentMemoryActions.IsValid(request.DependentMemories))
        {
            throw new HubException("dependentMemories must be review or delete.");
        }

        var dependentMemories = DependentMemoryActions.IsDelete(request.DependentMemories) ? DependentMemoryAction.Delete : DependentMemoryAction.Review;

        HistoryPurgeResult result;
        try
        {
            result = await this.historyPurge.PurgeAsync(
                scope,
                null,
                Context.ConnectionAborted,
                dependentMemories: dependentMemories,
                expectedMessages: request.ExpectedMessages).ConfigureAwait(false);
        }
        catch (HistoryScopeChangedException ex)
        {
            throw new HubException(ex.Message);
        }

        if (request.ChannelId is { Length: > 0 } channelId)
        {
            await this.ResetSessionsAfterChannelPurgeAsync(channelId).ConfigureAwait(false);
        }
        else
        {
            await this.runtime.ResetAllSessionsAsync(Context.ConnectionAborted).ConfigureAwait(false);
        }

        return new HistoryPurgeResultDto
        {
            Deleted = result.Deleted,
            ErasureComplete = result.ErasureComplete,
            CleanupPending = result.CleanupPending,
            QuarantinedMemories = result.QuarantinedMemories,
            DeletedMemories = result.DeletedMemories,
            DependentMemories = dependentMemories == DependentMemoryAction.Delete ? DependentMemoryActions.Delete : DependentMemoryActions.Review,
        };
    }

    /// <inheritdoc />
    public Task<bool> SetHistoryRecallTools(bool enabled)
    {
        this.historyTools.SetEnabled(enabled);
        return Task.FromResult(this.historyTools.IsEnabled);
    }

    /// <inheritdoc />
    public async Task<EventBackfillResultDto> BackfillHistoryEvents()
    {
        if (this.eventBackfill is null)
        {
            throw new HubException("The event backfill is not available on this agent.");
        }

        var report = await this.eventBackfill.RunAsync(Context.ConnectionAborted).ConfigureAwait(false);
        return new EventBackfillResultDto
        {
            Scanned = report.Scanned,
            Inserted = report.Inserted,
            ElapsedMs = (long)report.Elapsed.TotalMilliseconds,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChannelSummaryDto>> GetChannelSummaries()
    {
        var summaries = await this.messageStore.GetChannelSummariesAsync(Context.ConnectionAborted)
            .ConfigureAwait(false);

        return summaries.Select(s => new ChannelSummaryDto
        {
            Id = s.ChannelId,
            MessageCount = s.MessageCount,
            LastActivity = s.LastActivity,
        }).ToList();
    }

    /// <inheritdoc />
    public async Task ClearAllMemories()
    {
        await this.DeleteListedMemoriesAsync(10000).ConfigureAwait(false);
    }

    private async Task DeleteListedMemoriesAsync(int limit)
    {
        var memories = await this.memoryService.ListAsync(limit, offset: 0, Context.ConnectionAborted)
            .ConfigureAwait(false);
        if (memories.Items.Count > 0)
        {
            await this.memoryService.DeleteManyAsync(
                memories.Items.Select(m => m.MemoryId).ToList(), Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ClearAll()
    {
        // Clear messages through the erasure workflow
        await this.historyPurge.PurgeAsync(HistoryPurgeScope.All, null, Context.ConnectionAborted)
            .ConfigureAwait(false);

        await this.DeleteListedMemoriesAsync(10000).ConfigureAwait(false);

        // Reset sessions
        await this.runtime.ResetAllSessionsAsync(Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    // --- Memory Management ---

    /// <inheritdoc />
    public Task<MemoryListResult> ListMemories(int limit, int offset)
    {
        this.LogMemoryList(limit, offset);
        return this.memoryService.ListAsync(limit, offset, Context.ConnectionAborted);
    }

    /// <inheritdoc />
    public Task<MemoryItem?> GetMemory(string memoryId)
    {
        return this.memoryService.GetAsync(memoryId, Context.ConnectionAborted);
    }

    /// <inheritdoc />
    public async Task<MemoryItem?> CreateMemory(MemoryCreateRequest request)
    {
        this.LogMemoryCreate(request.Title?.Length ?? 0, request.Content.Length);
        return await this.memoryService.CreateAsync(
            request.Content, request.Title, request.Tags,
            force: true, Context.ConnectionAborted).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MemoryItem?> UpdateMemory(MemoryUpdateRequest request)
    {
        this.LogMemoryUpdate(request.MemoryId);
        return await this.memoryService.UpdateAsync(
            request.MemoryId, request.Content, request.Title, request.Tags,
            Context.ConnectionAborted).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteMemory(string memoryId)
    {
        this.LogMemoryDelete(memoryId);
        return await this.memoryService.DeleteAsync(memoryId, Context.ConnectionAborted).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MemoryListResult> ListQuarantinedMemories()
    {
        var ids = await this.memoryQuarantine.GetQuarantinedIdsAsync(Context.ConnectionAborted).ConfigureAwait(false);
        var items = new List<MemoryItem>();
        foreach (var id in ids.Order(StringComparer.Ordinal))
        {
            if (await this.memoryService.GetAsync(id, Context.ConnectionAborted).ConfigureAwait(false) is { } item)
            {
                items.Add(item);
            }
        }

        return new MemoryListResult { Items = items, TotalCount = items.Count };
    }

    /// <inheritdoc />
    public async Task<bool> KeepQuarantinedMemory(string memoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryId);
        await this.memoryQuarantine.KeepAsync(memoryId, Context.ConnectionAborted).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MemorySearchItem>> SearchMemories(MemorySearchRequest request)
    {
        this.LogMemorySearch(request.Query.Length);
        return this.memoryService.SearchAsync(
            request.Query, request.Limit, request.MinScore, request.Tags,
            Context.ConnectionAborted);
    }

    // --- Memory Configuration ---

    /// <inheritdoc />
    public Task<MemoryConfig> GetMemoryConfig()
    {
        var mcpOpts = this.memoryMcpOptions.CurrentValue;
        var compOpts = this.compactionOptions.CurrentValue;
        var convCompOpts = this.conversationCompactionOptions.CurrentValue;
        return Task.FromResult(new MemoryConfig
        {
            DuplicateThreshold = mcpOpts.DuplicateThreshold,
            CompactionSimilarityThreshold = compOpts.SimilarityThreshold,
            CompactionEnabled = compOpts.Enabled,
            IdleCompactionEnabled = this.memorySettingsStore.IdleCompactionEnabled ?? true,
            IdleResetMinutes = this.memorySettingsStore.IdleResetMinutes ?? 360,
            CompactionPreserveRecentTurns = convCompOpts.PreserveRecentTurns,
        });
    }

    /// <inheritdoc />
    public Task UpdateMemoryConfig(MemoryConfig config)
    {
        this.LogMemoryConfigUpdate(config.DuplicateThreshold, config.CompactionSimilarityThreshold, config.CompactionEnabled);

        this.memorySettingsStore.Update(
            config.DuplicateThreshold,
            config.CompactionSimilarityThreshold,
            config.CompactionEnabled,
            config.IdleCompactionEnabled,
            config.IdleResetMinutes,
            config.ImagePreserveRecentTurns,
            config.ImageDescribeOnStrip,
            config.CompactionPreserveRecentTurns,
            config.OllamaEndpoint,
            config.OllamaApiKey,
            memoryEnabled: config.Enabled);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<EmbeddingProbeResult> TestEmbeddingEndpoint(string endpoint, string? apiKey)
    {
        var ollama = this.memoryMcpOptions.CurrentValue.Ollama;
        this.LogTestEmbeddingEndpoint(endpoint);

        using var http = this.httpClientFactory.CreateClient("embedding-probe");

        var result = await EmbeddingEndpointProber.ProbeAsync(
            endpoint,
            apiKey,
            ollama.Model,
            ollama.Dimensions,
            http,
            Context.ConnectionAborted).ConfigureAwait(false);

        this.LogTestEmbeddingEndpointResult(endpoint, result.Ok, result.Error);
        return result;
    }

    public async Task<CompactConversationResult> CompactConversation(string channelId)
    {
        this.LogCompactConversationRequested(channelId);
        try
        {
            return await this.runtime.CompactChannelAsync(channelId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new CompactConversationResult { Success = false, Error = ex.Message };
        }
    }

    public async Task<string> RunInlineSlashCommand(string channelId, string commandText)
    {
        this.LogInlineSlashCommandRequested(channelId, commandText);
        try
        {
            return await this.runtime
                .RunInlineSlashCommandAsync(channelId, commandText, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.LogInlineSlashCommandFailed(channelId, commandText, ex.Message);
            return $"Error running `{commandText}`: {ex.Message}";
        }
    }

    public async Task<CompactMemoriesResult> CompactMemories()
    {
        this.LogCompactMemoriesRequested();
        try
        {
            var (checked_, merged) = await this.compactionService.RunOnDemandAsync(CancellationToken.None)
                .ConfigureAwait(false);
            return new CompactMemoriesResult { Success = true, MemoriesChecked = checked_, MemoriesMerged = merged };
        }
        catch (Exception ex)
        {
            return new CompactMemoriesResult { Success = false, Error = ex.Message };
        }
    }

    public async Task ResetAndReseedSession(string channelId)
    {
        this.LogResetAndReseedSession(channelId);

        await this.runtime.ResetSessionAsync(channelId, Context.ConnectionAborted).ConfigureAwait(false);

        var messages = await this.messageStore.GetMessagesAsync(
            channelId, limit: 100,
            visibility: Storage.MessageVisibility.Seeding,
            cancellationToken: Context.ConnectionAborted).ConfigureAwait(false);

        if (messages.Count > 0)
        {
            var hubMessages = messages.Select(m => new HubChatMessage
            {
                MessageId = m.MessageId ?? $"reseed-{m.Id}",
                Role = m.Role,
                Text = m.Content,
                Timestamp = m.Timestamp,
            }).ToArray();

            await this.runtime.SeedHistoryAsync(channelId, hubMessages, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
    }

    // --- Export/Import ---

    /// <inheritdoc />
    public async Task<ExportBundle> ExportAll()
    {
        this.LogExportAll();
        var memories = await ExportMemories().ConfigureAwait(false);
        var messages = await ExportMessages().ConfigureAwait(false);
        var tasks = await ExportTasks().ConfigureAwait(false);

        return new ExportBundle
        {
            ExportedAt = DateTimeOffset.UtcNow,
            Memories = memories,
            Messages = messages,
            Tasks = tasks,
        };
    }

    /// <inheritdoc />
    public async Task<ExportMemoriesPayload> ExportMemories()
    {
        var result = await this.memoryService.ListAsync(limit: 100_000, offset: 0, Context.ConnectionAborted)
            .ConfigureAwait(false);

        return new ExportMemoriesPayload
        {
            Items = result.Items,
            TotalCount = result.TotalCount,
        };
    }

    /// <inheritdoc />
    public async Task<ExportMessagesPayload> ExportMessages()
    {
        var records = await this.messageStore.GetAllMessagesAsync(Storage.MessageVisibility.All, Context.ConnectionAborted)
            .ConfigureAwait(false);

        var items = records.Select(ExportImportMapper.MapMessageToExport).ToList();

        return new ExportMessagesPayload
        {
            Items = items,
            TotalCount = items.Count,
        };
    }

    /// <inheritdoc />
    public Task<ExportTasksPayload> ExportTasks()
    {
        var tasks = this.scheduler.GetAll();
        var items = tasks.Select(ExportImportMapper.MapTaskToDto).ToList();

        return Task.FromResult(new ExportTasksPayload
        {
            Items = items,
            TotalCount = items.Count,
        });
    }

    /// <inheritdoc />
    public async Task<ImportResult> ImportAll(ExportBundle bundle)
    {
        this.LogImportAll();
        var importStartedAt = DateTimeOffset.UtcNow;
        try
        {
            // The messages are validated first so a bad batch cannot leave memories already replaced.
            var bundleErrors = Storage.MessageImportValidator.Validate(
                bundle.Messages.Items.Select(ExportImportMapper.MapExportToMessageRecord).ToList());
            if (bundleErrors.Count > 0)
            {
                return new ImportResult { Success = false, Error = "Import rejected: " + string.Join("; ", bundleErrors.Take(5)) };
            }

            var memoriesResult = await ImportMemories(new ImportMemoriesRequest
            {
                Items = bundle.Memories.Items.Select(m => new MemoryCreateRequest
                {
                    Content = m.Content,
                    Title = m.Title,
                    Tags = m.Tags.ToList(),
                }).ToList(),
                ClearExisting = true,
            }).ConfigureAwait(false);

            var messagesResult = await this.ImportMessagesCoreAsync(new ImportMessagesRequest
            {
                Items = bundle.Messages.Items,
                ClearExisting = true,
            }, importStartedAt).ConfigureAwait(false);

            var tasksResult = await ImportTasks(new ImportTasksRequest
            {
                Items = bundle.Tasks.Items,
                ClearExisting = true,
            }).ConfigureAwait(false);

            return new ImportResult
            {
                Success = true,
                MemoriesImported = memoriesResult.MemoriesImported,
                MessagesImported = messagesResult.MessagesImported,
                TasksImported = tasksResult.TasksImported,
            };
        }
        catch (Exception ex)
        {
            return new ImportResult { Success = false, Error = ex.Message };
        }
    }

    /// <inheritdoc />
    public async Task<ImportResult> ImportMemories(ImportMemoriesRequest request)
    {
        try
        {
            if (request.ClearExisting)
            {
                await this.DeleteListedMemoriesAsync(100_000).ConfigureAwait(false);
            }

            var imported = 0;
            foreach (var item in request.Items)
            {
                var created = await this.memoryService.CreateAsync(
                    item.Content, item.Title, item.Tags,
                    force: true, Context.ConnectionAborted).ConfigureAwait(false);

                // An import is the owner's deliberate content: exempt from the quarantine windows
                // of the history purge that replaces messages (D10), whichever endpoint runs first.
                if (created is not null)
                {
                    await this.memoryQuarantine.KeepAsync(created.MemoryId, Context.ConnectionAborted).ConfigureAwait(false);
                }

                imported++;
            }

            return new ImportResult { Success = true, MemoriesImported = imported };
        }
        catch (Exception ex)
        {
            return new ImportResult { Success = false, Error = ex.Message };
        }
    }

    /// <inheritdoc />
    public Task<ImportResult> ImportMessages(ImportMessagesRequest request)
        => this.ImportMessagesCoreAsync(request, DateTimeOffset.UtcNow);

    private async Task<ImportResult> ImportMessagesCoreAsync(ImportMessagesRequest request, DateTimeOffset importStartedAt)
    {
        try
        {
            var records = request.Items
                .Select(ExportImportMapper.MapExportToMessageRecord)
                .ToList();

            // Validate and stage the whole batch before touching any row (§4.1 atomic import).
            var errors = Storage.MessageImportValidator.Validate(records);
            if (errors.Count > 0)
            {
                return new ImportResult { Success = false, Error = "Import rejected: " + string.Join("; ", errors.Take(5)) };
            }

            // ClearExisting replaces everything in ONE transaction through the erasure workflow
            // (delete-all + insert + restore-generation bump), so the old rows are erased too.
            // Quarantine windows end where the import began, so memories created by it are not hidden.
            var inserted = request.ClearExisting
                ? (await this.historyPurge.PurgeAsync(HistoryPurgeScope.All, records, Context.ConnectionAborted, importStartedAt).ConfigureAwait(false)).Inserted
                : await this.messageStore.BulkInsertAsync(records, Context.ConnectionAborted).ConfigureAwait(false);

            // Reset all sessions so they rebuild from the new message data
            await this.runtime.ResetAllSessionsAsync(Context.ConnectionAborted)
                .ConfigureAwait(false);

            return new ImportResult { Success = true, MessagesImported = inserted };
        }
        catch (Exception ex)
        {
            return new ImportResult { Success = false, Error = ex.Message };
        }
    }

    /// <inheritdoc />
    public Task<ImportResult> ImportTasks(ImportTasksRequest request)
    {
        try
        {
            if (request.ClearExisting)
            {
                this.scheduler.ClearAll();
            }

            var imported = 0;
            foreach (var dto in request.Items)
            {
                var task = ExportImportMapper.MapDtoToTask(dto);
                if (task.Status is ScheduledTaskStatus.Pending or ScheduledTaskStatus.Running)
                {
                    this.scheduler.Schedule(task);
                    imported++;
                }
            }

            return Task.FromResult(new ImportResult { Success = true, TasksImported = imported });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ImportResult { Success = false, Error = ex.Message });
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Inline slash command requested on {ChannelId}: {Command}")]
    private partial void LogInlineSlashCommandRequested(string channelId, string command);

    [LoggerMessage(Level = LogLevel.Error, Message = "Inline slash command failed on {ChannelId} ({Command}): {Error}")]
    private partial void LogInlineSlashCommandFailed(string channelId, string command, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Exporting all agent data")]
    private partial void LogExportAll();

    [LoggerMessage(Level = LogLevel.Information, Message = "Importing all agent data")]
    private partial void LogImportAll();

    [LoggerMessage(Level = LogLevel.Information, Message = "Bridge connected: {ConnectionId}")]
    private partial void LogBridgeConnected(string connectionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bridge connection rejected: {ConnectionId} — already connected as {ExistingConnectionId}")]
    private partial void LogBridgeConnectionRejected(string connectionId, string existingConnectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bridge disconnected: {ConnectionId}, reason: {Reason}")]
    private partial void LogBridgeDisconnected(string connectionId, string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Message received for conversation {ConversationId}")]
    private partial void LogMessageReceived(string conversationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error processing message for conversation {ConversationId}: {ErrorMessage}")]
    private partial void LogMessageError(string conversationId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Abort requested for conversation {ConversationId}")]
    private partial void LogAbortRequested(string conversationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent config update received")]
    private partial void LogConfigUpdate();

    [LoggerMessage(Level = LogLevel.Information, Message = "LLM credentials received: {ProviderCount} providers")]
    private partial void LogCredentialsReceived(int providerCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Storage key received: {Outcome}")]
    private partial void LogStorageKeyReceived(Storage.StorageKeyAcceptance outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reranker grant received: enabled={Enabled}, key={HasKey}")]
    private partial void LogRerankerGrantReceived(bool enabled, bool hasKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeding history for channel {ChannelId}: {MessageCount} messages")]
    private partial void LogSeedHistory(string channelId, int messageCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resetting session for channel {ChannelId}")]
    private partial void LogResetSession(string channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resetting all sessions")]
    private partial void LogResetAllSessions();

    [LoggerMessage(Level = LogLevel.Information, Message = "Active channels received from Bridge: {ChannelCount} channels")]
    private partial void LogActiveChannelsReceived(int channelCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Listing memories: limit={Limit}, offset={Offset}")]
    private partial void LogMemoryList(int limit, int offset);

    [LoggerMessage(Level = LogLevel.Information, Message = "Creating memory ({TitleLength} title chars, {ContentLength} content chars)")]
    private partial void LogMemoryCreate(int titleLength, int contentLength);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updating memory: {MemoryId}")]
    private partial void LogMemoryUpdate(string memoryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleting memory: {MemoryId}")]
    private partial void LogMemoryDelete(string memoryId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Searching memories ({QueryLength} chars)")]
    private partial void LogMemorySearch(int queryLength);

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory config update: duplicateThreshold={DuplicateThreshold}, compactionThreshold={CompactionThreshold}, compactionEnabled={CompactionEnabled}")]
    private partial void LogMemoryConfigUpdate(float duplicateThreshold, float compactionThreshold, bool compactionEnabled);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compact conversation requested for channel {ChannelId}")]
    private partial void LogCompactConversationRequested(string channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory compaction sweep requested on demand")]
    private partial void LogCompactMemoriesRequested();

    [LoggerMessage(Level = LogLevel.Information, Message = "Testing embedding endpoint {Endpoint}")]
    private partial void LogTestEmbeddingEndpoint(string endpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Embedding endpoint {Endpoint} probe result: ok={Ok}, error={Error}")]
    private partial void LogTestEmbeddingEndpointResult(string endpoint, bool ok, string? error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reset and reseed session requested for channel {ChannelId}")]
    private partial void LogResetAndReseedSession(string channelId);
}
