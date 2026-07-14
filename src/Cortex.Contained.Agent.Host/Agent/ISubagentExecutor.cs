namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Executes a single subagent task — a new run or a resume — and returns its terminal outcome.
/// The <see cref="SubagentExecutionCoordinator"/> owns claiming, concurrency admission, terminal
/// persistence, and requeue-on-shutdown; the executor owns only building the worker's context and
/// driving the <see cref="SubagentRunner"/> registered for the task. It must NOT persist any
/// terminal state itself — it returns a <see cref="SubagentExecutionResult"/> the coordinator
/// records exactly once.
/// </summary>
public interface ISubagentExecutor
{
    /// <summary>
    /// Run or resume <paramref name="task"/> using the runner already registered under its task id,
    /// dispatching by the persisted <see cref="SubagentTask.RunMode"/>.
    /// </summary>
    /// <param name="task">The claimed task (already transitioned to Running by the coordinator).</param>
    /// <param name="cancellationToken">
    /// The registry-owned per-task cancellation token handed to the coordinator by
    /// <see cref="SubagentRunnerRegistry.TryRegister"/>. This is the only token the execution uses.
    /// </param>
    Task<SubagentExecutionResult> ExecuteAsync(
        SubagentTask task,
        CancellationToken cancellationToken);
}
