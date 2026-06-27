using Cortex.Contained.Contracts.Config.Yaml;
using Microsoft.Extensions.Configuration;

namespace Cortex.Contained.Contracts.Tests;

public class YamlEnvironmentSubstitutionTests
{
    [Fact]
    public void SubstituteEnvironmentVariables_NullValue_ReturnsNull()
    {
        var result = YamlConfigurationProvider.SubstituteEnvironmentVariables(null);
        Assert.Null(result);
    }

    [Fact]
    public void SubstituteEnvironmentVariables_EmptyString_ReturnsEmpty()
    {
        var result = YamlConfigurationProvider.SubstituteEnvironmentVariables(string.Empty);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void SubstituteEnvironmentVariables_NoPattern_ReturnsOriginal()
    {
        var result = YamlConfigurationProvider.SubstituteEnvironmentVariables("plain value");
        Assert.Equal("plain value", result);
    }

    [Fact]
    public void SubstituteEnvironmentVariables_ExistingEnvVar_ReplacesValue()
    {
        const string varName = "CORTEX_TEST_YAML_VAR_1";
        Environment.SetEnvironmentVariable(varName, "resolved-value");
        try
        {
            var result = YamlConfigurationProvider.SubstituteEnvironmentVariables("${CORTEX_TEST_YAML_VAR_1}");
            Assert.Equal("resolved-value", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void SubstituteEnvironmentVariables_MissingEnvVar_WithDefault_ReturnsDefault()
    {
        // Ensure variable doesn't exist
        Environment.SetEnvironmentVariable("CORTEX_TEST_MISSING_VAR", null);

        var result = YamlConfigurationProvider.SubstituteEnvironmentVariables("${CORTEX_TEST_MISSING_VAR:-fallback}");
        Assert.Equal("fallback", result);
    }

    [Fact]
    public void SubstituteEnvironmentVariables_MissingEnvVar_NoDefault_KeepsPattern()
    {
        Environment.SetEnvironmentVariable("CORTEX_TEST_MISSING_VAR_2", null);

        var result = YamlConfigurationProvider.SubstituteEnvironmentVariables("${CORTEX_TEST_MISSING_VAR_2}");
        Assert.Equal("${CORTEX_TEST_MISSING_VAR_2}", result);
    }

    [Fact]
    public void SubstituteEnvironmentVariables_ExistingEnvVar_IgnoresDefault()
    {
        const string varName = "CORTEX_TEST_YAML_VAR_2";
        Environment.SetEnvironmentVariable(varName, "actual");
        try
        {
            var result = YamlConfigurationProvider.SubstituteEnvironmentVariables("${CORTEX_TEST_YAML_VAR_2:-fallback}");
            Assert.Equal("actual", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void SubstituteEnvironmentVariables_MultipleVars_AllResolved()
    {
        Environment.SetEnvironmentVariable("CORTEX_TEST_A", "alpha");
        Environment.SetEnvironmentVariable("CORTEX_TEST_B", "beta");
        try
        {
            var result = YamlConfigurationProvider.SubstituteEnvironmentVariables("${CORTEX_TEST_A}:${CORTEX_TEST_B}");
            Assert.Equal("alpha:beta", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CORTEX_TEST_A", null);
            Environment.SetEnvironmentVariable("CORTEX_TEST_B", null);
        }
    }

    [Fact]
    public void SubstituteEnvironmentVariables_EmptyDefault_ReturnsEmpty()
    {
        Environment.SetEnvironmentVariable("CORTEX_TEST_EMPTY_DEFAULT", null);

        var result = YamlConfigurationProvider.SubstituteEnvironmentVariables("${CORTEX_TEST_EMPTY_DEFAULT:-}");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void SubstituteEnvironmentVariables_MixedTextAndVars_ResolvedCorrectly()
    {
        Environment.SetEnvironmentVariable("CORTEX_TEST_HOST", "myhost");
        try
        {
            var result = YamlConfigurationProvider.SubstituteEnvironmentVariables("http://${CORTEX_TEST_HOST}:5000/api");
            Assert.Equal("http://myhost:5000/api", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CORTEX_TEST_HOST", null);
        }
    }
}

public class YamlConfigurationProviderTests
{
    private static IConfigurationRoot BuildConfigFromYaml(string yamlContent)
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, yamlContent);
        try
        {
            var builder = new ConfigurationBuilder();
            builder.AddYamlFile(tempFile, optional: false, reloadOnChange: false);
            return builder.Build();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Load_SimpleScalarValues_FlattensCorrectly()
    {
        var config = BuildConfigFromYaml("""
            name: Cortex
            port: "5080"
            """);

        Assert.Equal("Cortex", config["name"]);
        Assert.Equal("5080", config["port"]);
    }

    [Fact]
    public void Load_NestedMapping_UsesColonSeparation()
    {
        var config = BuildConfigFromYaml("""
            webui:
              enabled: "true"
              port: "8080"
              bind: 127.0.0.1
            """);

        Assert.Equal("true", config["webui:enabled"]);
        Assert.Equal("8080", config["webui:port"]);
        Assert.Equal("127.0.0.1", config["webui:bind"]);
    }

    [Fact]
    public void Load_DeeplyNested_FlattensAllLevels()
    {
        var config = BuildConfigFromYaml("""
            level1:
              level2:
                level3:
                  value: deep
            """);

        Assert.Equal("deep", config["level1:level2:level3:value"]);
    }

    [Fact]
    public void Load_SequenceOfScalars_UsesIndexKeys()
    {
        var config = BuildConfigFromYaml("""
            models:
              - gpt-4o
              - gpt-4o-mini
              - claude-sonnet
            """);

        Assert.Equal("gpt-4o", config["models:0"]);
        Assert.Equal("gpt-4o-mini", config["models:1"]);
        Assert.Equal("claude-sonnet", config["models:2"]);
    }

    [Fact]
    public void Load_SequenceOfMappings_FlattensCorrectly()
    {
        var config = BuildConfigFromYaml("""
            providers:
              - name: openai
                api: openai-completions
              - name: anthropic
                api: anthropic-messages
            """);

        Assert.Equal("openai", config["providers:0:name"]);
        Assert.Equal("openai-completions", config["providers:0:api"]);
        Assert.Equal("anthropic", config["providers:1:name"]);
        Assert.Equal("anthropic-messages", config["providers:1:api"]);
    }

    [Fact]
    public void Load_EmptyDocument_ProducesNoKeys()
    {
        var config = BuildConfigFromYaml(string.Empty);

        Assert.DoesNotContain(config.AsEnumerable(), kv => kv.Value is not null);
    }

    [Fact]
    public void Load_EnvVarSubstitution_ReplacesValues()
    {
        const string varName = "CORTEX_TEST_YAML_LOAD";
        Environment.SetEnvironmentVariable(varName, "substituted");
        try
        {
            var config = BuildConfigFromYaml("""
                token: ${CORTEX_TEST_YAML_LOAD}
                """);

            Assert.Equal("substituted", config["token"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Load_EnvVarWithDefault_UsesDefaultWhenMissing()
    {
        Environment.SetEnvironmentVariable("CORTEX_TEST_YAML_UNSET", null);

        var config = BuildConfigFromYaml("""
            url: ${CORTEX_TEST_YAML_UNSET:-http://localhost:5100}
            """);

        Assert.Equal("http://localhost:5100", config["url"]);
    }

    [Fact]
    public void Load_CaseInsensitiveKeys()
    {
        var config = BuildConfigFromYaml("""
            MyKey: value
            """);

        Assert.Equal("value", config["mykey"]);
        Assert.Equal("value", config["MYKEY"]);
        Assert.Equal("value", config["MyKey"]);
    }

    [Fact]
    public void Load_NestedSequenceInSequence_FlattensCorrectly()
    {
        var config = BuildConfigFromYaml("""
            matrix:
              - - a
                - b
              - - c
                - d
            """);

        Assert.Equal("a", config["matrix:0:0"]);
        Assert.Equal("b", config["matrix:0:1"]);
        Assert.Equal("c", config["matrix:1:0"]);
        Assert.Equal("d", config["matrix:1:1"]);
    }
}

public class YamlConfigurationExtensionsTests
{
    [Fact]
    public void AddYamlFile_NullBuilder_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            YamlConfigurationExtensions.AddYamlFile(null!, "test.yml"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddYamlFile_InvalidPath_ThrowsArgumentException(string? path)
    {
        var builder = new ConfigurationBuilder();
        Assert.ThrowsAny<ArgumentException>(() =>
            builder.AddYamlFile(path!));
    }

    [Fact]
    public void AddYamlFile_OptionalMissingFile_DoesNotThrow()
    {
        var builder = new ConfigurationBuilder();
        builder.AddYamlFile("nonexistent-file-12345.yml", optional: true);

        var config = builder.Build();
        Assert.DoesNotContain(config.AsEnumerable(), kv => kv.Value is not null);
    }

    [Fact]
    public void AddYamlFile_RequiredMissingFile_ThrowsOnBuild()
    {
        var builder = new ConfigurationBuilder();
        builder.AddYamlFile("nonexistent-file-12345.yml", optional: false);

        Assert.ThrowsAny<Exception>(() => builder.Build());
    }
}
