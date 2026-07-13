using System.Collections.Concurrent;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Transfers the current conversation's context to another channel by running an
/// internal LLM call to identify the latest topic boundary, summarizing pre-topic
/// context, and seeding the target channel's session with that summary plus the
/// verbatim recent exchange. The source session is untouched.
/// </summary>
internal sealed partial class TransferSessionTool : IAgentTool
{
    private readonly AgentSessionStore sessionStore;
    private readonly ActiveChannelStore activeChannelStore;
    private readonly ITopicSlicer slicer;
    private readonly Func<IAgentRuntime> agentRuntimeFactory;
    private readonly IProactiveMessageDispatcher dispatcher;
    private readonly MessageStore messageStore;
    private readonly ILogger<TransferSessionTool> logger;
    private readonly IChannelConversationResolver conversationResolver;
    private readonly SubagentSessionStore subagentStore;

    /// <summary>
    /// Per-target serialization. Two concurrent transfers to the same target channel
    /// would otherwise race on the session mutation and breadcrumb writes. The semaphore
    /// is created lazily per target id and never removed — memory cost is one
    /// <see cref="SemaphoreSlim"/> per channel ever transferred to, which is negligible
    /// for any realistic channel count.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> targetLocks = new();

    public TransferSessionTool(
        AgentSessionStore sessionStore,
        ActiveChannelStore activeChannelStore,
        ITopicSlicer slicer,
        Func<IAgentRuntime> agentRuntimeFactory,
        IProactiveMessageDispatcher dispatcher,
        MessageStore messageStore,
        ILogger<TransferSessionTool> logger,
        IChannelConversationResolver conversationResolver,
        SubagentSessionStore subagentStore)
    {
        this.sessionStore = sessionStore;
        this.activeChannelStore = activeChannelStore;
        this.slicer = slicer;
        this.agentRuntimeFactory = agentRuntimeFactory;
        this.dispatcher = dispatcher;
        this.messageStore = messageStore;
        this.logger = logger;
        this.conversationResolver = conversationResolver;
        this.subagentStore = subagentStore;
    }

    public string Name => "transfer_session";

    public string Description =>
        "Transfer the current conversation's context to another channel so the user can continue there. " +
        "Identifies the latest topic, summarizes earlier context, and seeds the target channel's session " +
        "with that material; the target's prior history is replaced. " +
        "Requires explicit user confirmation: phrasings like 'tell me via voice' or 'send the result to voice' " +
        "are ambiguous — the user may want a one-shot reply in the target (use send_message) rather than " +
        "moving the whole conversation. ASK FIRST and call this tool only after the user agrees. " +
        "Pass user_confirmed=true only when the user has explicitly approved the move in their most recent turn. " +
        "The tool automatically posts a short greeting in the target channel naming the topic (including " +
        "ringing the user via Discord DM if the target is a voice channel they haven't joined). " +
        "No follow-up acknowledgement needed — when the user next speaks, just continue naturally.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "target_channel": {
              "type": "string",
              "description": "Channel id to transfer to (e.g. 'voice-default', 'discord-dm'). Must be an active channel different from the current one."
            },
            "user_confirmed": {
              "type": "boolean",
              "description": "Set to true ONLY if the user explicitly confirmed they want the whole conversation moved to the target channel. Phrasing like 'tell me via voice' or 'say it in voice' is NOT confirmation by itself — the user may want a one-shot reply in the target channel while staying in the current one (use send_message for that). Ask the user 'Want me to move our whole conversation to <target>, or just speak the answer there?' and only set this true after they say yes."
            }
          },
          "required": ["target_channel", "user_confirmed"]
        }
        """;

    /// <summary>
    /// Builds the seeded message array for a target session.
    /// - When <paramref name="priorSummary"/> is non-null: starts with a synthetic
    ///   <see cref="LlmMessageType.CompactionSummary"/> user message that includes
    ///   the transfer marker and the summary, followed by verbatim user/assistant
    ///   messages from <paramref name="boundaryIndex"/> onward (tool plumbing excluded).
    /// - When null: just the verbatim slice, no marker.
    /// <paramref name="boundaryIndex"/> is clamped to <c>[0, sourceHistory.Count]</c>.
    /// </summary>
    internal static IReadOnlyList<LlmMessage> BuildSeedPayload(
        IReadOnlyList<LlmMessage> sourceHistory,
        int boundaryIndex,
        string? priorSummary,
        string sourceChannelId)
    {
        var clampedBoundary = Math.Clamp(boundaryIndex, 0, sourceHistory.Count);

        var verbatim = new List<LlmMessage>(sourceHistory.Count - clampedBoundary);
        for (var i = clampedBoundary; i < sourceHistory.Count; i++)
        {
            var m = sourceHistory[i];
            // Exclude tool plumbing — only carry plain user/assistant prose.
            if (m.Role != "user" && m.Role != "assistant")
            {
                continue;
            }

            if (m.ToolCalls is { Count: > 0 })
            {
                continue;
            }

            verbatim.Add(new LlmMessage
            {
                Role = m.Role,
                Content = m.Content,
                ContentBlocks = m.ContentBlocks,
                MessageType = LlmMessageType.Normal,
                Timestamp = m.Timestamp,
            });
        }

        if (string.IsNullOrWhiteSpace(priorSummary))
        {
            return verbatim;
        }

        var markerBody =
            $"This conversation was just transferred from {sourceChannelId}. Earlier context, summarized:\n\n"
            + priorSummary.Trim()
            + "\n\nThe verbatim recent exchange follows. Continue from where it leaves off — do not acknowledge the transfer.";

        var result = new List<LlmMessage>(verbatim.Count + 1)
        {
            new()
            {
                Role = "user",
                Content = markerBody,
                MessageType = LlmMessageType.CompactionSummary,
            },
        };
        result.AddRange(verbatim);
        return result;
    }

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string targetChannelRaw;
        bool userConfirmed;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (!doc.RootElement.TryGetProperty("target_channel", out var targetEl))
            {
                return Error("target_channel is required.");
            }

            targetChannelRaw = targetEl.GetString() ?? string.Empty;

            // Default to false when missing. We treat missing-equals-false rather than
            // a separate error so the failure message stays focused on the actual
            // remediation (ask the user) regardless of whether the LLM omitted the
            // field or set it explicitly false.
            userConfirmed = doc.RootElement.TryGetProperty("user_confirmed", out var confEl)
                && confEl.ValueKind == JsonValueKind.True;
        }
        catch (JsonException ex)
        {
            return Error($"Invalid JSON arguments: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(targetChannelRaw))
        {
            return Error("target_channel is required.");
        }

        if (!userConfirmed)
        {
            return Error(
                "user_confirmed=true is required to transfer. " +
                "Phrasings like 'tell me via voice' are ambiguous — the user may want a one-shot reply " +
                "in the target channel (use send_message for that) rather than moving the whole conversation. " +
                "Ask the user something like: 'Want me to move our whole conversation to <target>, " +
                "or just speak the answer there?' and only retry with user_confirmed=true after they say yes.");
        }

        // Resolve friendly name (e.g. "voice") to canonical id (e.g. "voice-default").
        if (!ChannelNameResolver.TryResolve(targetChannelRaw, out var targetChannelId))
        {
            // Unknown name — keep the raw value as the id; later active-channel
            // validation will reject it with a clear message.
            targetChannelId = targetChannelRaw;
        }

        if (string.Equals(targetChannelId, context.ChannelId, StringComparison.OrdinalIgnoreCase))
        {
            return Error($"target_channel cannot be the same as the current channel ({context.ChannelId}).");
        }

        // Reject synthetic conversation ids (scheduled tasks, sub-agents, etc.) — these aren't
        // backed by a real user-facing channel, so a transfer would mis-attribute the source
        // on the target's breadcrumb and confuse the agent's continuation context.
        if (ChannelNameResolver.ToFriendlyName(context.ChannelId) is null)
        {
            return Error($"Current conversation ({context.ChannelId}) is not a transferable conversation — only real user channels can be transferred.");
        }

        var activeChannels = this.activeChannelStore.Get();
        if (activeChannels.Count > 0 && !ChannelNameResolver.IsChannelActive(targetChannelId, activeChannels))
        {
            return Error($"target_channel '{targetChannelId}' is not currently active.");
        }

        var sourceSession = this.sessionStore.GetOrCreateWithIdleCheck(context.ConversationId);
        var sourceHistory = sourceSession.GetHistory();
        var userTurnCount = sourceHistory.Count(m => m.Role == "user" && !m.IsInternal);
        if (userTurnCount < 2)
        {
            return Error("Source session has no meaningful history to transfer yet.");
        }

        this.LogInvoked(context.ChannelId, targetChannelId);

        // Serialize concurrent transfers to the same target. Two transfers racing on the
        // same target would corrupt the seed/breadcrumb writes (last writer wins on Seed,
        // duplicate breadcrumb writes that no longer point at consistent state).
        var targetLock = this.targetLocks.GetOrAdd(targetChannelId, _ => new SemaphoreSlim(1, 1));
        await targetLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await this.ExecuteUnderTargetLockAsync(context, targetChannelId, sourceSession, sourceHistory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            targetLock.Release();
        }

        static AgentToolResult Error(string message) => new()
        {
            Success = false,
            Content = string.Empty,
            Error = message,
        };
    }

    /// <summary>
    /// The body of the transfer once we hold the per-target lock. Split out so the lock
    /// acquire/release is a clean wrapper and the body can use early-return semantics.
    /// </summary>
    private async Task<AgentToolResult> ExecuteUnderTargetLockAsync(
        ToolExecutionContext context,
        string targetChannelId,
        AgentSession sourceSession,
        IReadOnlyList<LlmMessage> sourceHistory,
        CancellationToken cancellationToken)
    {
        this.LogSlicerStarted(sourceHistory.Count);

        var sliceOutcome = await this.slicer.SliceAsync(
            sourceHistory,
            context.ChannelId,
            context.ConversationId,
            cancellationToken).ConfigureAwait(false);

        if (sliceOutcome is TopicSliceOutcome.Failure failure)
        {
            // Truncate the upstream reason — provider exceptions can include long stack-like
            // detail that would leak through to the agent's reasoning context.
            var safeReason = failure.Reason.Length > 200
                ? string.Concat(failure.Reason.AsSpan(0, 200), "…")
                : failure.Reason;
            return Error($"Transfer failed: could not slice source history ({safeReason}).");
        }

        var success = (TopicSliceOutcome.Success)sliceOutcome;
        this.LogSlicerCompleted(success.BoundaryIndex, success.PriorSummary is not null);

        var seedMessages = BuildSeedPayload(sourceHistory, success.BoundaryIndex, success.PriorSummary, context.ChannelId);

        // Resolve the channel id to the actual conversation id used by the runtime.
        // For voice this appends the tenant suffix (discord-voice → discord-voice-{tenant}).
        // Pull the tenant id from the source conversation if it carries one; otherwise
        // fall back to "default" (single-tenant deployment).
        var (_, sourceTenantId) = this.conversationResolver.ParseConversationId(context.ConversationId);
        var targetTenantId = sourceTenantId ?? "default";
        var targetConversationId = this.conversationResolver.ResolveConversationId(targetChannelId, targetTenantId);

        // Repoint transferable subagents that belong to the CURRENT TOPIC. The slicer's
        // BoundaryIndex marks where the current topic starts in source history;
        // subagents spawned at or after that message's timestamp were started during
        // the current topic and should follow the user to the target. Older
        // subagents belonged to a prior topic — leaving them pinned to source means
        // their completion notification lands where that conversation continues.
        //
        // Transferable = active (queued/running/revising) PLUS terminal tasks whose
        // completion notification is still pending/enqueued — an undelivered result
        // must follow the user. Delivered terminal history is NOT moved.
        var topicStartTime = success.BoundaryIndex < sourceHistory.Count
            ? sourceHistory[success.BoundaryIndex].Timestamp
            : DateTimeOffset.MaxValue;

        foreach (var transferableTask in this.subagentStore.GetTransferableTasks())
        {
            if (!string.Equals(transferableTask.ParentConversation, context.ConversationId, StringComparison.Ordinal))
            {
                continue;
            }

            if (transferableTask.CreatedAt >= topicStartTime)
            {
                this.subagentStore.RepointParent(transferableTask.TaskId, targetConversationId, targetChannelId);
                this.LogSubagentRepointed(transferableTask.TaskId, targetConversationId, targetChannelId);
            }
            else
            {
                this.LogSubagentLeftPinned(transferableTask.TaskId, context.ConversationId);
            }
        }

        // Delegate session-state mutation to the runtime. AgentRuntime owns drain +
        // Seed semantics (matches the compaction pattern — runtime is the single
        // owner of "replace this session's history" mutations); the tool stays an
        // orchestrator over: validation, slicing, payload-building, and breadcrumbs.
        await this.agentRuntimeFactory().TransferSessionAsync(targetConversationId, seedMessages, cancellationToken).ConfigureAwait(false);
        this.LogSeeded(targetConversationId, seedMessages.Count);

        // Dispatch the greeting FIRST, before writing the source-side "→ Continued in {target}"
        // breadcrumb. If the dispatch fails (e.g., voice channel briefly unreachable), we don't
        // want the source channel's UI claiming the conversation moved when it really didn't.
        // The target-side "↳ Continued from {source}" breadcrumb still writes either way —
        // it's true: the target session IS seeded (Seed already ran).
        //
        // Passing `context` to the dispatcher means the greeting also enters the target
        // session's in-memory history via AgentRuntime's deferred-injection drain — so the
        // agent on the user's next utterance knows it already greeted and won't double-greet.
        // This mirrors the exact pattern send_message uses (no double-recording observed
        // there, so reuse is safe here).
        var sourceFriendly = ChannelNameResolver.ToDisplayName(context.ChannelId);
        var targetFriendly = ChannelNameResolver.ToDisplayName(targetChannelId);
        var transferTime = DateTimeOffset.UtcNow;

        var greeting = string.IsNullOrWhiteSpace(success.TopicOneLine)
            ? $"Continuing our conversation here from {sourceFriendly}."
            : $"Continuing our conversation here from {sourceFriendly}. We were just on: {success.TopicOneLine}.";
        var dispatchResult = await this.dispatcher.DispatchAsync(targetChannelId, greeting, context, cancellationToken).ConfigureAwait(false);
        var dispatchWarning = dispatchResult.Success
            ? string.Empty
            : $" (greeting dispatch failed: {dispatchResult.Error})";
        if (!dispatchResult.Success)
        {
            this.LogGreetingFailed(targetChannelId, dispatchResult.Error ?? "(unspecified)");
        }

        // Persist UI breadcrumbs. MessageCategory.Transfer is visible in chat UI but excluded
        // from future seeding (no feedback into LLM context on a re-seed). The two writes are
        // independent — failing one shouldn't block the other.
        //
        // The source-side "→ Continued in {target}" breadcrumb is gated on dispatch success
        // so the source channel's UI never claims a transfer that didn't actually deliver.
        string? breadcrumbWarning = null;
        var failedChannels = new List<string>(2);

        try
        {
            await this.messageStore.SaveMessageAsync(
                userId: "system",
                channelId: targetChannelId,
                role: "system",
                content: $"↳ Continued from {sourceFriendly}",
                timestamp: transferTime,
                category: MessageCategory.Transfer,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Breadcrumb write must not roll back a successful in-memory seed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogBreadcrumbFailed(targetChannelId, ex.Message);
            failedChannels.Add(targetChannelId);
        }

        if (dispatchResult.Success)
        {
            try
            {
                await this.messageStore.SaveMessageAsync(
                    userId: "system",
                    channelId: context.ChannelId,
                    role: "system",
                    content: $"→ Continued in {targetFriendly}",
                    timestamp: transferTime,
                    category: MessageCategory.Transfer,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Breadcrumb write must not roll back a successful in-memory seed.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                this.LogBreadcrumbFailed(context.ChannelId, ex.Message);
                failedChannels.Add(context.ChannelId);
            }
        }

        if (failedChannels.Count > 0)
        {
            breadcrumbWarning = $" (breadcrumb persistence failed for: {string.Join(", ", failedChannels)})";
        }

        var verbatimCount = seedMessages.Count - (success.PriorSummary is null ? 0 : 1);
        var note = success.Degraded ? " (degraded: slicer JSON unparsable)" : string.Empty;
        var summaryFragment = success.PriorSummary is null ? string.Empty : " + summary of prior context";
        return AgentToolResult.Ok(
            $"Transferred conversation context to {targetChannelId}.\n" +
            $"Identified topic: {success.TopicOneLine}.\n" +
            $"Seeded {verbatimCount} verbatim messages{summaryFragment}.{note}{breadcrumbWarning}{dispatchWarning}");

        static AgentToolResult Error(string message) => new()
        {
            Success = false,
            Content = string.Empty,
            Error = message,
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "transfer_session invoked: source={SourceChannel} target={TargetChannel}")]
    private partial void LogInvoked(string sourceChannel, string targetChannel);

    [LoggerMessage(Level = LogLevel.Information, Message = "transfer_session slicer started: messageCount={MessageCount}")]
    private partial void LogSlicerStarted(int messageCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "transfer_session slicer completed: boundaryIndex={BoundaryIndex} priorSummary={HasPriorSummary}")]
    private partial void LogSlicerCompleted(int boundaryIndex, bool hasPriorSummary);

    [LoggerMessage(Level = LogLevel.Information, Message = "transfer_session seeded: targetConversation={TargetConversation} messages={MessageCount}")]
    private partial void LogSeeded(string targetConversation, int messageCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "transfer_session breadcrumb write failed: target={TargetChannel} reason={Reason}")]
    private partial void LogBreadcrumbFailed(string targetChannel, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "transfer_session greeting dispatch failed: target={TargetChannel} reason={Reason}")]
    private partial void LogGreetingFailed(string targetChannel, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "transfer_session repointed subagent {TaskId}: parent now {TargetConversation} / channel {TargetChannel}")]
    private partial void LogSubagentRepointed(string taskId, string targetConversation, string targetChannel);

    [LoggerMessage(Level = LogLevel.Information, Message = "transfer_session left subagent {TaskId} pinned to source {SourceConversation} (spawned before current topic)")]
    private partial void LogSubagentLeftPinned(string taskId, string sourceConversation);
}
