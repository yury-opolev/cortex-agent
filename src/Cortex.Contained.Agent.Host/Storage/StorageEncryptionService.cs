using System.Globalization;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// The owner's view of and switch for encryption at rest (status, pre-flight preview, enable). Enabling is
/// one-way: it requires the key the Bridge just pushed (by fingerprint), erases the plaintext evaluation
/// copies on request, writes the marker and restarts the agent, which migrates every covered store on the
/// next start (<see cref="EncryptedStorageOpener"/>).
/// </summary>
public sealed partial class StorageEncryptionService
{
    /// <summary>Head room on the state volume besides the largest database and the history index.</summary>
    internal const long MigrationMarginBytes = 64L * 1024 * 1024;

    /// <summary>Delay between the enable response and the self-restart, so the reply reaches the Bridge.</summary>
    internal static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    private readonly StorageEncryptionContext context;
    private readonly StorageKeyRing keys;
    private readonly IStorageState messages;
    private readonly StorageInventory inventory;
    private readonly IDiskSpace disk;
    private readonly IHostApplicationLifetime lifetime;
    private readonly TimeProvider time;
    private readonly ILogger<StorageEncryptionService> logger;
    private readonly EncryptedMemoryStore? memory;
    private readonly bool selfRestart;

    public StorageEncryptionService(
        StorageEncryptionContext context,
        StorageKeyRing keys,
        IStorageState messages,
        StorageInventory inventory,
        IDiskSpace disk,
        IHostApplicationLifetime lifetime,
        TimeProvider time,
        ILogger<StorageEncryptionService> logger,
        EncryptedMemoryStore? memory = null,
        bool selfRestart = false)
    {
        this.context = context;
        this.keys = keys;
        this.messages = messages;
        this.inventory = inventory;
        this.disk = disk;
        this.lifetime = lifetime;
        this.time = time;
        this.logger = logger;
        this.memory = memory;
        this.selfRestart = selfRestart;
    }

    /// <summary>Encryption mode, key state and the state of every stored item.</summary>
    public StorageEncryptionStatusDto GetStatus()
    {
        var covered = this.inventory.Covered();
        var health = this.GetHealth();
        List<StorageItemDto> items = [.. covered];
        if (this.inventory.Evaluation() is { } evaluation)
        {
            items.Add(evaluation);
        }

        items.AddRange(this.inventory.NotCovered());
        return new StorageEncryptionStatusDto
        {
            EncryptAtRest = health.EncryptAtRest,
            Source = health.Source,
            RestartPending = health.RestartPending,
            EnabledAt = this.context.Marker?.EnabledAt,
            KeyState = health.KeyState,
            KeyFingerprint = this.keys.Fingerprint,
            ExpectedKeyFingerprint = this.context.Marker is { KeyFingerprint.Length: > 0 } marker ? marker.KeyFingerprint : null,
            HasEncryptedData = covered.Any(i => i.State is StorageItemStates.Encrypted or StorageItemStates.Mixed),
            Unavailable = covered.Any(i => i.State == StorageItemStates.Unknown),
            Error = covered.Any(i => i.State == StorageItemStates.Unknown) ? "Some stores could not be read; their state is unknown." : null,
            MessagesState = health.MessagesState,
            MemoryState = health.MemoryState,
            Items = items,
        };
    }

    /// <summary>Like <see cref="GetStatus"/>, but a failure is reported as an unavailable status instead of an exception.</summary>
    public StorageEncryptionStatusDto GetStatusOrUnavailable()
    {
        try
        {
            return this.GetStatus();
        }
#pragma warning disable CA1031 // A status the Bridge cannot get must not look like "no encrypted data": report the failure as a flag
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogStatusUnavailable(ex.GetType().Name);
            return new StorageEncryptionStatusDto { Unavailable = true, Error = "The storage status could not be read (" + ex.GetType().Name + ")." };
        }
#pragma warning restore CA1031
    }

    /// <summary>The cheap part of <see cref="GetStatus"/> for <c>/health</c> (no file scan, no fingerprints).</summary>
    public StorageHealth GetHealth() => new(
        this.context.Enabled,
        this.context.Source,
        this.context.RestartPending,
        // RejectedForData is sticky: an accepted key always wins over an earlier wrong one.
        this.keys.IsAvailable ? StorageKeyStates.Accepted : this.keys.RejectedForData ? StorageKeyStates.Rejected : StorageKeyStates.Absent,
        this.context.Enabled ? StateName(this.messages.State) : "plaintext",
        this.memory is null ? "plaintext" : StateName(this.memory.State));

    /// <summary>What enabling would do, and whether it can run now. Changes nothing.</summary>
    public StorageEncryptionPreviewDto Preview()
    {
        var covered = this.inventory.Covered();
        var evaluation = this.inventory.Evaluation();
        var notCovered = this.inventory.NotCovered();
        var largestDatabase = covered.Where(i => i.Id is "messages" or "memory-db").Select(i => i.Bytes).DefaultIfEmpty(0).Max();
        var indexBytes = covered.Where(i => i.Id == "history-index").Sum(i => i.Bytes);
        var requiredFree = largestDatabase + indexBytes + MigrationMarginBytes;
        var availableFree = this.disk.AvailableBytes(this.context.Layout.StateRoot);
        var availableTemp = this.disk.AvailableBytes(Path.GetTempPath());

        var blockers = new List<string>();
        if (availableFree >= 0 && availableFree < requiredFree)
        {
            blockers.Add($"Not enough free space on the state volume: {Megabytes(requiredFree)} needed, {Megabytes(availableFree)} free.");
        }

        if (availableTemp >= 0 && availableTemp < largestDatabase)
        {
            blockers.Add($"Not enough free space in the agent's temporary directory: {Megabytes(largestDatabase)} needed, {Megabytes(availableTemp)} free.");
        }

        var warnings = new List<string>
        {
            "The agent restarts to apply encryption and encrypts its stores on that start; chat pauses meanwhile (longer for a large history).",
            "Encryption cannot be turned off from the UI, and agent images older than this version cannot read encrypted stores: do not roll back.",
            "Keep the recovery kit somewhere safe: without it, losing this Windows profile (DPAPI) loses every message and memory.",
        };
        if (indexBytes > 0)
        {
            warnings.Add("The history search index is rebuilt encrypted; until that finishes, history search covers recent messages only.");
        }

        if (evaluation is not null)
        {
            warnings.Add("Evaluation runs hold plaintext copies of your history and memories; erase them when enabling.");
        }

        if (notCovered.Count > 0)
        {
            warnings.Add("Some stores stay in plaintext; they are listed under \"Not covered\".");
        }

        return new StorageEncryptionPreviewDto
        {
            AlreadyEnabled = this.context.Enabled || this.context.RestartPending,
            KeyFingerprint = this.keys.Fingerprint,
            WillEncrypt = covered.Where(i => i.State != StorageItemStates.Absent).ToList(),
            WillErase = evaluation is null ? [] : [evaluation],
            NotCovered = notCovered,
            RequiredFreeBytes = requiredFree,
            AvailableFreeBytes = availableFree,
            RequiredTempBytes = largestDatabase,
            AvailableTempBytes = availableTemp,
            Blockers = blockers,
            Warnings = warnings,
        };
    }

    /// <summary>Turns encryption on (one-way) and schedules the restart that applies it.</summary>
    public StorageEncryptionEnableResultDto Enable(StorageEncryptionEnableRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (this.context.Enabled)
        {
            return new StorageEncryptionEnableResultDto { Accepted = true, AlreadyEnabled = true };
        }

        if (this.context.RestartPending)
        {
            return new StorageEncryptionEnableResultDto { Accepted = true, RestartScheduled = this.selfRestart };
        }

        var errors = new List<string>();
        if (!this.keys.IsAvailable)
        {
            errors.Add("The agent has not received the storage key from the Bridge.");
        }
        else if (!string.Equals(this.keys.Fingerprint, request.KeyFingerprint, StringComparison.Ordinal))
        {
            errors.Add("The agent holds a different storage key than the one the Bridge backed up; reconnect the agent and try again.");
        }

        errors.AddRange(this.Preview().Blockers);
        if (errors.Count > 0)
        {
            return new StorageEncryptionEnableResultDto { Errors = errors };
        }

        if (request.EraseEvaluationArtifacts)
        {
            var erased = SecureErase.Directory(this.context.Layout.EvaluationDirectory);
            this.LogEvaluationErased(erased);
        }

        var fingerprint = this.keys.Fingerprint!;
        this.context.MarkRestartPending(new StorageEncryptionMarker { KeyFingerprint = fingerprint, EnabledAt = this.time.GetUtcNow() });
        this.LogEnabled(fingerprint, this.selfRestart);
        if (this.selfRestart)
        {
            _ = this.StopAfterDelayAsync();
        }

        return new StorageEncryptionEnableResultDto { Accepted = true, RestartScheduled = this.selfRestart };
    }

    private async Task StopAfterDelayAsync()
    {
        await Task.Delay(RestartDelay, this.time).ConfigureAwait(false);
        this.lifetime.StopApplication();
    }

    private static string StateName(StorageState state) => state switch
    {
        StorageState.Open => "open",
        StorageState.Failed => "failed",
        _ => "awaitingKey",
    };

    private static string Megabytes(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{Math.Ceiling(bytes / 1048576.0):0} MB");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Encryption at rest enabled (key {Fingerprint}); self-restart scheduled: {SelfRestart}")]
    private partial void LogEnabled(string fingerprint, bool selfRestart);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The storage status could not be determined ({ErrorType}); reporting it as unavailable")]
    private partial void LogStatusUnavailable(string errorType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Erased {Files} files of plaintext evaluation runs before enabling encryption")]
    private partial void LogEvaluationErased(int files);
}

/// <summary>Encryption state for <c>/health</c>.</summary>
/// <param name="EncryptAtRest">The stores run encrypted.</param>
/// <param name="Source"><c>off</c>, <c>config</c>, <c>marker</c> or <c>detected</c>.</param>
/// <param name="RestartPending">Enabled; applies after the restart.</param>
/// <param name="KeyState">One of <see cref="StorageKeyStates"/>.</param>
/// <param name="MessagesState"><c>plaintext</c>, <c>open</c>, <c>awaitingKey</c> or <c>failed</c>.</param>
/// <param name="MemoryState"><c>plaintext</c>, <c>open</c>, <c>awaitingKey</c> or <c>failed</c>.</param>
public sealed record StorageHealth(bool EncryptAtRest, string Source, bool RestartPending, string KeyState, string MessagesState, string MemoryState)
{
    /// <summary><c>off</c>, <c>ok</c> (every encrypted store open) or <c>degraded</c>.</summary>
    public string Status => !this.EncryptAtRest ? "off" : this.MessagesState == "open" && this.MemoryState == "open" ? "ok" : "degraded";
}
