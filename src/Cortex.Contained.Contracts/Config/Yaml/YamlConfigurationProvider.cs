using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using YamlDotNet.RepresentationModel;

namespace Cortex.Contained.Contracts.Config.Yaml;

/// <summary>
/// Configuration provider that reads YAML files and supports
/// <c>${ENV_VAR}</c> and <c>${ENV_VAR:-default}</c> environment variable substitution.
/// </summary>
public sealed partial class YamlConfigurationProvider : FileConfigurationProvider
{
    public YamlConfigurationProvider(YamlConfigurationSource source)
        : base(source)
    {
    }

    /// <inheritdoc />
    public override void Load(Stream stream)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StreamReader(stream);
        var yaml = new YamlStream();
        yaml.Load(reader);

        if (yaml.Documents.Count == 0)
        {
            Data = data;
            return;
        }

        var root = yaml.Documents[0].RootNode;
        if (root is YamlMappingNode mapping)
        {
            VisitMapping(mapping, string.Empty, data);
        }

        Data = data;
    }

    private static void VisitMapping(YamlMappingNode mapping, string prefix, Dictionary<string, string?> data)
    {
        foreach (var entry in mapping.Children)
        {
            var key = ((YamlScalarNode)entry.Key).Value ?? string.Empty;
            var fullKey = string.IsNullOrEmpty(prefix)
                ? key
                : string.Create(CultureInfo.InvariantCulture, $"{prefix}:{key}");

            switch (entry.Value)
            {
                case YamlMappingNode childMapping:
                    VisitMapping(childMapping, fullKey, data);
                    break;

                case YamlSequenceNode sequence:
                    VisitSequence(sequence, fullKey, data);
                    break;

                case YamlScalarNode scalar:
                    data[fullKey] = SubstituteEnvironmentVariables(scalar.Value);
                    break;
            }
        }
    }

    private static void VisitSequence(YamlSequenceNode sequence, string prefix, Dictionary<string, string?> data)
    {
        for (var i = 0; i < sequence.Children.Count; i++)
        {
            var indexKey = string.Create(CultureInfo.InvariantCulture, $"{prefix}:{i}");

            switch (sequence.Children[i])
            {
                case YamlMappingNode childMapping:
                    VisitMapping(childMapping, indexKey, data);
                    break;

                case YamlSequenceNode childSequence:
                    VisitSequence(childSequence, indexKey, data);
                    break;

                case YamlScalarNode scalar:
                    data[indexKey] = SubstituteEnvironmentVariables(scalar.Value);
                    break;
            }
        }
    }

    /// <summary>
    /// Substitutes <c>${ENV_VAR}</c> and <c>${ENV_VAR:-default_value}</c> patterns
    /// with the corresponding environment variable values.
    /// </summary>
    internal static string? SubstituteEnvironmentVariables(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return EnvVarPattern().Replace(value, static match =>
        {
            var varName = match.Groups["name"].Value;
            var defaultValue = match.Groups["default"].Success
                ? match.Groups["default"].Value
                : null;

            return Environment.GetEnvironmentVariable(varName) ?? defaultValue ?? match.Value;
        });
    }

    // Matches ${VAR_NAME} and ${VAR_NAME:-default_value}
    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?::-(?<default>[^}]*))?\}", RegexOptions.Compiled)]
    private static partial Regex EnvVarPattern();
}
