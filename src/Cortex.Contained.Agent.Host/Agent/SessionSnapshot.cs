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
    /// Saves the snapshot to disk using atomic write (temp + rename).
    /// </summary>
    public static async Task SaveAsync(SessionsSnapshot snapshot, string stateDir, ILogger logger)
    {
        var filePath = Path.Combine(stateDir, SnapshotFileName);
        var tempPath = filePath + ".tmp";

        try
        {
            Directory.CreateDirectory(stateDir);

            var json = JsonSerializer.Serialize(snapshot, jsonOptions);
            await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
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

    /// <summary>
    /// Loads and restores sessions from the snapshot file.
    /// Returns the number of sessions restored, or 0 if no snapshot or on failure.
    /// The snapshot file is deleted after successful restore (one-shot).
    /// </summary>
    public static int TryRestore(AgentSessionStore sessionStore, string stateDir, ILogger logger)
    {
        var filePath = Path.Combine(stateDir, SnapshotFileName);
        if (!File.Exists(filePath))
        {
            return 0;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var snapshot = JsonSerializer.Deserialize<SessionsSnapshot>(json, jsonOptions);
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
}
