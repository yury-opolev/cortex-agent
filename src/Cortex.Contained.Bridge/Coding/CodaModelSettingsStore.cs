using Cortex.Contained.Bridge.Storage;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Runtime-mutable store for coda provider + model selection. Persists to a JSON file.
/// Reads on demand so changes from the web UI are picked up without a restart.
/// </summary>
public sealed class CodaModelSettingsStore : JsonFileSettingsStore<CodaModelSettingsStore.CodaModelSettingsFile>
{
    public CodaModelSettingsStore(string filePath)
        : base(filePath)
    {
    }

    /// <summary>Construct using the default location: <c>%APPDATA%\Cortex\coda-model.json</c>.</summary>
    public static CodaModelSettingsStore Default()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var path = Path.Combine(appData, "Cortex", "coda-model.json");
        return new CodaModelSettingsStore(path);
    }

    /// <summary>Read the current provider/model settings from disk.</summary>
    public CodaModelSettings Get()
    {
        var file = this.Load();
        return new CodaModelSettings(file.Provider, file.Model);
    }

    /// <summary>
    /// Persist the provider and model selection. Blank or whitespace-only values are normalized to null.
    /// </summary>
    public void Set(string? provider, string? model)
    {
        var normalizedProvider = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim();
        var normalizedModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();

        this.Save(new CodaModelSettingsFile
        {
            Provider = normalizedProvider,
            Model = normalizedModel,
        });
    }

    /// <summary>On-disk shape of the coda model settings document.</summary>
    public sealed class CodaModelSettingsFile
    {
        public string? Provider { get; set; }
        public string? Model { get; set; }
    }
}
