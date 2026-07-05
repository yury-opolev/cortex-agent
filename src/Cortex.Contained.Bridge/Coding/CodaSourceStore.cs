using System.Text.Json.Serialization;
using Cortex.Contained.Bridge.Storage;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Runtime-mutable store for the coda binary source (Auto/Host/Bundled), set from the web UI.
/// Persists to JSON and reads on demand (no restart). Overrides the <c>Coding:Coda:Source</c>
/// value in cortex.yml when set. Mirrors <see cref="CodaMcpSettingsStore"/>.
/// </summary>
public sealed class CodaSourceStore : JsonFileSettingsStore<CodaSourceStore.CodaSourceFile>
{
    public CodaSourceStore(string filePath)
        : base(filePath)
    {
    }

    /// <summary>Default location: <c>%APPDATA%\Cortex\coda-source.json</c>.</summary>
    public static CodaSourceStore Default()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new CodaSourceStore(Path.Combine(appData, "Cortex", "coda-source.json"));
    }

    /// <summary>Current source override, or null when unset (use the YAML default).</summary>
    public CodaSource? Get() => this.Load().Source;

    /// <summary>Persist the source override (null clears it).</summary>
    public void Set(CodaSource? source) => this.Save(new CodaSourceFile { Source = source });

    /// <summary>On-disk shape.</summary>
    public sealed class CodaSourceFile
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public CodaSource? Source { get; set; }
    }
}
