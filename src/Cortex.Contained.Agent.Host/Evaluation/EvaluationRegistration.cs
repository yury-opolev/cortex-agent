using Cortex.Contained.Agent.Host.Evaluation.Frozen;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Llm;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Evaluation;

/// <summary>DI registration of the in-process evaluation jobs and workspace files (called from <c>Program.cs</c>).</summary>
public static class EvaluationRegistration
{
    /// <summary>Registers <see cref="EvaluationJobService"/>, <see cref="EvaluationFiles"/> and the labelling options.</summary>
    public static void AddEvaluation(IServiceCollection services, StorageEncryptionContext encryption, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(encryption);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<LabellingOptions>(configuration.GetSection(LabellingOptions.SectionName));
        services.AddSingleton(new FrozenCorpusStore(encryption.Layout.EvaluationDirectory));
        services.AddSingleton(sp =>
        {
            var history = sp.GetRequiredService<IOptionsMonitor<HistoryOptions>>();
            var memory = sp.GetRequiredService<IOptionsMonitor<MemoryMcpOptions>>();
            var access = sp.GetRequiredService<RerankerAccess>();
            Func<RerankerOptions, IReranker> reranker = options => RerankEvalSetup.LiveReranker(options, () => access.ApiKey);
            var commands = new Dictionary<string, EvaluationCommand>(StringComparer.Ordinal)
            {
                ["history-eval"] = (run, ct) => IsLabel(run)
                    ? LabelCli.RunAsync("history-eval", run, Labelling(sp, encryption), ct)
                    : HistoryEvalCli.RunInAgentAsync(run.Args, run.Output, run.Protection, history.CurrentValue, memory.CurrentValue, encryption.Layout.MessagesDatabase, reranker, ct),
                ["memory-eval"] = (run, ct) => IsLabel(run)
                    ? LabelCli.RunAsync("memory-eval", run, Labelling(sp, encryption), ct)
                    : MemoryEvalCli.RunInAgentAsync(run.Args, run.Output, run.Protection, memory.CurrentValue, reranker, ct),
                ["eval-corpus"] = (run, ct) => EvalCorpusCli.RunAsync(
                    run,
                    new EvalCorpusContext(
                        encryption.Layout.EvaluationDirectory,
                        encryption.Layout.MessagesDatabase,
                        memory.CurrentValue,
                        history.CurrentValue.EvalArtifactsDirectory,
                        MemoryEvalWorkspace.DefaultDirectory,
                        sp.GetRequiredService<TimeProvider>(),
                        sp.GetService<ILlmClient>(),
                        sp.GetService<Cortex.Contained.Agent.Host.Agent.IModelProvider>()?.MemoryModel),
                    ct),
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

    /// <summary>What <c>label</c> runs with: the configured models through the agent's provider, the main model, the live stores.</summary>
    public static LabelContext Labelling(IServiceProvider services, StorageEncryptionContext encryption)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(encryption);
        var memory = services.GetRequiredService<IOptionsMonitor<MemoryMcpOptions>>().CurrentValue;
        var direct = services.GetRequiredService<DirectLlmClient>();
        return new LabelContext(
            services.GetRequiredService<IOptionsMonitor<LabellingOptions>>().CurrentValue,
            services.GetRequiredService<ILlmClient>(),
            direct.ProviderFor,
            () => services.GetService<Cortex.Contained.Agent.Host.Agent.IModelProvider>()?.DefaultModel,
            encryption.Layout.MessagesDatabase,
            services.GetRequiredService<IOptionsMonitor<HistoryOptions>>().CurrentValue.EvalArtifactsDirectory,
            MemoryEvalWorkspace.DefaultDirectory,
            memory,
            HistoryEvalCli.AgentEmbedder(memory),
            MemoryEvalCli.AgentEmbedder,
            services.GetRequiredService<TimeProvider>());
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

    private static bool IsLabel(EvaluationRun run) => run.Args.Count > 0 && string.Equals(run.Args[0], "label", StringComparison.Ordinal);
}
