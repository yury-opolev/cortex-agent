using System.Collections.Concurrent;
using System.Globalization;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.SystemPrompt;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cortex.Contained.Bridge.Hub;

/// <summary>
/// SignalR client that connects to the Agent Hub inside Docker.
/// Implements <see cref="IAgentHubClient"/> callbacks — when the agent
/// invokes client methods (OnResponseChunk, etc.),
/// this class handles them on the Bridge side.
/// </summary>
public sealed partial class HubClient : IAsyncDisposable
{
    private readonly ILogger<HubClient> logger;
    private HubConnection? connection;

    /// <summary>Pending API channel requests awaiting a response, keyed by conversation ID.</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ResponseCompleteMessage>> pendingResponses = new();

    /// <summary>Fired when the agent sends a response chunk.</summary>
    public event Func<ResponseChunkMessage, Task>? OnResponseChunk;

    /// <summary>Fired when the agent completes a response.</summary>
    public event Func<ResponseCompleteMessage, Task>? OnResponseComplete;

    /// <summary>Fired when the agent executes a tool.</summary>
    public event Func<ToolExecutionMessage, Task>? OnToolExecution;

    /// <summary>Fired when the agent's status changes.</summary>
    public event Func<AgentStatusInfo, Task>? OnStatusChanged;

    /// <summary>Fired when the agent reports an error.</summary>
    public event Func<AgentErrorMessage, Task>? OnError;

    /// <summary>Fired when a conversation is updated.</summary>
    public event Func<ConversationInfo, Task>? OnConversationUpdated;

    /// <summary>
    /// Fired when the agent sends a proactive (unsolicited) message to a channel.
    /// The Bridge should route the message to the target channel and return the result.
    /// </summary>
    public event Func<ProactiveMessage, Task<ProactiveMessageResult>>? OnProactiveMessage;

    /// <summary>
    /// Fired when a scheduled task completes execution.
    /// The Bridge should persist the instruction and response to the scheduled-tasks channel.
    /// </summary>
    public event Func<ScheduledTaskCompleteMessage, Task>? OnScheduledTaskComplete;

    /// <summary>
    /// Fired when the agent detects voice gender from a personality description.
    /// The Bridge should update the tenant's VoiceGender setting.
    /// </summary>
    public event Func<string, Task>? OnVoiceGenderDetected;

    /// <summary>Raised when the agent invalidates a voiceprint cache entry. Argument is the tenant ID.</summary>
    public event Func<string, Task>? OnVoiceprintInvalidated;

    /// <summary>Raised on enrollment state changes. Arguments: tenantId, stateName, capturedSamples, requiredSamples.</summary>
    public event Func<string, string, int, int, Task>? OnVoiceEnrollmentProgress;

    /// <summary>
    /// Fired when the agent needs the Bridge to refresh an OAuth token.
    /// The Bridge should refresh the token, persist it, and return the fresh tokens.
    /// </summary>
    public event Func<string, Task<TokenRefreshResult>>? OnTokenRefreshRequested;

    /// <summary>
    /// Agent requests token reload from secrets.json (token revoked by another process).
    /// </summary>
    public event Func<string, Task<TokenRefreshResult>>? OnTokenReloadRequested;

    /// <summary>Fired when the connection is lost.</summary>
    public event Func<Exception?, Task>? Disconnected;

    /// <summary>Fired when the connection is re-established.</summary>
    public event Func<string?, Task>? Reconnected;

    public HubClient(ILogger<HubClient> logger)
    {
        this.logger = logger;
    }

    /// <summary>Whether the client is currently connected to the hub.</summary>
    public bool IsConnected => this.connection?.State == HubConnectionState.Connected;

    /// <summary>
    /// Connect to the Agent Hub. Safe to call again on an already-created client:
    /// the previous connection (possibly stuck in an endless reconnect attempt,
    /// e.g. a hung WebSocket connect against a wedged docker-proxy) is detached
    /// and disposed after the new one is established.
    /// </summary>
    public async Task ConnectAsync(string hubUrl, string token, CancellationToken cancellationToken)
    {
        this.LogConnecting(hubUrl);

        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .WithAutomaticReconnect(new RetryPolicy())
            .Build();

        RegisterCallbacks(connection);

        connection.Closed += OnConnectionClosed;
        connection.Reconnected += OnConnectionReconnected;
        connection.Reconnecting += OnConnectionReconnecting;

        await connection.StartAsync(cancellationToken).ConfigureAwait(false);

        var previous = this.connection;
        this.connection = connection;
        this.LogConnected(connection.ConnectionId ?? "unknown");

        if (previous is not null)
        {
            // Detach handlers first so killing the zombie doesn't raise a
            // spurious Disconnected event for the now-healthy client.
            previous.Closed -= OnConnectionClosed;
            previous.Reconnected -= OnConnectionReconnected;
            previous.Reconnecting -= OnConnectionReconnecting;
            try
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.LogStaleConnectionDisposeFailed(ex.Message);
            }
        }
    }

    /// <summary>
    /// Send a message to the agent.
    /// </summary>
    public async Task<SendMessageResult> SendMessageAsync(HubInboundMessage message, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<SendMessageResult>(
            nameof(IAgentHub.SendMessage),
            message,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the agent to complete a response for the given conversation.
    /// Must be called <b>before</b> <see cref="SendMessageAsync"/> for the same conversation,
    /// or use <see cref="SendAndWaitAsync"/> which handles ordering.
    /// </summary>
    public async Task<ResponseCompleteMessage> WaitForResponseAsync(
        string conversationId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcs = this.pendingResponses.GetOrAdd(conversationId, _ => new TaskCompletionSource<ResponseCompleteMessage>());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            using var registration = cts.Token.Register(() => tcs.TrySetException(
                new TimeoutException($"Agent did not respond within {timeout.TotalSeconds}s")));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            this.pendingResponses.TryRemove(conversationId, out _);
        }
    }

    /// <summary>
    /// Completes a pending <see cref="WaitForResponseAsync"/> call.
    /// Called internally when <see cref="OnResponseComplete"/> fires.
    /// </summary>
    internal void CompleteResponse(ResponseCompleteMessage response)
    {
        if (this.pendingResponses.TryGetValue(response.ConversationId, out var tcs))
        {
            tcs.TrySetResult(response);
        }
    }

    /// <summary>Abort an in-progress generation.</summary>
    public async Task AbortGenerationAsync(string conversationId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.AbortGeneration),
            conversationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Record a voice barge-in: the Agent Host replaces the in-flight assistant
    /// turn with the truncated text the user actually heard.
    /// </summary>
    public async Task OnTurnInterruptedAsync(
        TurnInterruptedNotification notification, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.OnTurnInterrupted),
            notification,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Get agent status.</summary>
    public async Task<AgentStatusInfo> GetStatusAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<AgentStatusInfo>(
            nameof(IAgentHub.GetStatus),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Update agent config.</summary>
    public async Task UpdateConfigAsync(AgentConfigUpdate config, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.UpdateConfig),
            config,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ping the agent hub.</summary>
    public async Task<HealthInfo> PingAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<HealthInfo>(
            nameof(IAgentHub.Ping),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Seed the agent's session for a channel with historical messages.</summary>
    public async Task SeedHistoryAsync(string channelId, HubChatMessage[] messages, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.SeedHistory),
            channelId,
            messages,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reset the agent's in-memory session for a channel.</summary>
    public async Task ResetSessionAsync(string channelId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.ResetSession),
            channelId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reset all agent sessions.</summary>
    public async Task ResetAllSessionsAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.ResetAllSessions),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tell the agent which channels are currently active on the Bridge.</summary>
    public async Task SetActiveChannelsAsync(string[] channelIds, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.SetActiveChannels),
            channelIds,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Push LLM provider credentials to the agent.</summary>
    public async Task ProvideCredentialsAsync(LlmCredentials credentials, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.ProvideCredentials),
            credentials,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Get the agent's personality (system prompt from personality.md).</summary>
    public async Task<string> GetPersonalityAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<string>(
            nameof(IAgentHub.GetPersonality),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Set the agent's personality (writes personality.md).</summary>
    public async Task SetPersonalityAsync(string personality, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.SetPersonality),
            personality,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Get the active system-prompt configuration (templates + authorable segments).</summary>
    public async Task<SystemPromptConfig> GetSystemPromptConfigAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<SystemPromptConfig>(
            nameof(IAgentHub.GetSystemPromptConfig), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validate and, if valid, persist a new system-prompt configuration.</summary>
    public async Task<SystemPromptValidationResult> SetSystemPromptConfigAsync(
        SystemPromptConfig config, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<SystemPromptValidationResult>(
            nameof(IAgentHub.SetSystemPromptConfig), config, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reset the system-prompt configuration to defaults and return it.</summary>
    public async Task<SystemPromptConfig> ResetSystemPromptConfigAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<SystemPromptConfig>(
            nameof(IAgentHub.ResetSystemPromptConfig), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Render and return the exact system prompt the model would receive for a given channel.</summary>
    public async Task<string> GetSystemPromptPreviewAsync(string channelId, bool isVoice, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<string>(
            nameof(IAgentHub.GetSystemPromptPreview), channelId, isVoice, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Get the agent's self-notes (operating principles).</summary>
    public async Task<string> GetSelfNotesAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<string>(
            nameof(IAgentHub.GetSelfNotes),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Set the agent's self-notes (operating principles).</summary>
    public async Task SetSelfNotesAsync(string content, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.SetSelfNotes),
            content,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fetch the current voiceprint snapshot for a tenant. Null when no record exists.</summary>
    public async Task<VoiceprintSnapshotDto?> GetVoiceprintAsync(string tenantId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<VoiceprintSnapshotDto?>(
            nameof(IAgentHub.GetVoiceprint),
            tenantId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Admin: toggle the per-tenant voice-id feature flag.</summary>
    public async Task SetVoiceFeatureEnabledAsync(string tenantId, bool enabled, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.SetVoiceFeatureEnabled),
            tenantId,
            enabled,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Admin: wipe the voiceprint and move state to Declined.</summary>
    public async Task ResetVoiceEnrollmentAsync(string tenantId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.ResetVoiceEnrollment),
            tenantId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Admin: set or clear the per-tenant cosine threshold override.</summary>
    public async Task SetVoiceThresholdOverrideAsync(string tenantId, float? threshold, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.SetVoiceThresholdOverride),
            tenantId,
            threshold,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Start voice enrollment for a tenant. Returns null on success, an error string on invalid-state.</summary>
    public async Task<string?> StartVoiceEnrollmentAsync(string tenantId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<string?>(
            nameof(IAgentHub.StartVoiceEnrollment),
            tenantId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Push the finished enrollment voiceprint to the agent to store (transitions the tenant to Enrolled).</summary>
    public async Task SubmitVoiceprintAsync(string tenantId, float[] embedding, string modelId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.SubmitVoiceprint),
            tenantId,
            embedding,
            modelId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reset self-notes to default and return the default content.</summary>
    public async Task<string> ResetSelfNotesAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<string>(
            nameof(IAgentHub.ResetSelfNotes),
            cancellationToken).ConfigureAwait(false);
    }

    // --- History Management ---

    /// <summary>List conversations from the agent's message store.</summary>
    public async Task<ConversationListResult> GetConversationsAsync(string? channelId, int limit, int offset, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<ConversationListResult>(
            nameof(IAgentHub.GetConversations),
            channelId,
            limit,
            offset,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Get messages for a conversation from the agent's message store.</summary>
    public async Task<MessageListResult> GetMessagesAsync(string conversationId, int limit, int offset, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<MessageListResult>(
            nameof(IAgentHub.GetMessages),
            conversationId,
            limit,
            offset,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Search messages across all conversations in the agent's message store.</summary>
    public async Task<MessageListResult> SearchMessagesAsync(string query, int limit, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<MessageListResult>(
            nameof(IAgentHub.SearchMessages),
            query,
            limit,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delete all messages for a conversation in the agent's message store. Returns count deleted.</summary>
    public async Task<int> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<int>(
            nameof(IAgentHub.DeleteConversation),
            conversationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Clear all messages in the agent's message store. Returns count deleted.</summary>
    public async Task<int> ClearAllMessagesAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<int>(
            nameof(IAgentHub.ClearAllMessages),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delete messages older than the given timestamp across all channels. Returns count deleted.</summary>
    public async Task<int> DeleteMessagesOlderThanAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<int>(
            nameof(IAgentHub.DeleteMessagesOlderThan),
            olderThan,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delete messages older than the given timestamp for a specific channel. Returns count deleted.</summary>
    public async Task<int> DeleteChannelMessagesOlderThanAsync(string channelId, DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<int>(
            nameof(IAgentHub.DeleteChannelMessagesOlderThan),
            channelId,
            olderThan,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// List distinct channels in the agent's message store, each with its message count
    /// and most recent activity timestamp.
    /// </summary>
    public async Task<IReadOnlyList<ChannelSummaryDto>> GetChannelSummariesAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<IReadOnlyList<ChannelSummaryDto>>(
            nameof(IAgentHub.GetChannelSummaries),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Clear all memories in the agent's memory store.</summary>
    public async Task ClearAllMemoriesAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.ClearAllMemories),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Clear all data (messages, memories) in the agent container.</summary>
    public async Task ClearAllAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.ClearAll),
            cancellationToken).ConfigureAwait(false);
    }

    // --- Memory Management ---

    /// <summary>List all memories with pagination.</summary>
    public async Task<MemoryListResult> ListMemoriesAsync(int limit, int offset, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<MemoryListResult>(
            nameof(IAgentHub.ListMemories),
            limit,
            offset,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Get a single memory by ID.</summary>
    public async Task<MemoryItem?> GetMemoryAsync(string memoryId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<MemoryItem?>(
            nameof(IAgentHub.GetMemory),
            memoryId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Create a new memory.</summary>
    public async Task<MemoryItem?> CreateMemoryAsync(MemoryCreateRequest request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<MemoryItem?>(
            nameof(IAgentHub.CreateMemory),
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Update an existing memory.</summary>
    public async Task<MemoryItem?> UpdateMemoryAsync(MemoryUpdateRequest request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<MemoryItem?>(
            nameof(IAgentHub.UpdateMemory),
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delete a memory by ID.</summary>
    public async Task<bool> DeleteMemoryAsync(string memoryId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<bool>(
            nameof(IAgentHub.DeleteMemory),
            memoryId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Search memories by semantic similarity.</summary>
    public async Task<IReadOnlyList<MemorySearchItem>> SearchMemoriesAsync(MemorySearchRequest request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<IReadOnlyList<MemorySearchItem>>(
            nameof(IAgentHub.SearchMemories),
            request,
            cancellationToken).ConfigureAwait(false);
    }

    // --- Memory Configuration ---

    /// <summary>Get the current memory configuration from the agent.</summary>
    public async Task<MemoryConfig> GetMemoryConfigAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<MemoryConfig>(
            nameof(IAgentHub.GetMemoryConfig),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Update memory configuration on the agent at runtime.</summary>
    public async Task UpdateMemoryConfigAsync(MemoryConfig config, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.UpdateMemoryConfig),
            config,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateSpeakerIdConfigAsync(SpeakerIdConfig config, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.UpdateSpeakerIdConfig),
            config,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CompactConversationResult> CompactConversationAsync(string channelId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<CompactConversationResult>(
            nameof(IAgentHub.CompactConversation),
            channelId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RunInlineSlashCommandAsync(
        string channelId, string commandText, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<string>(
            nameof(IAgentHub.RunInlineSlashCommand),
            channelId,
            commandText,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CompactMemoriesResult> CompactMemoriesAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<CompactMemoriesResult>(
            nameof(IAgentHub.CompactMemories),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Probe an embedding endpoint from the agent's network context (so Docker-internal
    /// service names resolve). Returns reachability + observed embedding dimension.
    /// </summary>
    public async Task<EmbeddingProbeResult> TestEmbeddingEndpointAsync(
        string endpoint, string? apiKey, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<EmbeddingProbeResult>(
            nameof(IAgentHub.TestEmbeddingEndpoint),
            endpoint,
            apiKey,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetAndReseedSessionAsync(string channelId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(
            nameof(IAgentHub.ResetAndReseedSession),
            channelId,
            cancellationToken).ConfigureAwait(false);
    }

    // --- Export/Import ---

    /// <summary>Export all agent data.</summary>
    public async Task<ExportBundle> ExportAllAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<ExportBundle>(
            nameof(IAgentHub.ExportAll),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Export all memories.</summary>
    public async Task<ExportMemoriesPayload> ExportMemoriesAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<ExportMemoriesPayload>(
            nameof(IAgentHub.ExportMemories),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Export all messages.</summary>
    public async Task<ExportMessagesPayload> ExportMessagesAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<ExportMessagesPayload>(
            nameof(IAgentHub.ExportMessages),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Export all scheduled tasks.</summary>
    public async Task<ExportTasksPayload> ExportTasksAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<ExportTasksPayload>(
            nameof(IAgentHub.ExportTasks),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Import a full export bundle.</summary>
    public async Task<ImportResult> ImportAllAsync(ExportBundle bundle, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<ImportResult>(
            nameof(IAgentHub.ImportAll),
            bundle,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Import memories.</summary>
    public async Task<ImportResult> ImportMemoriesAsync(ImportMemoriesRequest request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<ImportResult>(
            nameof(IAgentHub.ImportMemories),
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Import messages.</summary>
    public async Task<ImportResult> ImportMessagesAsync(ImportMessagesRequest request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<ImportResult>(
            nameof(IAgentHub.ImportMessages),
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Import scheduled tasks.</summary>
    public async Task<ImportResult> ImportTasksAsync(ImportTasksRequest request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<ImportResult>(
            nameof(IAgentHub.ImportTasks),
            request,
            cancellationToken).ConfigureAwait(false);
    }

    private void RegisterCallbacks(HubConnection connection)
    {
        // Agent pushes response chunks
        connection.On<ResponseChunkMessage>(
            nameof(IAgentHubClient.OnResponseChunk),
            chunk => OnResponseChunk?.Invoke(chunk) ?? Task.CompletedTask);

        // Agent completes a response
        connection.On<ResponseCompleteMessage>(
            nameof(IAgentHubClient.OnResponseComplete),
            response =>
            {
                // Complete any pending API channel waiters
                CompleteResponse(response);
                return OnResponseComplete?.Invoke(response) ?? Task.CompletedTask;
            });

        // Agent executes a tool
        connection.On<ToolExecutionMessage>(
            nameof(IAgentHubClient.OnToolExecution),
            toolExec => OnToolExecution?.Invoke(toolExec) ?? Task.CompletedTask);

        // Agent status changes
        connection.On<AgentStatusInfo>(
            nameof(IAgentHubClient.OnStatusChanged),
            status => OnStatusChanged?.Invoke(status) ?? Task.CompletedTask);

        // Agent errors
        connection.On<AgentErrorMessage>(
            nameof(IAgentHubClient.OnError),
            agentError =>
            {
                // Fail any pending API channel waiters for this conversation
                if (this.pendingResponses.TryRemove(agentError.ConversationId, out var tcs))
                {
                    tcs.TrySetException(new InvalidOperationException(
                        $"Agent error ({agentError.ErrorCode}): {agentError.Message}"));
                }
                return OnError?.Invoke(agentError) ?? Task.CompletedTask;
            });

        // Conversation updates
        connection.On<ConversationInfo>(
            nameof(IAgentHubClient.OnConversationUpdated),
            conversation => OnConversationUpdated?.Invoke(conversation) ?? Task.CompletedTask);

        // Agent sends a proactive (unsolicited) message to a channel.
        // Returns delivery result via SignalR Client Results.
        connection.On<ProactiveMessage, ProactiveMessageResult>(
            nameof(IAgentHubClient.OnProactiveMessage),
            message => OnProactiveMessage?.Invoke(message)
                ?? Task.FromResult(new ProactiveMessageResult { Success = false, Error = "No handler registered" }));

        // Agent reports that a scheduled task has finished execution.
        connection.On<ScheduledTaskCompleteMessage>(
            nameof(IAgentHubClient.OnScheduledTaskComplete),
            message => OnScheduledTaskComplete?.Invoke(message) ?? Task.CompletedTask);

        // Agent requests an OAuth token refresh (Anthropic token expired/expiring).
        // Returns the fresh tokens directly via SignalR Client Results to avoid the
        // deadlock that occurs with a separate ProvideCredentials callback.
        connection.On<string, TokenRefreshResult>(
            nameof(IAgentHubClient.OnTokenRefreshRequested),
            providerName => OnTokenRefreshRequested?.Invoke(providerName)
                ?? Task.FromResult(new TokenRefreshResult { Success = false, Error = "No handler registered" }));

        // Agent requests token reload from secrets.json (token revoked by another process).
        connection.On<string, TokenRefreshResult>(
            nameof(IAgentHubClient.OnTokenReloadRequested),
            providerName => OnTokenReloadRequested?.Invoke(providerName)
                ?? Task.FromResult(new TokenRefreshResult { Success = false, Error = "No handler registered" }));

        // Agent detected voice gender from personality description.
        connection.On<string>(
            nameof(IAgentHubClient.OnVoiceGenderDetected),
            gender => OnVoiceGenderDetected?.Invoke(gender) ?? Task.CompletedTask);

        // Agent pushed an invalidation for a tenant's voiceprint cache entry.
        connection.On<string>(
            nameof(IAgentHubClient.OnVoiceprintInvalidated),
            tenantId => OnVoiceprintInvalidated?.Invoke(tenantId) ?? Task.CompletedTask);

        // Agent pushed an enrollment progress event.
        connection.On<string, string, int, int>(
            nameof(IAgentHubClient.OnVoiceEnrollmentProgress),
            (tenantId, stateName, captured, required) =>
                OnVoiceEnrollmentProgress?.Invoke(tenantId, stateName, captured, required) ?? Task.CompletedTask);

        this.RegisterCodingCallbacks(connection);
        this.RegisterMcpCallbacks(connection);
    }

    private void EnsureConnected()
    {
        if (this.connection is null || this.connection.State != HubConnectionState.Connected)
        {
            throw new InvalidOperationException("Not connected to the Agent Hub.");
        }
    }

    private Task OnConnectionClosed(Exception? ex)
    {
        this.LogDisconnected(ex?.Message);
        return Disconnected?.Invoke(ex) ?? Task.CompletedTask;
    }

    private Task OnConnectionReconnected(string? connectionId)
    {
        this.LogReconnected(connectionId ?? "unknown");
        return Reconnected?.Invoke(connectionId) ?? Task.CompletedTask;
    }

    private Task OnConnectionReconnecting(Exception? ex)
    {
        this.LogReconnecting(ex?.Message);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (this.connection is not null)
        {
            await this.connection.DisposeAsync().ConfigureAwait(false);
            this.connection = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connecting to Agent Hub at {HubUrl}")]
    private partial void LogConnecting(string hubUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to Agent Hub: connectionId={ConnectionId}")]
    private partial void LogConnected(string connectionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disconnected from Agent Hub: {Reason}")]
    private partial void LogDisconnected(string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnected to Agent Hub: connectionId={ConnectionId}")]
    private partial void LogReconnected(string connectionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconnecting to Agent Hub: {Reason}")]
    private partial void LogReconnecting(string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to dispose stale Agent Hub connection: {Reason}")]
    private partial void LogStaleConnectionDisposeFailed(string? reason);

    /// <summary>
    /// Reconnect policy with exponential backoff.
    /// </summary>
    private sealed class RetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays =
        [
            TimeSpan.FromSeconds(0),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var index = Math.Min(retryContext.PreviousRetryCount, Delays.Length - 1);
            return Delays[index];
        }
    }
}
