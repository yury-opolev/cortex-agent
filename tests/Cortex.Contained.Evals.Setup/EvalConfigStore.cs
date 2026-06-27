using System.Globalization;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Common.Security;

namespace Cortex.Contained.Evals.Setup;

/// <summary>
/// Reads and writes eval LLM configuration. Config is stored at
/// <c>%LOCALAPPDATA%\Cortex\eval.yml</c> and API keys are encrypted
/// in <c>%LOCALAPPDATA%\Cortex\secrets\eval-secrets.json</c> via DPAPI.
/// Completely separate from the Bridge's production credentials.
/// </summary>
public sealed class EvalConfigStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly string _evalYamlPath;
    private readonly string _evalSecretsPath;
    private readonly ISecretStore _secretStore;

    public EvalConfigStore(ISecretStore? secretStore = null)
    {
        var cortexDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cortex");
        _evalYamlPath = Path.Combine(cortexDir, "eval.yml");
        _evalSecretsPath = Path.Combine(cortexDir, "secrets", "eval-secrets.json");
        _secretStore = secretStore ?? new DpapiSecretStore();
    }

    /// <summary>Loads the current eval configuration. Returns defaults if not yet configured.</summary>
    public EvalConfig Load()
    {
        if (!File.Exists(_evalYamlPath))
            return new EvalConfig();

        // Simple line-based YAML parsing (same pattern as the rest of the codebase)
        var lines = File.ReadAllLines(_evalYamlPath);
        var config = new EvalConfig { IsConfigured = true };

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("providerName:", StringComparison.OrdinalIgnoreCase))
                config.ProviderName = ExtractValue(trimmed);
            else if (trimmed.StartsWith("api:", StringComparison.OrdinalIgnoreCase))
                config.Api = ExtractValue(trimmed);
            else if (trimmed.StartsWith("baseUrl:", StringComparison.OrdinalIgnoreCase))
                config.BaseUrl = ExtractValue(trimmed);
            else if (trimmed.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                config.Model = ExtractValue(trimmed);
        }

        return config;
    }

    /// <summary>Loads the decrypted API key. Returns null if not stored.</summary>
    public string? LoadApiKey()
    {
        if (!File.Exists(_evalSecretsPath))
            return null;

        try
        {
            var json = File.ReadAllText(_evalSecretsPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ApiKey", out var val))
            {
                var encrypted = val.GetString();
                return !string.IsNullOrEmpty(encrypted) ? _secretStore.Unprotect(encrypted) : null;
            }
        }
        catch
        {
            // Corrupted file or decryption failure
        }
        return null;
    }

    /// <summary>Saves eval configuration and encrypted API key.</summary>
    public void Save(EvalConfig config, string? apiKey)
    {
        // Write eval.yml
        var dir = Path.GetDirectoryName(_evalYamlPath);
        if (dir is not null) Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine("# Eval LLM provider configuration");
        sb.AppendLine("# Credentials are stored separately in eval-secrets.json (DPAPI encrypted)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"providerName: {config.ProviderName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"api: {config.Api}");
        if (!string.IsNullOrEmpty(config.BaseUrl))
            sb.AppendLine(CultureInfo.InvariantCulture, $"baseUrl: {config.BaseUrl}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"model: {config.Model}");

        File.WriteAllText(_evalYamlPath, sb.ToString());

        // Write encrypted API key
        if (!string.IsNullOrEmpty(apiKey))
        {
            var secretsDir = Path.GetDirectoryName(_evalSecretsPath);
            if (secretsDir is not null) Directory.CreateDirectory(secretsDir);

            var encrypted = _secretStore.Protect(apiKey);
            var secretsObj = new { ApiKey = encrypted };
            File.WriteAllText(_evalSecretsPath, JsonSerializer.Serialize(secretsObj, s_jsonOptions));
        }
    }

    /// <summary>Returns true if eval credentials have been configured.</summary>
    public bool IsConfigured() => File.Exists(_evalYamlPath);

    private static string ExtractValue(string line)
    {
        var idx = line.IndexOf(':', StringComparison.Ordinal);
        if (idx < 0) return string.Empty;
        return line[(idx + 1)..].Trim().Trim('"');
    }
}
