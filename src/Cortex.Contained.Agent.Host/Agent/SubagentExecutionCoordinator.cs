using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// The single owner of durable subagent execution. Runs as a hosted service that:
/// claims queued/recovered work, admits it atomically under the concurrency cap, executes new or
/// resumed runs via <see cref="ISubagentExecutor"/>, records the terminal result EXACTLY ONCE
/// (never overwriting a Failed/Cancelled as Completed), requeues (not fails) in-flight work on host
/// shutdown, and only dispatches once Bridge + credentials + MCP-catalog readiness are all signaled.
/// </summary>
public sealed partial class SubagentExecutionCoordinator : IHostedService, IDisposable
{
    private readonly SubagentSessionStore store;
    private readonly SubagentRunnerRegistry registry;
    private readonly ISubagentExecutor executor;
    private readonly Func<SubagentTask, SubagentRunner> runnerFactory;
    private readonly Func<string, string, Task> onCompletion;
    private readonly ILogger<SubagentExecutionCoordinator> logger;

    /// <summary>
    /// Wake signal (not task data): capacity one, DropOldest. Any number of writers coalesce into a
    /// single pending "check the queue" token so the dispatch loop never backs up.
    /// </summary>
    private readonly Channel<byte> wakeChannel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly ConcurrentDictionary<string, Task> inFlight = new(StringComparer.Ordinal);

    private readonly Lock readinessLock = new();
    private bool bridgeConnected;
    private bool credentialsReady;
    private bool mcpCatalogReady;

    private CancellationTokenSource? stoppingCts;
    private Task? dispatchLoop;

    /// <summary>Max length of a sanitized crash message persisted as a task result.</summary>
    private const int MaxSanitizedMessageLength = 300;

    public SubagentExecutionCoordinator(
        SubagentSessionStore store,
        SubagentRunnerRegistry registry,
        ISubagentExecutor executor,
        Func<SubagentTask, SubagentRunner> runnerFactory,
        Func<string, string, Task> onCompletion,
        ILogger<SubagentExecutionCoordinator> logger)
    {
        this.store = store;
        this.registry = registry;
        this.executor = executor;
        this.runnerFactory = runnerFactory;
        this.onCompletion = onCompletion;
        this.logger = logger;

        // A freed slot (Remove) or a raised cap (SetMaxConcurrent) wakes the dispatch loop.
        this.registry.SetSlotsOpenedCallback(this.SignalWorkAvailable);
    }

    // ── Public signals ───────────────────────────────────────────────────

    /// <summary>Wake the dispatch loop to re-check the queue. Idempotent and non-blocking.</summary>
    public void SignalWorkAvailable()
    {
        this.wakeChannel.Writer.TryWrite(0);
    }

    /// <summary>The Bridge connected; enable dispatch once all readiness signals are set.</summary>
    public void OnBridgeConnected()
    {
        this.SetReadiness(bridge: true);
    }

    /// <summary>The Bridge disconnected; stop dispatching new work until it returns.</summary>
    public void OnBridgeDisconnected()
    {
        lock (this.readinessLock)
        {
            this.bridgeConnected = false;
        }
    }

    /// <summary>LLM credentials were (un)applied by the Bridge.</summary>
    public void MarkCredentialsReady(bool ready)
    {
        this.SetReadiness(credentials: ready);
    }

    /// <summary>The MCP tool catalog was received (an empty catalog still counts as ready).</summary>
    public void MarkMcpCatalogReady()
    {
        this.SetReadiness(mcp: true);
    }

    private void SetReadiness(bool? bridge = null, bool? credentials = null, bool? mcp = null)
    {
        bool ready;
        lock (this.readinessLock)
        {
            if (bridge.HasValue)
            {
                this.bridgeConnected = bridge.Value;
            }

            if (credentials.HasValue)
            {
                this.credentialsReady = credentials.Value;
            }

            if (mcp.HasValue)
            {
                this.mcpCatalogReady = mcp.Value;
            }

            ready = this.bridgeConnected && this.credentialsReady && this.mcpCatalogReady;
        }

        if (ready)
        {
            this.LogReadyToDispatch();
            this.SignalWorkAvailable();
        }
    }

    private bool IsReady
    {
        get
        {
            lock (this.readinessLock)
            {
                return this.bridgeConnected && this.credentialsReady && this.mcpCatalogReady;
            }
        }
    }

    // ── Hosted service lifecycle ─────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        this.stoppingCts = new CancellationTokenSource();
        this.dispatchLoop = Task.Run(() => this.RunDispatchLoopAsync(this.stoppingCts.Token), CancellationToken.None);

        // In case work is already queued (e.g. crash recovery) and readiness is already set.
        this.SignalWorkAvailable();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        this.stoppingCts?.Cancel();

        // Cancel every in-flight runner so its loop unwinds; each execution then REQUEUES
        // (not fails) because the host-stopping token is set.
        foreach (var taskId in this.registry.GetActiveTaskIds())
        {
            this.registry.TryCancel(taskId);
        }

        var pending = this.inFlight.Values.ToArray();
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Shutdown deadline reached — remaining work stays queued for the next start.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                this.LogShutdownWaitInterrupted(ex.Message);
            }
        }

        if (this.dispatchLoop is not null)
        {
            try
            {
                await this.dispatchLoop.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The loop unwinds on cancellation; nothing to recover here.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                this.LogShutdownWaitInterrupted(ex.Message);
            }
        }
    }

    // ── Dispatch loop ────────────────────────────────────────────────────

    private async Task RunDispatchLoopAsync(CancellationToken stopping)
    {
        try
        {
            while (await this.wakeChannel.Reader.WaitToReadAsync(stopping).ConfigureAwait(false))
            {
                // Coalesce all queued signals into one pass.
                while (this.wakeChannel.Reader.TryRead(out _))
                {
                }

                this.DispatchReadyWork(stopping);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
#pragma warning disable CA1031 // The dispatch loop must never crash the host.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogDispatchLoopFailed(ex.Message);
        }
    }

    private void DispatchReadyWork(CancellationToken stopping)
    {
        // Recovered/queued work is dispatched ONLY after Bridge + credentials + MCP are ready.
        if (!this.IsReady || stopping.IsCancellationRequested)
        {
            return;
        }

        while (!stopping.IsCancellationRequested && this.registry.HasAvailableSlot)
        {
            var task = this.store.TryClaimOldestQueued();
            if (task is null)
            {
                break;
            }

            var runner = this.runnerFactory(task);

            // Atomic admission under the cap. The ONLY token the execution uses is the one handed back.
            if (!this.registry.TryRegister(task.TaskId, runner, out var token))
            {
                // Cap was lowered between the slot check and here — put the task back and stop.
                runner.Dispose();
                this.store.Requeue(task.TaskId);
                break;
            }

            var runModeValue = task.RunMode.ToStorageValue();
            this.LogDispatching(task.TaskId, runModeValue);

            var taskId = task.TaskId;
            var execution = this.ExecuteClaimedAsync(task, runner, token, stopping);
            this.inFlight[taskId] = execution;

            // Remove the tracking entry when the execution finishes (even if it already completed
            // synchronously) so the dictionary never accumulates stale, completed tasks.
            _ = execution.ContinueWith(
                completed => this.inFlight.TryRemove(new KeyValuePair<string, Task>(taskId, completed)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task ExecuteClaimedAsync(
        SubagentTask task, SubagentRunner runner, CancellationToken token, CancellationToken stopping)
    {
        var taskId = task.TaskId;
        try
        {
            var result = await this.executor.ExecuteAsync(task, token).ConfigureAwait(false);
            await this.RecordTerminalAndNotifyAsync(taskId, result).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Host shutdown: REQUEUE in-flight work instead of recording a terminal failure.
            this.store.Requeue(taskId);
            this.LogRequeuedOnShutdown(taskId);
        }
        catch (OperationCanceledException)
        {
            // Per-task cancellation (sub_agent_stop cancelled the registry-owned token).
            await this.RecordTerminalAndNotifyAsync(
                taskId, new SubagentExecutionResult(SubagentTaskState.Cancelled, "[Subagent stopped]"))
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A crashing subagent must not crash the host; record it as Failed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogSubagentCrashed(taskId, ex.Message);
            await this.RecordTerminalAndNotifyAsync(
                taskId, new SubagentExecutionResult(
                    SubagentTaskState.Failed, $"[Subagent crashed: {Sanitize(ex.Message)}]"))
                .ConfigureAwait(false);
        }
        finally
        {
            // Always free the slot, then signal another pass so the next queued task starts.
            // (The inFlight entry is removed by the dispatcher's completion continuation.)
            this.registry.Remove(taskId);
            runner.Dispose();
            this.SignalWorkAvailable();
        }
    }

    /// <summary>
    /// Records the terminal result exactly once through the guarded conditional update (a Failed or
    /// Cancelled task can never be overwritten as Completed) and then notifies the main agent with the
    /// durable result — whichever terminal write actually won.
    /// </summary>
    private async Task RecordTerminalAndNotifyAsync(string taskId, SubagentExecutionResult result)
    {
        this.store.TrySetTerminalResult(taskId, result);

        // Notify with the durable result (the first terminal write wins), not necessarily this one.
        var finalTask = this.store.GetById(taskId);
        var deliveredResult = finalTask?.Result ?? result.Result;

        try
        {
            await this.onCompletion(taskId, deliveredResult).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Completion notification must never fault the execution path.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogCompletionNotifyFailed(taskId, ex.Message);
        }
    }

    private static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "unknown error";
        }

        var collapsed = message
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();

        return collapsed.Length > MaxSanitizedMessageLength
            ? collapsed[..MaxSanitizedMessageLength]
            : collapsed;
    }

    public void Dispose()
    {
        this.wakeChannel.Writer.TryComplete();
        this.stoppingCts?.Dispose();
    }

    // ── Logging ──────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-coordinator] Readiness satisfied — dispatching queued work")]
    private partial void LogReadyToDispatch();

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-coordinator] Dispatching {TaskId} (mode={RunMode})")]
    private partial void LogDispatching(string taskId, string runMode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent-coordinator] Requeued in-flight task on shutdown: {TaskId}")]
    private partial void LogRequeuedOnShutdown(string taskId);

    [LoggerMessage(Level = LogLevel.Error, Message = "[subagent-coordinator] Subagent crashed: {TaskId} — {ErrorMessage}")]
    private partial void LogSubagentCrashed(string taskId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent-coordinator] Completion notification failed for {TaskId}: {ErrorMessage}")]
    private partial void LogCompletionNotifyFailed(string taskId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent-coordinator] Dispatch loop failed: {ErrorMessage}")]
    private partial void LogDispatchLoopFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[subagent-coordinator] Shutdown wait interrupted: {ErrorMessage}")]
    private partial void LogShutdownWaitInterrupted(string errorMessage);
}
