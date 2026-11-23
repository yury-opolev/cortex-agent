using Cortex.Contained.Contracts.Config.Yaml;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Keeps the in-container evaluation CLIs (<c>docker exec … history-eval|history-smoke|memory-eval</c>) from
/// copying encrypted stores: such a process never holds the storage key, so on an encrypted install it could
/// only fail or write plaintext. Those commands run as evaluation jobs inside the agent process instead
/// (encrypted-workspace mode, <c>scripts/Invoke-CortexEval.ps1</c> through the Bridge). Commands that read
/// no store stay available: <c>kappa</c> (ids and grades) and the synthetic-text checks of <c>history-smoke</c>.
/// </summary>
public static class EvaluationGuard
{
    /// <summary>Process exit code of a refused command.</summary>
    public const int RefusedExitCode = 3;

    private static readonly HashSet<string> StoreFreeCommands = new(StringComparer.Ordinal)
    {
        "history-eval kappa", "memory-eval kappa", "history-smoke embedder-check", "history-smoke reranker-check",
    };

    /// <summary>True (and an explanation on <paramref name="output"/>) when <paramref name="args"/> (tool, command, …) must not run here.</summary>
    public static async Task<bool> RefuseWhenEncryptedAsync(IConfiguration configuration, IReadOnlyList<string> args, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        var tool = args.Count > 0 ? args[0] : string.Empty;
        if (args.Count > 1 && StoreFreeCommands.Contains(tool + " " + args[1]))
        {
            return false;
        }

        var stateRoot = configuration["CORTEX_STATE_PATH"] ?? "/app/state";
        var dataRoot = configuration["CORTEX_DATA_PATH"] ?? "/app/data";
        var storage = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
        var memory = configuration.GetSection(MemoryMcpOptions.SectionName).Get<MemoryMcpOptions>() ?? new MemoryMcpOptions();
        if (!StorageEncryptionContext.Resolve(storage, StorageLayout.From(stateRoot, dataRoot, memory)).Enabled)
        {
            return false;
        }

        await output.WriteLineAsync(tool == "history-smoke"
            ? "history-smoke is unavailable while encryption at rest is on (only embedder-check and reranker-check run): use history-eval, which runs encrypted inside the agent."
            : $"{tool} cannot run in a separate process while encryption at rest is on: it would need plaintext copies of the encrypted stores. " +
              $"Run it inside the agent instead (encrypted-workspace mode): scripts/Invoke-CortexEval.ps1 -Tool {tool} -- {string.Join(' ', args.Skip(1))}")
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>The configuration the agent image uses outside the web host: <c>appsettings.json</c>, <c>cortex.yml</c> and environment variables.</summary>
    public static IConfiguration CliConfiguration() => new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: true)
        .AddYamlFile("cortex.yml", optional: true)
        .AddEnvironmentVariables()
        .Build();
}
