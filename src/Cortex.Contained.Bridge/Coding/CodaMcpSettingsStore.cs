using System.Text.Json.Serialization;
using Cortex.Contained.Bridge.Storage;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Runtime-mutable store for the coda MCP policy selection (set from the web UI). Persists to a JSON
/// file and reads on demand, so changes are picked up without a restart. Takes precedence over the
/// <c>Coding:Coda:Mcp</c> value in cortex.yml.
/// </summary>
public sealed class CodaMcpSettingsStore : JsonFileSettingsStore<CodaMcpSettingsStore.CodaMcpSettingsFile>
{
    public CodaMcpSettingsStore(string filePath)
        : base(filePath)
    {
    }

    /// <summary>Construct using the default location: <c>%APPDATA%\Cortex\coda-mcp.json</c>.</summary>
    public static CodaMcpSettingsStore Default()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new CodaMcpSettingsStore(Path.Combine(appData, "Cortex", "coda-mcp.json"));
    }

    /// <summary>Read the current MCP policy settings from disk. Null policy → not set (use YAML default).</summary>
    public CodaMcpSettings Get()
    {
        var file = this.Load();
        return new CodaMcpSettings(file.Mcp, file.CuratedMcpDir);
    }

    /// <summary>Persist the MCP policy and curated directory. A blank directory is normalized to null.</summary>
    public void Set(CodaMcpPolicy? mcp, string? curatedMcpDir)
    {
        this.Save(new CodaMcpSettingsFile
        {
            Mcp = mcp,
            CuratedMcpDir = string.IsNullOrWhiteSpace(curatedMcpDir) ? null : curatedMcpDir.Trim(),
        });
    }

    /// <summary>On-disk shape of the coda MCP settings document.</summary>
    public sealed class CodaMcpSettingsFile
    {
        /// <summary>Serialized as the policy name (host/curated/off) so the file is human-editable.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public CodaMcpPolicy? Mcp { get; set; }

        public string? CuratedMcpDir { get; set; }
    }
}
