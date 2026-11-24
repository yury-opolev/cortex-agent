using Cortex.Contained.Agent.Host.Evaluation.Calibration;
using Cortex.Contained.Agent.Host.Evaluation.Frozen;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Evaluation.Orchestration;
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
    /// <summary>Registers <see cref="EvaluationJobService"/>, <see cref="EvaluationFiles"/>, the labelling options and the evaluation run.</summary>
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
                ["judge-calibration"] = (run, ct) => JudgeCalibrationCli.RunAsync(run, Calibration(sp, encryption), ct),
                ["eval-corpus"] = async (run, ct) =>
                {
                    // suggest --llm needs the provider: after an agent restart the credentials arrive a little later.
                    if (IsSuggestLlm(run))
                    {
                        var direct = sp.GetRequiredService<DirectLlmClient>();
                        var models = sp.GetService<Cortex.Contained.Agent.Host.Agent.IModelProvider>();
                        await CredentialsGate.WaitAsync(() => direct.HasCredentials && !string.IsNullOrEmpty(models?.MemoryModel), null, null, sp.GetRequiredService<TimeProvider>(), ct).ConfigureAwait(false);
                    }

                    return await EvalCorpusCli.RunAsync(
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
                        ct).ConfigureAwait(false);
                },
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
        services.AddSingleton(sp => new GradingService(
            Workspaces(sp.GetRequiredService<IOptionsMonitor<HistoryOptions>>().CurrentValue, encryption.Layout),
            EvaluationJobService.ProtectionFor(encryption, sp.GetRequiredService<StorageKeyRing>())));
        services.AddSingleton(sp =>
        {
            var labelling = sp.GetRequiredService<IOptionsMonitor<LabellingOptions>>();
            var reranker = sp.GetRequiredService<RerankerAccess>();
            var environment = new RunEnvironment(
                sp.GetRequiredService<IOptionsMonitor<HistoryOptions>>().CurrentValue.EvalArtifactsDirectory,
                MemoryEvalWorkspace.DefaultDirectory,
                encryption.Layout.EvaluationDirectory,
                EvaluationJobService.ProtectionFor(encryption, sp.GetRequiredService<StorageKeyRing>()),
                () => reranker.Enabled,
                () => labelling.CurrentValue);
            return new EvaluationRunService(
                sp.GetRequiredService<EvaluationJobService>(),
                sp.GetRequiredService<GradingService>(),
                environment,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<EvaluationRunService>>());
        });
        services.AddHostedService(sp => sp.GetRequiredService<EvaluationRunService>());
        services.AddSingleton(sp => new EvaluationRunApi(
            sp.GetRequiredService<EvaluationRunService>(),
            sp.GetRequiredService<GradingService>(),
            () => sp.GetRequiredService<IOptionsMonitor<LabellingOptions>>().CurrentValue));
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
            services.GetRequiredService<TimeProvider>(),
            () => direct.HasCredentials);
    }

    /// <summary>What <c>judge-calibration</c> runs with: the labelling options and provider, the calibration root and the workspaces.</summary>
    public static CalibrationContext Calibration(IServiceProvider services, StorageEncryptionContext encryption)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(encryption);
        var direct = services.GetRequiredService<DirectLlmClient>();
        return new CalibrationContext(
            services.GetRequiredService<IOptionsMonitor<LabellingOptions>>().CurrentValue,
            services.GetRequiredService<ILlmClient>(),
            direct.ProviderFor,
            CalibrationArchive.RootIn(encryption.Layout.EvaluationDirectory),
            Workspaces(services.GetRequiredService<IOptionsMonitor<HistoryOptions>>().CurrentValue, encryption.Layout),
            services.GetRequiredService<TimeProvider>(),
            () => direct.HasCredentials);
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

    private static bool IsSuggestLlm(EvaluationRun run) =>
        run.Args.Count > 0 && string.Equals(run.Args[0], "suggest", StringComparison.Ordinal) && run.Args.Contains("--llm", StringComparer.Ordinal);

    private static bool IsLabel(EvaluationRun run) => run.Args.Count > 0 && string.Equals(run.Args[0], "label", StringComparison.Ordinal);
}
