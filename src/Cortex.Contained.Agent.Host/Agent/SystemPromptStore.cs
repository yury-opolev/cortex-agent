using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Contracts.SystemPrompt;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// File-backed store for <see cref="SystemPromptConfig"/>. Persists one JSON file in the
/// container data volume (per container = per tenant). Missing/corrupt file falls back to
/// defaults. Reads are cached and invalidated on file write-time change. Writes are atomic
/// (temp file + rename) and validated — an invalid config is rejected without persisting.
/// </summary>
public sealed partial class SystemPromptStore
{
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string filePath;
    private readonly ILogger<SystemPromptStore> logger;
    private readonly object cacheLock = new();
    private SystemPromptConfig? cached;
    private DateTime cachedWriteUtc;

    public SystemPromptStore(string filePath, ILogger<SystemPromptStore> logger)
    {
        this.filePath = filePath;
        this.logger = logger;
    }

    /// <summary>Path to the config file (diagnostics).</summary>
    public string FilePath => this.filePath;

    /// <summary>Read the active config, falling back to defaults on any problem.</summary>
    public SystemPromptConfig Read()
    {
        try
        {
            if (File.Exists(this.filePath))
            {
                var writeUtc = File.GetLastWriteTimeUtc(this.filePath);
                lock (this.cacheLock)
                {
                    if (this.cached is not null && this.cachedWriteUtc == writeUtc)
                    {
                        return this.cached;
                    }
                }

                var json = File.ReadAllText(this.filePath);
                var parsed = JsonSerializer.Deserialize<SystemPromptConfig>(json, jsonOptions);
                if (parsed is not null)
                {
                    lock (this.cacheLock)
                    {
                        this.cached = parsed;
                        this.cachedWriteUtc = writeUtc;
                    }

                    return parsed;
                }
            }
        }
#pragma warning disable CA1031 // Corrupt/unreadable config must never crash prompt building
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogReadFailed(this.filePath, ex.Message);
        }

        return SystemPromptDefaults.Create();
    }

    /// <summary>Validate and, if valid, persist atomically.</summary>
    public SystemPromptValidationResult Write(SystemPromptConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var validation = SystemPromptValidator.Validate(config);
        if (!validation.IsValid)
        {
            this.LogWriteRejected(string.Join("; ", validation.Errors));
            return validation;
        }

        try
        {
            var dir = Path.GetDirectoryName(this.filePath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(config, jsonOptions);
            var tmp = this.filePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, this.filePath, overwrite: true);

            lock (this.cacheLock)
            {
                this.cached = config;
                this.cachedWriteUtc = File.GetLastWriteTimeUtc(this.filePath);
            }
        }
#pragma warning disable CA1031 // Surface write failure as an error result, do not crash
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogWriteFailed(this.filePath, ex.Message);
            validation.IsValid = false;
            validation.Errors.Add($"Failed to persist system-prompt config: {ex.Message}");
        }

        return validation;
    }

    /// <summary>Reset to defaults and persist.</summary>
    public SystemPromptConfig Reset()
    {
        var defaults = SystemPromptDefaults.Create();
        this.Write(defaults);
        return defaults;
    }

    /// <summary>Stable 8-hex-char fingerprint of the active config (telemetry correlation).</summary>
    public string Fingerprint()
    {
        var c = this.Read();
        var material = string.Concat(
            c.MainTemplate, c.SubagentTemplate, c.VoiceMode, c.CodingRelay, c.SubagentInstructions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "[system-prompt] read failed for {Path}: {Reason}; using defaults")]
    private partial void LogReadFailed(string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[system-prompt] write rejected (invalid): {Errors}")]
    private partial void LogWriteRejected(string errors);

    [LoggerMessage(Level = LogLevel.Error, Message = "[system-prompt] write failed for {Path}: {Reason}")]
    private partial void LogWriteFailed(string path, string reason);
}
