using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace Cortex.Contained.Contracts.Config.Yaml;

/// <summary>
/// Extension methods for adding YAML configuration sources to <see cref="IConfigurationBuilder"/>.
/// </summary>
public static class YamlConfigurationExtensions
{
    /// <summary>
    /// Adds a YAML configuration file at <paramref name="path"/>.
    /// </summary>
    public static IConfigurationBuilder AddYamlFile(
        this IConfigurationBuilder builder,
        string path,
        bool optional = false,
        bool reloadOnChange = false)
    {
        return AddYamlFile(builder, provider: null, path, optional, reloadOnChange);
    }

    /// <summary>
    /// Adds a YAML configuration file at <paramref name="path"/> with an explicit file provider.
    /// </summary>
    public static IConfigurationBuilder AddYamlFile(
        this IConfigurationBuilder builder,
        IFileProvider? provider,
        string path,
        bool optional,
        bool reloadOnChange)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return builder.Add<YamlConfigurationSource>(source =>
        {
            source.FileProvider = provider;
            source.Path = path;
            source.Optional = optional;
            source.ReloadOnChange = reloadOnChange;
            source.ResolveFileProvider();
        });
    }
}
