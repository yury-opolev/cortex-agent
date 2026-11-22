using Cortex.Contained.Contracts.Config.Yaml;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Keeps the evaluation commands (<c>history-eval</c>, <c>history-smoke</c>, <c>memory-eval</c>) from making
/// plaintext copies of encrypted stores: their run directories hold raw copies of <c>messages.db</c> and the
/// memory store, so they refuse to run while encryption at rest is on.
/// </summary>
public static class EvaluationGuard
{
    /// <summary>Process exit code of a refused command.</summary>
    public const int RefusedExitCode = 3;

    /// <summary>True (and an explanation on <paramref name="output"/>) when <paramref name="command"/> must not run.</summary>
    public static async Task<bool> RefuseWhenEncryptedAsync(IConfiguration configuration, string command, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(output);
        var stateRoot = configuration["CORTEX_STATE_PATH"] ?? "/app/state";
        var dataRoot = configuration["CORTEX_DATA_PATH"] ?? "/app/data";
        var storage = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
        var memory = configuration.GetSection(MemoryMcpOptions.SectionName).Get<MemoryMcpOptions>() ?? new MemoryMcpOptions();
        if (!StorageEncryptionContext.Resolve(storage, StorageLayout.From(stateRoot, dataRoot, memory)).Enabled)
        {
            return false;
        }

        await output.WriteLineAsync(
            $"{command} is unavailable while encryption at rest is on: its run directories would hold plaintext copies of the encrypted history and memories.")
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
