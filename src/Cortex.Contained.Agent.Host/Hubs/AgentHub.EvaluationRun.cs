using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary><see cref="IEvaluationHub"/>: the evaluation run and the owner's κ grading.</summary>
public sealed partial class AgentHub
{
    /// <inheritdoc />
    public Task<EvaluationRunDto> GetEvaluationRun() => Task.FromResult(this.EvaluationRun.Run());

    /// <inheritdoc />
    public Task<EvaluationRunCommandResultDto> ControlEvaluationRun(EvaluationRunCommandDto command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Task.FromResult(Unlocked(() => this.EvaluationRun.Control(command)));
    }

    /// <inheritdoc />
    public Task<GradingItemDto> GetGradingItem(int index) => Task.FromResult(Unlocked(() => this.EvaluationRun.Item(index)));

    /// <inheritdoc />
    public Task<GradingProgressDto> SubmitGrade(GradeRequestDto grade)
    {
        ArgumentNullException.ThrowIfNull(grade);
        try
        {
            return Task.FromResult(Unlocked(() => this.EvaluationRun.Grade(grade)));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new HubException(ex.Message.Split(" (Parameter", StringSplitOptions.None)[0]);
        }
    }

    /// <inheritdoc />
    public Task<string?> ReadEvaluationRunReport(string key) => Task.FromResult(this.EvaluationRun.Report(key));

    private Evaluation.Orchestration.EvaluationRunApi EvaluationRun =>
        this.evaluationRun ?? throw new HubException("The evaluation run is not available on this agent.");

    private static T Unlocked<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (StorageLockedException)
        {
            throw new HubException("The storage key has not arrived from the Bridge yet.");
        }
    }
}
