using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Evaluation;

/// <summary>DI registration of the in-process evaluation jobs and workspace files (called from <c>Program.cs</c>).</summary>
public static class EvaluationRegistration
{
    /// <summary>Registers <see cref="EvaluationJobService"/> and <see cref="EvaluationFiles"/>.</summary>
    public static void AddEvaluation(IServiceCollection services, StorageEncryptionContext encryption)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(encryption);
        services.AddSingleton(sp =>
        {
            var history = sp.GetRequiredService<IOptionsMonitor<HistoryOptions>>();
            var memory = sp.GetRequiredService<IOptionsMonitor<MemoryMcpOptions>>();
            var access = sp.GetRequiredService<RerankerAccess>();
            Func<RerankerOptions, IReranker> reranker = options => RerankEvalSetup.LiveReranker(options, () => access.ApiKey);
            var commands = new Dictionary<string, EvaluationCommand>(StringComparer.Ordinal)
            {
                ["history-eval"] = (run, ct) => HistoryEvalCli.RunInAgentAsync(
                    run.Args, run.Output, run.Protection, history.CurrentValue, memory.CurrentValue, encryption.Layout.MessagesDatabase, reranker, ct),
                ["memory-eval"] = (run, ct) => MemoryEvalCli.RunInAgentAsync(run.Args, run.Output, run.Protection, memory.CurrentValue, reranker, ct),
            };
            return new EvaluationJobService(
                commands,
                EvaluationJobService.ProtectionFor(encryption, sp.GetRequiredService<StorageKeyRing>()),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<EvaluationJobService>>());
        });
        services.AddSingleton(sp => new EvaluationFiles(
            Workspaces(sp.GetRequiredService<IOptionsMonitor<HistoryOptions>>().CurrentValue, encryption.Layout),
            EvaluationJobService.ProtectionFor(encryption, sp.GetRequiredService<StorageKeyRing>())));
    }

    /// <summary>Workspace directory per tool.</summary>
    public static IReadOnlyDictionary<string, string> Workspaces(HistoryOptions history, StorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(layout);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["history-eval"] = history.EvalArtifactsDirectory,
            ["memory-eval"] = MemoryEvalWorkspace.DefaultDirectory,
            ["eval-corpus"] = Path.Combine(layout.EvaluationDirectory, "corpus"),
        };
    }
}
