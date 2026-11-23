using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary><see cref="IEvaluationHub"/>: evaluation jobs, workspace files and frozen corpora for the Bridge evaluation endpoints.</summary>
public sealed partial class AgentHub
{
    /// <inheritdoc />
    public Task<EvaluationJobDto> StartEvaluation(EvaluationStartRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(this.EvaluationJobs.Start(request));
    }

    /// <inheritdoc />
    public Task<EvaluationJobDto?> GetEvaluationJob(string jobId) => Task.FromResult(this.EvaluationJobs.Get(jobId));

    /// <inheritdoc />
    public Task<IReadOnlyList<EvaluationJobDto>> ListEvaluationJobs() => Task.FromResult(this.EvaluationJobs.List());

    /// <inheritdoc />
    public Task<bool> CancelEvaluationJob(string jobId) => Task.FromResult(this.EvaluationJobs.Cancel(jobId));

    /// <inheritdoc />
    public Task<EvaluationFileDto> ReadEvaluationFile(EvaluationFileRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return Task.FromResult(this.EvaluationFiles.Read(request));
        }
        catch (StorageLockedException)
        {
            throw new HubException("The storage key has not arrived from the Bridge yet.");
        }
    }

    /// <inheritdoc />
    public Task<EvaluationFileWriteResultDto> WriteEvaluationFile(EvaluationFileWriteDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return Task.FromResult(this.EvaluationFiles.Write(request));
        }
        catch (StorageLockedException)
        {
            throw new HubException("The storage key has not arrived from the Bridge yet.");
        }
    }

    /// <inheritdoc />
    public Task<FrozenCorpusStatusDto> GetFrozenCorpusStatus() => Task.FromResult(this.FrozenCorpora.Status());

    /// <inheritdoc />
    public Task<EvaluationPackageChunkDto> ReadEvaluationPackage(string name, long offset, int count) =>
        Task.FromResult(this.FrozenCorpora.ReadPackage(name, offset, count));

    /// <inheritdoc />
    public Task<bool> AcknowledgeOffsiteDeletion(string version) => Task.FromResult(this.FrozenCorpora.AcknowledgeDeletion(version));

    private Evaluation.EvaluationJobService EvaluationJobs =>
        this.evaluationJobs ?? throw new HubException("Evaluation jobs are not available on this agent.");

    private Evaluation.Frozen.FrozenCorpusStore FrozenCorpora =>
        this.frozenCorpora ?? throw new HubException("Frozen evaluation corpora are not available on this agent.");

    private Evaluation.EvaluationFiles EvaluationFiles =>
        this.evaluationFiles ?? throw new HubException("Evaluation files are not available on this agent.");
}
