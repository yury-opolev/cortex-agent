using Microsoft.Extensions.Configuration;

namespace Cortex.Contained.Contracts.Config.Yaml;

/// <summary>
/// Configuration source for YAML files.
/// </summary>
public sealed class YamlConfigurationSource : FileConfigurationSource
{
    /// <inheritdoc />
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new YamlConfigurationProvider(this);
    }
}
