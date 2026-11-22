using System.Text.Json;
using System.Text.Json.Serialization;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Primitives;

namespace Cortex.Contained.Agent.Host.Recall;

/// <summary>Outcome of <see cref="RecallSettingsStore.Apply"/>.</summary>
/// <param name="Applied">False when validation or persistence failed; the overrides are unchanged.</param>
/// <param name="Errors">Validation or persistence errors.</param>
/// <param name="Previous">Overrides before the update.</param>
/// <param name="Current">Overrides after the update.</param>
public sealed record RecallSettingsApplyResult(bool Applied, IReadOnlyList<string> Errors, RecallOverrides Previous, RecallOverrides Current);

/// <summary>
/// File-backed runtime overrides for the recall stack (<c>&lt;state&gt;/recall-settings.json</c>, one per
/// agent container = per tenant). It lives in the state volume, outside the tool sandbox, so the agent
/// cannot change it and Self-Update, image upgrades and the Launcher's compose-file sync keep it.
/// Option post-configures read <see cref="Current"/>; <see cref="GetChangeToken"/> fires on every
/// applied update so <c>IOptionsMonitor&lt;T&gt;.CurrentValue</c> picks the change up live.
/// A missing or unreadable file means no overrides.
/// </summary>
public sealed partial class RecallSettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string filePath;
    private readonly ILogger<RecallSettingsStore> logger;
    private readonly Lock syncLock = new();
    private RecallOverrides current;
    private CancellationTokenSource changeTokenSource = new();

    public RecallSettingsStore(string filePath, ILogger<RecallSettingsStore> logger)
    {
        this.filePath = filePath;
        this.logger = logger;
        this.current = this.Load();
    }

    /// <summary>The persisted overrides.</summary>
    public RecallOverrides Current
    {
        get
        {
            lock (this.syncLock)
            {
                return this.current;
            }
        }
    }

    /// <summary>Validates <paramref name="patch"/>, persists the merged overrides atomically and signals the change.</summary>
    public RecallSettingsApplyResult Apply(RecallSettingsPatchDto patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var errors = RecallSettingsValidation.TryParse(patch, out var parsed);
        CancellationTokenSource? fired = null;
        RecallSettingsApplyResult result;
        lock (this.syncLock)
        {
            var previous = this.current;
            if (errors.Count > 0)
            {
                return new RecallSettingsApplyResult(false, errors, previous, previous);
            }

            var next = (patch.ResetToDefaults ? RecallOverrides.None : previous).With(parsed);
            try
            {
                this.Persist(next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                this.LogWriteFailed(this.filePath, ex.Message);
                return new RecallSettingsApplyResult(false, [$"Could not save the recall settings: {ex.Message}"], previous, previous);
            }

            this.current = next;
            fired = this.changeTokenSource;
            this.changeTokenSource = new CancellationTokenSource();
            result = new RecallSettingsApplyResult(true, [], previous, next);
        }

        fired.Cancel();
        fired.Dispose();
        var overridden = string.Join(", ", result.Current.Names());
        this.LogApplied(overridden);
        return result;
    }

    /// <summary>A token that fires when the overrides change.</summary>
    public IChangeToken GetChangeToken()
    {
        lock (this.syncLock)
        {
            return new CancellationChangeToken(this.changeTokenSource.Token);
        }
    }

    /// <inheritdoc />
    public void Dispose() => this.changeTokenSource.Dispose();

    private RecallOverrides Load()
    {
        try
        {
            if (File.Exists(this.filePath))
            {
                var loaded = JsonSerializer.Deserialize<RecallOverrides>(File.ReadAllText(this.filePath), json) ?? RecallOverrides.None;
                var clean = RecallSettingsValidation.Sanitize(loaded, out var dropped);
                if (dropped.Count > 0)
                {
                    this.LogInvalidDropped(this.filePath, string.Join(", ", dropped));
                }

                return clean;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            this.LogReadFailed(this.filePath, ex.Message);
        }

        return RecallOverrides.None;
    }

    private void Persist(RecallOverrides overrides)
    {
        var directory = Path.GetDirectoryName(this.filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = this.filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(overrides, json));
        File.Move(temp, this.filePath, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "[recall-settings] read failed for {Path}: {Reason}; using the configured defaults")]
    private partial void LogReadFailed(string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[recall-settings] {Path} holds invalid values for {Names}; they are ignored and the configured defaults apply")]
    private partial void LogInvalidDropped(string path, string names);

    [LoggerMessage(Level = LogLevel.Error, Message = "[recall-settings] write failed for {Path}: {Reason}")]
    private partial void LogWriteFailed(string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "[recall-settings] applied; overridden: {Overridden}")]
    private partial void LogApplied(string overridden);
}
