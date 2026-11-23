using System.Globalization;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Llm;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>What the <c>label</c> command works with (supplied by <see cref="EvaluationRegistration"/>).</summary>
public sealed record LabelContext(
    LabellingOptions Options,
    ILlmClient Llm,
    Func<string, string?> ProviderFor,
    Func<string?> MainModel,
    string MessagesDb,
    string HistoryWorkspace,
    string MemoryWorkspace,
    MemoryMcpOptions Memory,
    Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> HistoryEmbedder,
    Func<MemoryMcpOptions, IEmbeddingService> MemoryEmbedder,
    TimeProvider Time);

/// <summary>
/// <c>history-eval label</c> and <c>memory-eval label</c> (evaluation jobs): <c>[--queries N] [--kappa N] [--estimate]</c>.
/// Copies the store into <c>runs/label-*</c> (encrypted with the evaluation key), labels it with
/// <see cref="Labeller"/> and deletes the copy. The workspace must be initialised (its <c>ablation.json</c> lists
/// the pooled configurations).
/// </summary>
public static class LabelCli
{
    public const string Usage = "usage: <history-eval|memory-eval> label [--queries N] [--kappa N] [--estimate]";

    /// <summary>Runs the command for <paramref name="tool"/>; returns the exit code.</summary>
    public static async Task<int> RunAsync(string tool, EvaluationRun run, LabelContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(context);
        var output = run.Output;
        var options = context.Options;
        if (options.Validate() is { Count: > 0 } errors)
        {
            foreach (var error in errors)
            {
                await output.WriteLineAsync("error: " + error).ConfigureAwait(false);
            }

            return 2;
        }

        var memory = tool == "memory-eval";
        var queries = IntOption(run.Args, "--queries") ?? (memory ? options.MemoryQueries : options.HistoryQueries);
        var kappa = IntOption(run.Args, "--kappa") ?? options.KappaSample;
        if (run.Args.Contains("--estimate", StringComparer.Ordinal))
        {
            var estimate = Labeller.Estimate(queries, options);
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{tool} label: {queries} queries, ~{estimate.Total} LLM calls (writer {estimate.WriterCalls}, main model {estimate.MainCalls}, judge {estimate.JudgeCalls}), ~{estimate.Hours} h; cap {options.MaxCalls}.")).ConfigureAwait(false);
            return 0;
        }

        if (context.MainModel() is not { Length: > 0 } mainModel)
        {
            await output.WriteLineAsync("error: the agent has no main model yet (credentials have not arrived from the Bridge).").ConfigureAwait(false);
            return 2;
        }

        var directory = memory ? context.MemoryWorkspace : context.HistoryWorkspace;
        var calls = new LabellingStateStore(directory, run.Protection).Load()?.Calls ?? 0;
        var llm = new LabellingLlm(context.Llm, context.ProviderFor, options, calls, time: context.Time);
        var runDirectory = Path.Combine(directory, "runs", string.Create(CultureInfo.InvariantCulture, $"label-{context.Time.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}"));
        try
        {
            return memory
                ? await RunMemoryAsync(run, context, llm, mainModel, runDirectory, queries, kappa, cancellationToken).ConfigureAwait(false)
                : await RunHistoryAsync(run, context, llm, mainModel, runDirectory, queries, kappa, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
            return 2;
        }
        finally
        {
            // The copy holds raw text: it never outlives the job.
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(runDirectory))
            {
                Directory.Delete(runDirectory, recursive: true);
            }
        }
    }

    private static async Task<int> RunHistoryAsync(
        EvaluationRun run, LabelContext context, LabellingLlm llm, string mainModel, string runDirectory, int queries, int kappa, CancellationToken cancellationToken)
    {
        var workspace = new EvalWorkspace(context.HistoryWorkspace, run.Protection);
        var configs = workspace.ReadPlan().Configs.Where(c => c.RerankBudget() is null).ToList();
        await using var corpus = await EvalCorpus.CreateAsync(
            context.MessagesDb, runDirectory, new HistoryOptions { Enabled = true }, configs, context.HistoryEmbedder, cancellationToken, protection: run.Protection).ConfigureAwait(false);
        var source = new HistoryLabelSource(corpus, workspace, configs);
        return await new Labeller(source, llm, context.Options, mainModel, workspace.Directory, run.Protection, run.Output, context.Time)
            .RunAsync(queries, kappa, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunMemoryAsync(
        EvaluationRun run, LabelContext context, LabellingLlm llm, string mainModel, string runDirectory, int queries, int kappa, CancellationToken cancellationToken)
    {
        var workspace = new MemoryEvalWorkspace(context.MemoryWorkspace, run.Protection);
        var configs = workspace.ReadPlan().Configs;
        await using var corpus = await MemoryEvalCorpus.CreateAsync(
            context.Memory.DataDirectory, runDirectory, context.Memory, context.MemoryEmbedder(context.Memory), cancellationToken, protection: run.Protection).ConfigureAwait(false);
        var source = new MemoryLabelSource(corpus, workspace, configs, () =>
        {
            if (!File.Exists(context.MessagesDb))
            {
                return null;
            }

            var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(
                context.MessagesDb, run.Protection.SourcePassword(MessageStore.PasswordPurpose), SqliteOpenMode.ReadOnly, pooling: false));
            connection.Open();
            return connection;
        });
        return await new Labeller(source, llm, context.Options, mainModel, workspace.Directory, run.Protection, run.Output, context.Time)
            .RunAsync(queries, kappa, cancellationToken).ConfigureAwait(false);
    }

    private static int? IntOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal) && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var value) && value > 0)
            {
                return value;
            }
        }

        return null;
    }
}
