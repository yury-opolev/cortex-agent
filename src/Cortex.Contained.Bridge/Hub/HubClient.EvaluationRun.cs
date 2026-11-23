using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cortex.Contained.Bridge.Hub;

/// <summary>The evaluation run and the owner's κ grading in the agent (<see cref="IEvaluationHub"/>).</summary>
public sealed partial class HubClient
{
    /// <summary>The evaluation run.</summary>
    public async Task<EvaluationRunDto> GetEvaluationRunAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<EvaluationRunDto>(nameof(IAgentHub.GetEvaluationRun), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts, cancels or resumes the run, approves the dictionary or supplies the export passphrase.</summary>
    public async Task<EvaluationRunCommandResultDto> ControlEvaluationRunAsync(EvaluationRunCommandDto command, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<EvaluationRunCommandResultDto>(nameof(IAgentHub.ControlEvaluationRun), command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One item of the κ sample.</summary>
    public async Task<GradingItemDto> GetGradingItemAsync(int index, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<GradingItemDto>(nameof(IAgentHub.GetGradingItem), index, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores the owner's grade of one item.</summary>
    public async Task<GradingProgressDto> SubmitGradeAsync(GradeRequestDto grade, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<GradingProgressDto>(nameof(IAgentHub.SubmitGrade), grade, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A report the run recorded, or null.</summary>
    public async Task<string?> ReadEvaluationRunReportAsync(string key, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<string?>(nameof(IAgentHub.ReadEvaluationRunReport), key, cancellationToken).ConfigureAwait(false);
    }
}
