using System.Text.Json;
using System.Text.Json.Serialization;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Snapshot of a single <see cref="AgentSession"/> for persistence across container restarts.
/// Contains the full LLM conversation (including tool calls, content blocks) plus
/// extraction buffer state.
/// </summary>
public sealed class SessionSnapshotEntry
{
    public required string ConversationId { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastMessageAt { get; init; }
    public int LastPromptTokens { get; init; }
    public int LastCompactionRound { get; init; }
    public required IReadOnlyList<LlmMessage> History { get; init; }
    public IReadOnlyList<ExtractionEntry>? ExtractionBuffer { get; init; }
}

/// <summary>
/// Root object for the sessions snapshot file.
/// </summary>
public sealed class SessionsSnapshot
{
    public DateTimeOffset SavedAt { get; init; }
    public required IReadOnlyList<SessionSnapshotEntry> Sessions { get; init; }
}

/// <summary>
/// Serializes and deserializes session state to/from the persistent volume.
/// Uses atomic write (temp file + rename) to prevent corrupt snapshots.
/// </summary>
public static partial class SessionSnapshotSerializer
{
    /// <summary>Default snapshot file name within the state directory.</summary>
    public const string SnapshotFileName = "sessions-snapshot.json";

    /// <summary>Key purpose (and associated-data label) of an encrypted snapshot.</summary>
    public const string Purpose = "sessions-snapshot";

    /// <summary>Name prefix of an unrestored snapshot an earlier shutdown set aside.</summary>
    public const string PendingPrefix = "sessions-snapshot.pending-";

    /// <summary>Name suffix of a set-aside snapshot.</summary>
    public const string PendingSuffix = ".enc";

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Captures a snapshot from all active sessions in the store.
    /// </summary>
    public static SessionsSnapshot CaptureSnapshot(AgentSessionStore sessionStore)
    {
        var sessions = sessionStore.GetAll();
        var entries = new List<SessionSnapshotEntry>(sessions.Count);

        foreach (var session in sessions)
        {
            var history = session.GetHistory();
            if (history.Count == 0)
            {
                continue; // Skip empty sessions
            }

            entries.Add(new SessionSnapshotEntry
            {
                ConversationId = session.ConversationId,
                Title = session.Title,
                CreatedAt = session.CreatedAt,
                LastMessageAt = session.LastMessageAt,
                LastPromptTokens = session.LastPromptTokens,
                LastCompactionRound = session.LastCompactionRound,
                History = history,
                ExtractionBuffer = session.PeekAllExtractionEntries(),
            });
        }

        return new SessionsSnapshot
        {
            SavedAt = DateTimeOffset.UtcNow,
            Sessions = entries,
        };
    }

    /// <summary>
    /// Saves the snapshot to disk using atomic write (temp + flush + rename). With <paramref name="cipher"/> the
    /// file is AES-GCM encrypted under the tenant storage key (it holds every live conversation).
    /// </summary>
    public static async Task SaveAsync(SessionsSnapshot snapshot, string stateDir, ILogger logger, Storage.StorageFileCipher? cipher = null)
    {
        var filePath = Path.Combine(stateDir, SnapshotFileName);
        var tempPath = filePath + ".tmp";

        try
        {
            Directory.CreateDirectory(stateDir);
            SetAsideUnrestored(filePath);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, jsonOptions);
            if (cipher is not null)
            {
                var plaintext = bytes;
                bytes = cipher.Encrypt(plaintext);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            }

            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, filePath, overwrite: true);

            LogSnapshotSaved(logger, snapshot.Sessions.Count, filePath);
        }
        catch (Exception ex)
        {
            LogSnapshotSaveFailed(logger, ex.Message);

            // Clean up temp file on failure
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    /// <summary>True when the snapshot file, or one set aside by an earlier shutdown, is encrypted (restoring it needs the storage key).</summary>
    public static bool IsEncrypted(string stateDir)
    {
        var filePath = Path.Combine(stateDir, SnapshotFileName);
        return (File.Exists(filePath) && Storage.StorageFileCipher.IsEncryptedFile(filePath)) || PendingSnapshots(stateDir).Count > 0;
    }

    // An unrestored snapshot (the key never arrived) must survive the next shutdown: it is renamed aside, not overwritten.
    private static void SetAsideUnrestored(string filePath)
    {
        if (!File.Exists(filePath) || !Storage.StorageFileCipher.IsEncryptedFile(filePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(filePath)!;
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        var target = Path.Combine(directory, PendingPrefix + stamp + PendingSuffix);
        for (var n = 1; File.Exists(target); n++)
        {
            target = Path.Combine(directory, PendingPrefix + stamp + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + PendingSuffix);
        }

        File.Move(filePath, target);
    }

    /// <summary>
    /// Loads and restores sessions from the snapshot file.
    /// Returns the number of sessions restored, or 0 if no snapshot or on failure.
    /// The snapshot file is deleted after successful restore (one-shot); a plaintext one is overwritten first.
    /// An encrypted snapshot without <paramref name="cipher"/>, or one that does not authenticate, is kept.
    /// </summary>
    public static int TryRestore(AgentSessionStore sessionStore, string stateDir, ILogger logger, Storage.StorageFileCipher? cipher = null) =>
        TryRestoreFile(sessionStore, Path.Combine(stateDir, SnapshotFileName), logger, cipher, onlyMissingSessions: false);

    /// <summary>
    /// Restores the snapshot file and every snapshot set aside by <see cref="SaveAsync"/> (newest first) into
    /// sessions that do not exist yet: a session that has history already is newer than the snapshot and is left
    /// alone. Each file that restores is deleted; one that cannot be decrypted is kept. Returns the sessions restored.
    /// </summary>
    public static int RestoreUnrestored(AgentSessionStore sessionStore, string stateDir, ILogger logger, Storage.StorageFileCipher? cipher)
    {
        var restored = TryRestoreFile(sessionStore, Path.Combine(stateDir, SnapshotFileName), logger, cipher, onlyMissingSessions: true);
        foreach (var pending in PendingSnapshots(stateDir))
        {
            restored += TryRestoreFile(sessionStore, pending, logger, cipher, onlyMissingSessions: true);
        }

        return restored;
    }

    /// <summary>Snapshots an earlier shutdown set aside rather than overwrite (they were never restored), newest first.</summary>
    public static IReadOnlyList<string> PendingSnapshots(string stateDir) =>
        Directory.Exists(stateDir)
            ? [.. Directory.EnumerateFiles(stateDir, PendingPrefix + "*" + PendingSuffix).OrderByDescending(f => f, StringComparer.Ordinal)]
            : [];

    private static int TryRestoreFile(AgentSessionStore sessionStore, string filePath, ILogger logger, Storage.StorageFileCipher? cipher, bool onlyMissingSessions)
    {
        if (!File.Exists(filePath))
        {
            return 0;
        }

        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (Storage.StorageFileCipher.IsEncrypted(bytes))
            {
                if (cipher is null)
                {
                    LogEncryptedSnapshotKept(logger, filePath);
                    return 0;
                }

                try
                {
                    bytes = cipher.Decrypt(bytes);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    LogEncryptedSnapshotKept(logger, filePath);
                    return 0;
                }
            }

            var snapshot = JsonSerializer.Deserialize<SessionsSnapshot>(bytes, jsonOptions);
            if (snapshot?.Sessions is null || snapshot.Sessions.Count == 0)
            {
                LogSnapshotEmpty(logger, filePath);
                DeleteSnapshotFile(filePath);
                return 0;
            }

            var restored = 0;
            foreach (var entry in snapshot.Sessions)
            {
                if (entry.History.Count == 0)
                {
                    continue;
                }

                if (onlyMissingSessions && sessionStore.TryGet(entry.ConversationId, out var existing) && existing?.GetHistory().Count > 0)
                {
                    continue;
                }
                var session = sessionStore.GetOrCreate(entry.ConversationId);
                RestoreSession(session, entry);
                restored++;
            }

            LogSnapshotRestored(logger, restored, filePath, snapshot.SavedAt);
            DeleteSnapshotFile(filePath);
            return restored;
        }
        catch (Exception ex)
        {
            LogSnapshotRestoreFailed(logger, filePath, ex.Message);
            DeleteSnapshotFile(filePath);
            return 0;
        }
    }

    /// <summary>
    /// Populates an <see cref="AgentSession"/> from a snapshot entry.
    /// </summary>
    internal static void RestoreSession(AgentSession session, SessionSnapshotEntry entry)
    {
        session.Title = entry.Title;
        session.LastPromptTokens = entry.LastPromptTokens;
        session.LastCompactionRound = entry.LastCompactionRound;

        // Restore message history
        foreach (var message in entry.History)
        {
            session.AddMessage(message);
        }

        // Restore extraction buffer
        if (entry.ExtractionBuffer is { Count: > 0 })
        {
            foreach (var extraction in entry.ExtractionBuffer)
            {
                session.AppendToExtractionBuffer(extraction);
            }
        }

    }

    private static void DeleteSnapshotFile(string filePath)
    {
        try
        {
            // A plaintext snapshot holds whole conversations: overwrite it before unlinking.
            if (File.Exists(filePath) && !Storage.StorageFileCipher.IsEncryptedFile(filePath))
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
                stream.Write(new byte[stream.Length]);
                stream.Flush(flushToDisk: true);
            }

            File.Delete(filePath);
        }
        catch
        {
            // Best effort — stale file will be overwritten next shutdown
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session snapshot saved: {Count} sessions to {FilePath}")]
    private static partial void LogSnapshotSaved(ILogger logger, int count, string filePath);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to save session snapshot: {ErrorMessage}")]
    private static partial void LogSnapshotSaveFailed(ILogger logger, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session snapshot restored: {Count} sessions from {FilePath} (saved at {SavedAt})")]
    private static partial void LogSnapshotRestored(ILogger logger, int count, string filePath, DateTimeOffset savedAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session snapshot file is empty or invalid: {FilePath}")]
    private static partial void LogSnapshotEmpty(ILogger logger, string filePath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to restore session snapshot from {FilePath}: {ErrorMessage}. Sessions will be re-seeded by Bridge.")]
    private static partial void LogSnapshotRestoreFailed(ILogger logger, string filePath, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Encrypted session snapshot {FilePath} kept: it cannot be decrypted now (no storage key, or another key)")]
    private static partial void LogEncryptedSnapshotKept(ILogger logger, string filePath);
}
