using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Evaluation;

/// <summary>The agent calls the evaluation endpoints make (a seam over <see cref="HubClient"/> for tests).</summary>
public interface IEvaluationAgentClient
{
    Task<EvaluationJobDto> StartAsync(EvaluationStartRequestDto request, CancellationToken cancellationToken);

    Task<EvaluationJobDto?> GetAsync(string jobId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EvaluationJobDto>> ListAsync(CancellationToken cancellationToken);

    Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken);

    Task<EvaluationFileDto> ReadFileAsync(EvaluationFileRequestDto request, CancellationToken cancellationToken);

    Task<EvaluationFileWriteResultDto> WriteFileAsync(EvaluationFileWriteDto request, CancellationToken cancellationToken);

    Task<FrozenCorpusStatusDto> GetFrozenStatusAsync(CancellationToken cancellationToken);

    Task<EvaluationPackageChunkDto> ReadPackageAsync(string name, long offset, int count, CancellationToken cancellationToken);

    Task<bool> AcknowledgeOffsiteDeletionAsync(string version, CancellationToken cancellationToken);

    Task<EvaluationRunDto> GetRunAsync(CancellationToken cancellationToken);

    Task<EvaluationRunCommandResultDto> ControlRunAsync(EvaluationRunCommandDto command, CancellationToken cancellationToken);

    Task<GradingItemDto> GetGradingItemAsync(int index, CancellationToken cancellationToken);

    Task<GradingProgressDto> SubmitGradeAsync(GradeRequestDto grade, CancellationToken cancellationToken);

    Task<string?> ReadRunReportAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Adapts a connected <see cref="HubClient"/> to <see cref="IEvaluationAgentClient"/>.</summary>
public sealed class HubEvaluationAgentClient(HubClient client) : IEvaluationAgentClient
{
    /// <summary>The adapter for <paramref name="client"/>, or null when it is missing or disconnected.</summary>
    public static IEvaluationAgentClient? For(HubClient? client) => client is { IsConnected: true } ? new HubEvaluationAgentClient(client) : null;

    public Task<EvaluationJobDto> StartAsync(EvaluationStartRequestDto request, CancellationToken cancellationToken) => client.StartEvaluationAsync(request, cancellationToken);

    public Task<EvaluationJobDto?> GetAsync(string jobId, CancellationToken cancellationToken) => client.GetEvaluationJobAsync(jobId, cancellationToken);

    public Task<IReadOnlyList<EvaluationJobDto>> ListAsync(CancellationToken cancellationToken) => client.ListEvaluationJobsAsync(cancellationToken);

    public Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken) => client.CancelEvaluationJobAsync(jobId, cancellationToken);

    public Task<EvaluationFileDto> ReadFileAsync(EvaluationFileRequestDto request, CancellationToken cancellationToken) => client.ReadEvaluationFileAsync(request, cancellationToken);

    public Task<EvaluationFileWriteResultDto> WriteFileAsync(EvaluationFileWriteDto request, CancellationToken cancellationToken) => client.WriteEvaluationFileAsync(request, cancellationToken);

    public Task<FrozenCorpusStatusDto> GetFrozenStatusAsync(CancellationToken cancellationToken) => client.GetFrozenCorpusStatusAsync(cancellationToken);

    public Task<EvaluationPackageChunkDto> ReadPackageAsync(string name, long offset, int count, CancellationToken cancellationToken) =>
        client.ReadEvaluationPackageAsync(name, offset, count, cancellationToken);

    public Task<bool> AcknowledgeOffsiteDeletionAsync(string version, CancellationToken cancellationToken) => client.AcknowledgeOffsiteDeletionAsync(version, cancellationToken);

    public Task<EvaluationRunDto> GetRunAsync(CancellationToken cancellationToken) => client.GetEvaluationRunAsync(cancellationToken);

    public Task<EvaluationRunCommandResultDto> ControlRunAsync(EvaluationRunCommandDto command, CancellationToken cancellationToken) => client.ControlEvaluationRunAsync(command, cancellationToken);

    public Task<GradingItemDto> GetGradingItemAsync(int index, CancellationToken cancellationToken) => client.GetGradingItemAsync(index, cancellationToken);

    public Task<GradingProgressDto> SubmitGradeAsync(GradeRequestDto grade, CancellationToken cancellationToken) => client.SubmitGradeAsync(grade, cancellationToken);

    public Task<string?> ReadRunReportAsync(string key, CancellationToken cancellationToken) => client.ReadEvaluationRunReportAsync(key, cancellationToken);
}
