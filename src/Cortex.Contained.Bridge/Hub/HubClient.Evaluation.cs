using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cortex.Contained.Bridge.Hub;

/// <summary>Evaluation jobs and workspace files in the agent (<see cref="IEvaluationHub"/>).</summary>
public sealed partial class HubClient
{
    /// <summary>Starts an evaluation command in the agent process.</summary>
    public async Task<EvaluationJobDto> StartEvaluationAsync(EvaluationStartRequestDto request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<EvaluationJobDto>(nameof(IAgentHub.StartEvaluation), request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One job, or null.</summary>
    public async Task<EvaluationJobDto?> GetEvaluationJobAsync(string jobId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<EvaluationJobDto?>(nameof(IAgentHub.GetEvaluationJob), jobId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Recent jobs, newest first.</summary>
    public async Task<IReadOnlyList<EvaluationJobDto>> ListEvaluationJobsAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<IReadOnlyList<EvaluationJobDto>>(nameof(IAgentHub.ListEvaluationJobs), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Requests cancellation of a running job.</summary>
    public async Task<bool> CancelEvaluationJobAsync(string jobId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<bool>(nameof(IAgentHub.CancelEvaluationJob), jobId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a workspace file.</summary>
    public async Task<EvaluationFileDto> ReadEvaluationFileAsync(EvaluationFileRequestDto request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<EvaluationFileDto>(nameof(IAgentHub.ReadEvaluationFile), request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a workspace file.</summary>
    public async Task<EvaluationFileWriteResultDto> WriteEvaluationFileAsync(EvaluationFileWriteDto request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<EvaluationFileWriteResultDto>(nameof(IAgentHub.WriteEvaluationFile), request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Frozen corpora, packages and pending off-machine deletions.</summary>
    public async Task<FrozenCorpusStatusDto> GetFrozenCorpusStatusAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<FrozenCorpusStatusDto>(nameof(IAgentHub.GetFrozenCorpusStatus), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One chunk of an export package.</summary>
    public async Task<EvaluationPackageChunkDto> ReadEvaluationPackageAsync(string name, long offset, int count, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<EvaluationPackageChunkDto>(nameof(IAgentHub.ReadEvaluationPackage), name, offset, count, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records the owner's deletion of an off-machine package.</summary>
    public async Task<bool> AcknowledgeOffsiteDeletionAsync(string version, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<bool>(nameof(IAgentHub.AcknowledgeOffsiteDeletion), version, cancellationToken).ConfigureAwait(false);
    }
}
