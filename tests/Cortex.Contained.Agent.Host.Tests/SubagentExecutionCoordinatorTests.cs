using System.Diagnostics;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Drives <see cref="SubagentExecutionCoordinator"/> against a hand-written
/// <see cref="ISubagentExecutor"/> substitute to prove the durable-execution invariants:
/// exactly-once terminal persistence, registry-owned cancellation tokens, readiness gating,
/// slot release + re-dispatch, and requeue (not fail) on host shutdown.
/// </summary>
public sealed class SubagentExecutionCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sacoord-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SubagentSessionStore _store;

    public SubagentExecutionCoordinatorTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SubagentSessionStore(_dir, NullLogger<SubagentSessionStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Executor dispatch by mode ────────────────────────────────────────

    [Fact]
    public async Task QueuedNewTask_ExecutorReceivesNewMode()
    {
        SeedQueued("sa-new", SubagentRunMode.New);
        var executor = new RecordingExecutor();
        await using var harness = this.StartHarness(executor, maxConcurrent: 2);

        harness.MarkAllReady();

        await WaitUntilAsync(() => executor.CallCount > 0);
        Assert.Equal(SubagentRunMode.New, executor.LastTask!.RunMode);
    }

    [Fact]
    public async Task QueuedResumeTask_ExecutorReceivesResumeMode()
    {
        SeedQueued("sa-resume", SubagentRunMode.Resume, withMessages: true);
        var executor = new RecordingExecutor();
        await using var harness = this.StartHarness(executor, maxConcurrent: 2);

        harness.MarkAllReady();

        await WaitUntilAsync(() => executor.CallCount > 0);
        Assert.Equal(SubagentRunMode.Resume, executor.LastTask!.RunMode);
    }

    // ── Terminal persistence ─────────────────────────────────────────────

    [Fact]
    public async Task FailedExecution_RemainsFailed()
    {
        SeedQueued("sa-fail", SubagentRunMode.New);
        var executor = new RecordingExecutor
        {
            Behavior = (_, _) => Task.FromResult(new SubagentExecutionResult(SubagentTaskState.Failed, "boom")),
        };
        await using var harness = this.StartHarness(executor, maxConcurrent: 2);

        harness.MarkAllReady();

        await WaitUntilAsync(() => _store.GetById("sa-fail")!.State == SubagentTaskState.Failed);
        var task = _store.GetById("sa-fail")!;
        Assert.Equal(SubagentTaskState.Failed, task.State);
        Assert.Equal("boom", task.Result);
    }

    [Fact]
    public async Task CancelledExecution_RemainsCancelled()
    {
        SeedQueued("sa-cancel", SubagentRunMode.New);
        var executor = new RecordingExecutor
        {
            // Per-task cancellation (not host shutdown) surfaces as OperationCanceledException.
            Behavior = (_, _) => throw new OperationCanceledException(),
        };
        await using var harness = this.StartHarness(executor, maxConcurrent: 2);

        harness.MarkAllReady();

        await WaitUntilAsync(() => _store.GetById("sa-cancel")!.State == SubagentTaskState.Cancelled);
        Assert.Equal(SubagentTaskState.Cancelled, _store.GetById("sa-cancel")!.State);
    }

    [Fact]
    public async Task CompletedExecution_CannotOverwriteConcurrentCancellation()
    {
        SeedQueued("sa-race", SubagentRunMode.New);
        var executor = new RecordingExecutor
        {
            Behavior = (task, _) =>
            {
                // Simulate a concurrent stop landing the guarded terminal Cancelled write mid-execution.
                _store.TrySetTerminalResult(
                    task.TaskId, new SubagentExecutionResult(SubagentTaskState.Cancelled, "[stopped]"));
                // The executor still reports Completed — the coordinator's guarded write must NOT win.
                return Task.FromResult(new SubagentExecutionResult(SubagentTaskState.Completed, "all good"));
            },
        };
        await using var harness = this.StartHarness(executor, maxConcurrent: 2);

        harness.MarkAllReady();

        await WaitUntilAsync(() => executor.CallCount > 0);
        // Give the coordinator's post-return terminal write a chance to (fail to) apply.
        await WaitUntilAsync(() => _store.GetById("sa-race")!.State == SubagentTaskState.Cancelled);
        var task = _store.GetById("sa-race")!;
        Assert.Equal(SubagentTaskState.Cancelled, task.State);
        Assert.Equal("[stopped]", task.Result);
    }

    // ── Registry-owned token ─────────────────────────────────────────────

    [Fact]
    public async Task ResumedExecution_UsesRegistryOwnedToken()
    {
        SeedQueued("sa-token", SubagentRunMode.Resume, withMessages: true);
        var captured = new ManualResetEventSlim(false);
        var release = new TaskCompletionSource();
        var executor = new RecordingExecutor
        {
            Behavior = async (_, ct) =>
            {
                captured.Set();
                await release.Task.ConfigureAwait(false);
                return new SubagentExecutionResult(SubagentTaskState.Completed, "done");
            },
        };
        await using var harness = this.StartHarness(executor, maxConcurrent: 2);

        harness.MarkAllReady();

        Assert.True(captured.Wait(TimeSpan.FromSeconds(5)));
        // The token handed to the executor is the registry-owned per-task token, not a caller token.
        Assert.Equal(harness.Registry.GetCancellationToken("sa-token"), executor.LastToken);
        Assert.True(executor.LastToken.CanBeCanceled);

        release.SetResult();
        await WaitUntilAsync(() => _store.GetById("sa-token")!.State == SubagentTaskState.Completed);
    }

    // ── Slot release + re-dispatch ───────────────────────────────────────

    [Fact]
    public async Task RunnerCompletion_ReleasesSlotAndDispatchesNext()
    {
        SeedQueued("sa-1", SubagentRunMode.New, createdAt: DateTimeOffset.UtcNow);
        SeedQueued("sa-2", SubagentRunMode.New, createdAt: DateTimeOffset.UtcNow.AddSeconds(1));
        var executor = new RecordingExecutor();
        // Cap of 1 forces the second task to wait for the first to release its slot.
        await using var harness = this.StartHarness(executor, maxConcurrent: 1);

        harness.MarkAllReady();

        await WaitUntilAsync(() =>
            _store.GetById("sa-1")!.State == SubagentTaskState.Completed
            && _store.GetById("sa-2")!.State == SubagentTaskState.Completed);

        Assert.Equal(2, executor.CallCount);
        Assert.Equal(0, harness.Registry.ActiveCount);
    }

    // ── Readiness gating ─────────────────────────────────────────────────

    [Fact]
    public async Task NotReady_QueuedTask_IsNotDispatched()
    {
        SeedQueued("sa-gated", SubagentRunMode.New);
        var executor = new RecordingExecutor();
        await using var harness = this.StartHarness(executor, maxConcurrent: 2);

        // Only two of the three readiness signals — the MCP catalog is still missing.
        harness.Coordinator.OnBridgeConnected();
        harness.Coordinator.MarkCredentialsReady(true);
        harness.Coordinator.SignalWorkAvailable();

        // The queued task must stay unclaimed for the whole observation window.
        await AssertNeverAsync(() =>
            executor.CallCount > 0 || _store.GetById("sa-gated")!.State != SubagentTaskState.Queued);

        // The last readiness signal opens the gate and the queued task dispatches.
        harness.Coordinator.MarkMcpCatalogReady();

        await WaitUntilAsync(() => executor.CallCount > 0);
        await WaitUntilAsync(() => _store.GetById("sa-gated")!.State == SubagentTaskState.Completed);
    }

    // ── Host shutdown ────────────────────────────────────────────────────

    [Fact]
    public async Task StopAsync_RequeuesInFlightTask()
    {
        SeedQueued("sa-inflight", SubagentRunMode.New);
        var executor = new RecordingExecutor
        {
            // Block until the registry-owned token is cancelled (by StopAsync), then surface OCE.
            Behavior = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return new SubagentExecutionResult(SubagentTaskState.Completed, "unreachable");
            },
        };
        var harness = this.StartHarness(executor, maxConcurrent: 2);
        try
        {
            harness.MarkAllReady();

            // Wait until the task is actually in-flight (claimed + running).
            await WaitUntilAsync(() => _store.GetById("sa-inflight")!.State == SubagentTaskState.Running
                && executor.CallCount > 0);

            await harness.Coordinator.StopAsync(CancellationToken.None);

            var task = _store.GetById("sa-inflight")!;
            Assert.Equal(SubagentTaskState.Queued, task.State); // requeued, NOT failed
            Assert.True(task.RestartCount >= 1);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    // ── Dispatch-loop resilience (I1) ────────────────────────────────────

    [Fact]
    public async Task DispatchIteration_ThrowsOnce_LoopSurvivesAndNextTaskDispatches()
    {
        // sa-boom is claimed first; the runner factory throws on that first dispatch, simulating an
        // unexpected error inside a dispatch iteration (e.g. a transient store/Sqlite exception).
        // The per-iteration guard must log and CONTINUE — not exit the loop — so a subsequently
        // enqueued task still dispatches. (The store is sealed/concrete, so the throw is injected
        // through the coordinator's runner factory, which runs inside the same guarded iteration.)
        SeedQueued("sa-boom", SubagentRunMode.New, createdAt: DateTimeOffset.UtcNow);
        var executor = new RecordingExecutor();
        var throwNext = true;

        SubagentRunner FaultyFactory(SubagentTask _)
        {
            if (Volatile.Read(ref throwNext))
            {
                Volatile.Write(ref throwNext, false);
                throw new InvalidOperationException("simulated transient store failure");
            }

            return NewRunner();
        }

        await using var harness = this.StartHarness(executor, maxConcurrent: 2, runnerFactory: FaultyFactory);
        harness.MarkAllReady();

        // Wait until the faulty first dispatch has been consumed (throwNext flipped to false).
        await WaitUntilAsync(() => !Volatile.Read(ref throwNext));

        // The loop survived: a newly enqueued task still dispatches and completes.
        SeedQueued("sa-ok", SubagentRunMode.New, createdAt: DateTimeOffset.UtcNow.AddSeconds(1));
        harness.Coordinator.SignalWorkAvailable();

        await WaitUntilAsync(() => _store.GetById("sa-ok")!.State == SubagentTaskState.Completed);
        Assert.Equal(SubagentTaskState.Completed, _store.GetById("sa-ok")!.State);
    }

    // ── Periodic backstop tick (idle redelivery) ─────────────────────────

    [Fact]
    public async Task BackingOffNotification_IsRedeliveredByPeriodicTick_WithoutExternalWake()
    {
        // A completed run whose durable completion notification is Pending but currently BACKING
        // OFF: attempts == 2 means the store throttles the next retry by its 5s base backoff since
        // notification_updated_at. Backdating that timestamp to 4.5s ago makes the notification
        // become due ~0.5s from now — comfortably after the only external wakes this harness ever
        // raises (StartAsync's initial signal + the readiness signal, both at t≈0).
        var now = DateTimeOffset.UtcNow;
        _store.Create(new SubagentTask
        {
            TaskId = "sa-backstop",
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "d",
            Prompt = "p",
            State = SubagentTaskState.Completed,
            Result = "the result",
            CompletedAt = now,
            NotificationState = SubagentNotificationState.Pending,
            NotificationAttempts = 2,
            NotificationUpdatedAt = now - TimeSpan.FromSeconds(4.5),
        });

        var executor = new RecordingExecutor();
        // Fast backstop so the test does not wait the 15s production interval.
        await using var harness = this.StartHarness(
            executor, maxConcurrent: 2, backstopTickInterval: TimeSpan.FromMilliseconds(100));

        // The ONLY external wake the coordinator receives — it fires while the notification is
        // still inside its backoff window, so this pass must NOT claim/enqueue it.
        harness.MarkAllReady();

        // While still backing off, no wake (initial or readiness) may deliver it.
        await AssertNeverAsync(() =>
            _store.GetById("sa-backstop")!.NotificationState != SubagentNotificationState.Pending);

        // After the backoff elapses there are NO further external wakes — only the periodic backstop
        // tick can re-scan the queue, and it must eventually claim + enqueue the notification.
        await WaitUntilAsync(() =>
            _store.GetById("sa-backstop")!.NotificationState == SubagentNotificationState.Enqueued);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void SeedQueued(
        string taskId,
        SubagentRunMode runMode,
        bool withMessages = false,
        DateTimeOffset? createdAt = null)
    {
        var task = new SubagentTask
        {
            TaskId = taskId,
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "d",
            Prompt = "p",
            State = SubagentTaskState.Queued,
            RunMode = runMode,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            Messages = withMessages
                ? [new LlmMessage { Role = "user", Content = "prior" }]
                : [],
        };
        _store.Create(task);
    }

    private Harness StartHarness(
        ISubagentExecutor executor,
        int maxConcurrent,
        Func<SubagentTask, SubagentRunner>? runnerFactory = null,
        TimeSpan? backstopTickInterval = null)
    {
        var registry = new SubagentRunnerRegistry(maxConcurrent, NullLogger<SubagentRunnerRegistry>.Instance);

        SubagentRunner DefaultRunnerFactory(SubagentTask _) => new(
            Substitute.For<ILlmClient>(),
            new ToolRegistry([], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance),
            10,
            NullLogger<SubagentRunner>.Instance);

        var coordinator = new SubagentExecutionCoordinator(
            _store,
            registry,
            executor,
            runnerFactory ?? DefaultRunnerFactory,
            new AgentMessageChannel(),
            NullLogger<SubagentExecutionCoordinator>.Instance,
            backstopTickInterval);

        coordinator.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return new Harness(coordinator, registry);
    }

    private static SubagentRunner NewRunner() => new(
        Substitute.For<ILlmClient>(),
        new ToolRegistry([], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance),
        10,
        NullLogger<SubagentRunner>.Instance);

    /// <summary>Polls for the whole window and fails as soon as the condition becomes true.</summary>
    private static async Task AssertNeverAsync(Func<bool> condition, int windowMs = 300)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < windowMs)
        {
            Assert.False(condition());
            await Task.Delay(15).ConfigureAwait(false);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Condition was not satisfied within the timeout.");
            }

            await Task.Delay(15).ConfigureAwait(false);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public SubagentExecutionCoordinator Coordinator { get; }
        public SubagentRunnerRegistry Registry { get; }

        public Harness(SubagentExecutionCoordinator coordinator, SubagentRunnerRegistry registry)
        {
            this.Coordinator = coordinator;
            this.Registry = registry;
        }

        public void MarkAllReady()
        {
            this.Coordinator.OnBridgeConnected();
            this.Coordinator.MarkCredentialsReady(true);
            this.Coordinator.MarkMcpCatalogReady();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await this.Coordinator.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch { /* best-effort teardown */ }
#pragma warning restore CA1031
            this.Coordinator.Dispose();
        }
    }

    /// <summary>Hand-written <see cref="ISubagentExecutor"/> substitute with configurable behavior.</summary>
    private sealed class RecordingExecutor : ISubagentExecutor
    {
        private int callCount;

        public Func<SubagentTask, CancellationToken, Task<SubagentExecutionResult>> Behavior { get; set; }
            = (_, _) => Task.FromResult(new SubagentExecutionResult(SubagentTaskState.Completed, "done"));

        public int CallCount => Volatile.Read(ref this.callCount);
        public SubagentTask? LastTask { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<SubagentExecutionResult> ExecuteAsync(SubagentTask task, CancellationToken cancellationToken)
        {
            this.LastTask = task;
            this.LastToken = cancellationToken;
            Interlocked.Increment(ref this.callCount);
            return this.Behavior(task, cancellationToken);
        }
    }
}
