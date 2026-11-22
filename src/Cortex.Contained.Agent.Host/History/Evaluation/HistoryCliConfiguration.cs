using Cortex.Contained.Contracts.Config.Yaml;
using Microsoft.Extensions.Configuration;

namespace Cortex.Contained.Agent.Host.History.Evaluation;

/// <summary>
/// The <c>History</c> settings as the agent sees them (environment, then <c>cortex.yml</c>, like
/// <c>Program.cs</c>), for the evaluation CLIs that run before the host is built.
/// </summary>
internal static class HistoryCliConfiguration
{
    /// <summary>Loads <c>History</c> from the environment and <c>cortex.yml</c>.</summary>
    public static HistoryOptions Load() =>
        From(new ConfigurationBuilder().AddEnvironmentVariables().AddYamlFile("cortex.yml", optional: true).Build());

    /// <summary>Binds <c>History</c> from <paramref name="configuration"/>.</summary>
    public static HistoryOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new HistoryOptions();
        configuration.GetSection(HistoryOptions.SectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// A warning when <paramref name="directory"/> is not exactly the configured artifacts directory
    /// (purge erases only the <c>runs</c> directory of the configured one), otherwise null.
    /// </summary>
    public static string? ArtifactsWarning(string directory, string configured, string setting)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(Normalize(directory), Normalize(configured), comparison)
            ? null
            : $"warning: {directory} is not the configured {setting} ({configured}); its runs hold raw copies of messages.db and are not covered by purge erasure. Delete them yourself after use.";
    }
}
