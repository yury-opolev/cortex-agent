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
}
