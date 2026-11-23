using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Evaluation.Sanitising;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Evaluation.Frozen;

/// <summary>What a freeze reads: the live stores, the two registered workspaces and the owner dictionary.</summary>
public sealed record FreezeSources(string MessagesDb, MemoryMcpOptions Memory, EvalWorkspace History, MemoryEvalWorkspace MemoryWorkspace, string LexiconPath);

/// <summary>Outcome of a freeze: the new version, or the leakage findings that stopped it.</summary>
public sealed record FreezeResult(FrozenCorpusManifest? Manifest, IReadOnlyList<LeakageFinding> Findings);

/// <summary>
/// Freezes a version (D6, §4.2): a sanitised copy of the message history (only the <c>Messages</c> rows, ids and
/// timestamps unchanged, text, channel and user ids pseudonymised, tool-call arguments sanitised, external message
/// ids dropped), the sanitised curated memories, both registered datasets sanitised with the same pseudonyms (labels
/// stay valid), the unchanged analysis and ablation plans, a fresh registration of the sanitised files and a manifest
/// with their hashes. The version is built in <c>vN.partial</c>, scanned by <see cref="LeakageScanner"/> and only then
/// renamed; a finding deletes it (fail closed). Nothing is ever written in plaintext on an encrypted install.
/// </summary>
public static class FrozenCorpusBuilder
{
    /// <summary>Sealed file of the version's pseudonym secret (never exported).</summary>
    public const string SecretFileName = "secret.key";

    /// <summary>Sanitised memories (one <see cref="MemorySeedSource"/> per line, sealed).</summary>
    public const string MemoriesFileName = "memories.jsonl";

    /// <summary>Key of the messages digest in <see cref="FrozenCorpusManifest.Files"/>.</summary>
    public const string MessagesDigestKey = "messages.db#rows";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] Workspaces = ["history", "memory"];

    private static readonly string[] PlanFiles = ["analysis-plan.md", "ablation.json"];

    /// <summary>Freezes the next version; throws <see cref="InvalidOperationException"/> when the inputs are not ready.</summary>
    public static async Task<FreezeResult> FreezeAsync(FreezeSources sources, FrozenCorpusStore store, EvalProtection protection, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(time);
        var lexicon = File.Exists(sources.LexiconPath) ? SanitiserLexicon.Parse(protection.ReadLines(sources.LexiconPath)) : SanitiserLexicon.Empty;
        var history = ReadRegistered(sources.History.DatasetPath, sources.History.VerifyRegistration, sources.History.ReadDataset);
        var memoryQueries = ReadRegistered(sources.MemoryWorkspace.DatasetPath, sources.MemoryWorkspace.VerifyRegistration, sources.MemoryWorkspace.ReadDataset);
        if (history is null && memoryQueries is null)
        {
            throw new InvalidOperationException("Nothing to freeze: neither history-eval nor memory-eval has a dataset.");
        }

        var version = store.ReserveNextVersion();
        var final = store.DirectoryOf(version);
        var partial = final + ".partial";
        Directory.CreateDirectory(partial);
        try
        {
            var secret = PseudonymMap.NewSecret();
            protection.WriteText(Path.Combine(partial, SecretFileName), Convert.ToBase64String(secret));
            var sanitiser = new TextSanitiser(lexicon, new PseudonymMap(secret));
            CryptographicOperations.ZeroMemory(secret);
            var files = new Dictionary<string, string>(StringComparer.Ordinal);

            var dbPath = Path.Combine(partial, "messages.db");
            var copyPassword = protection.CopyPassword(MessageStore.PasswordPurpose);
            var corpus = await WriteMessagesAsync(sources.MessagesDb, protection.SourcePassword(MessageStore.PasswordPurpose), dbPath, protection, sanitiser, cancellationToken).ConfigureAwait(false);
            files[MessagesDigestKey] = MessagesDigest(dbPath, copyPassword);

            var memories = (await ReadMemoriesAsync(sources.Memory, Path.Combine(partial, "tmp-memory"), protection, cancellationToken).ConfigureAwait(false))
                .Select(m => new MemorySeedSource(m.MemoryId, m.Title is null ? null : sanitiser.Sanitise(m.Title), m.Tags.Select(sanitiser.Sanitise).ToList(), sanitiser.Sanitise(m.Content)))
                .ToList();
            WriteSealed(protection, Path.Combine(partial, MemoriesFileName), memories.Select(m => JsonSerializer.Serialize(m, Json)), files, MemoriesFileName);

            if (history is not null)
            {
                var missing = history.SelectMany(q => q.Judgments).Count(j => !corpus.Ids.Contains(j.MessageId));
                if (missing > 0)
                {
                    throw new InvalidOperationException($"{missing} history judgments refer to messages that no longer exist (deleted): update dataset.jsonl and register again.");
                }

                var sanitised = history.Select(q => Sanitise(q, sanitiser)).ToList();
                WriteWorkspace(protection, sources.History.Directory, Path.Combine(partial, "history"), sanitised.Select(EvalDataset.ToLine), files, "history");
            }

            if (memoryQueries is not null)
            {
                var memoryIds = memories.Select(m => m.MemoryId).ToHashSet(StringComparer.Ordinal);
                var missing = memoryQueries.SelectMany(q => q.Judgments).Count(j => !memoryIds.Contains(j.MemoryId));
                if (missing > 0)
                {
                    throw new InvalidOperationException($"{missing} memory judgments refer to memories that no longer exist: update the memory dataset.jsonl and register again.");
                }

                var sanitised = memoryQueries.Select(q => Sanitise(q, sanitiser)).ToList();
                WriteWorkspace(protection, sources.MemoryWorkspace.Directory, Path.Combine(partial, "memory"), sanitised.Select(MemoryEvalDataset.ToLine), files, "memory");
            }

            var scanner = new LeakageScanner(lexicon, sanitiser.Pseudonyms);
            ScanVersion(partial, dbPath, copyPassword, protection, scanner);
            if (!scanner.Passed)
            {
                DeleteDirectory(partial);
                return new FreezeResult(null, scanner.Findings);
            }

            if (history is not null)
            {
                new EvalWorkspace(Path.Combine(partial, "history"), protection).Register();
            }

            if (memoryQueries is not null)
            {
                new MemoryEvalWorkspace(Path.Combine(partial, "memory"), protection).Register();
            }

            var manifest = new FrozenCorpusManifest
            {
                Version = version,
                CreatedAt = time.GetUtcNow(),
                CorpusFrom = corpus.From,
                CorpusTo = corpus.To,
                Messages = corpus.Ids.Count,
                Memories = memories.Count,
                HistoryQueries = history?.Count ?? 0,
                MemoryQueries = memoryQueries?.Count ?? 0,
                LexiconEntries = lexicon.Entries.Count,
                LeakageTextsScanned = scanner.Scanned,
                Encrypted = protection.IsEncrypted,
                Files = files,
                RawRegistration = RawRegistration(sources),
            };
            manifest.Write(Path.Combine(partial, FrozenCorpusManifest.FileName));
            SqliteConnection.ClearAllPools();
            Directory.Move(partial, final);
            return new FreezeResult(manifest, []);
        }
        catch
        {
            DeleteDirectory(partial);
            throw;
        }
    }

    /// <summary>SHA-256 over the rows of <c>Messages</c> (ids, time, role, category, channel, user, text, tool calls), the version's integrity hash of the corpus.</summary>
    public static string MessagesDigest(string dbPath, string? password)
    {
        using var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(dbPath, password, SqliteOpenMode.ReadOnly, pooling: false));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Timestamp, Role, Category, ChannelId, UserId, Content, ToolCalls FROM Messages ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? "\0" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
                var bytes = Encoding.UTF8.GetBytes(value);
                hash.AppendData(BitConverter.GetBytes(bytes.Length));
                hash.AppendData(bytes);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>SHA-256 (hex) of a text's UTF-8 bytes.</summary>
    public static string TextDigest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static IReadOnlyList<T>? ReadRegistered<T>(string datasetPath, Action verifyRegistration, Func<IReadOnlyList<T>> read)
    {
        if (!File.Exists(datasetPath))
        {
            return null;
        }

        verifyRegistration();
        return read();
    }

    private static EvalQuery Sanitise(EvalQuery query, TextSanitiser sanitiser)
    {
        EvalFilters? Filters(EvalFilters? filters) =>
            filters is null ? null : filters with { Channel = filters.Channel is null ? null : sanitiser.SanitiseIdentifier(filters.Channel) };

        return query with
        {
            Text = sanitiser.Sanitise(query.Text),
            GoldFilters = Filters(query.GoldFilters),
            ModelQuery = query.ModelQuery is null ? null : query.ModelQuery with { Text = sanitiser.Sanitise(query.ModelQuery.Text), Filters = Filters(query.ModelQuery.Filters) },
        };
    }

    private static MemoryEvalQuery Sanitise(MemoryEvalQuery query, TextSanitiser sanitiser) => query with
    {
        Text = sanitiser.Sanitise(query.Text),
        Tags = query.Tags?.Select(sanitiser.Sanitise).ToList(),
        ModelQuery = query.ModelQuery is null ? null : query.ModelQuery with { Text = sanitiser.Sanitise(query.ModelQuery.Text), Tags = query.ModelQuery.Tags?.Select(sanitiser.Sanitise).ToList() },
    };

    private static async Task<(HashSet<long> Ids, DateTimeOffset From, DateTimeOffset To)> WriteMessagesAsync(
        string sourceDb, string? sourcePassword, string targetDb, EvalProtection protection, TextSanitiser sanitiser, CancellationToken cancellationToken)
    {
        // The store creates the schema (encrypted with the evaluation ring when the install is encrypted); only Messages rows are written.
        await using (var schema = new MessageStore(targetDb, NullLogger<MessageStore>.Instance, protection.CopyMessageEncryption))
        {
            await schema.WhenOpenAsync(cancellationToken).ConfigureAwait(false);
        }

        var ids = new HashSet<long>();
        DateTimeOffset? from = null, to = null;
        using var source = new SqliteConnection(EncryptedSqlite.ConnectionString(sourceDb, sourcePassword, SqliteOpenMode.ReadOnly, pooling: false));
        using var target = new SqliteConnection(EncryptedSqlite.ConnectionString(targetDb, protection.CopyPassword(MessageStore.PasswordPurpose), SqliteOpenMode.ReadWrite, pooling: false));
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await target.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = target.BeginTransaction();
        using var insert = target.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO Messages (Id, UserId, ChannelId, Role, Content, Timestamp, MessageId, Category, ToolCalls)
            VALUES (@id, @user, @channel, @role, @content, @timestamp, NULL, @category, @tools);
            """;
        var parameters = new[]
        {
            insert.Parameters.Add("@id", SqliteType.Integer),
            insert.Parameters.Add("@user", SqliteType.Text),
            insert.Parameters.Add("@channel", SqliteType.Text),
            insert.Parameters.Add("@role", SqliteType.Text),
            insert.Parameters.Add("@content", SqliteType.Text),
            insert.Parameters.Add("@timestamp", SqliteType.Text),
            insert.Parameters.Add("@category", SqliteType.Integer),
            insert.Parameters.Add("@tools", SqliteType.Text),
        };
        using var read = source.CreateCommand();
        read.CommandText = "SELECT Id, UserId, ChannelId, Role, Content, Timestamp, Category, ToolCalls FROM Messages ORDER BY Id;";
        using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var timestamp = reader.GetString(5);
            var at = SqliteDateTimeText.Parse(timestamp);
            from = from is null || at < from ? at : from;
            to = to is null || at > to ? at : to;
            parameters[0].Value = id;
            parameters[1].Value = sanitiser.SanitiseIdentifier(reader.GetString(1));
            parameters[2].Value = sanitiser.SanitiseIdentifier(reader.GetString(2));
            parameters[3].Value = reader.GetString(3);
            parameters[4].Value = sanitiser.Sanitise(reader.GetString(4));
            parameters[5].Value = timestamp;
            parameters[6].Value = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
            parameters[7].Value = reader.IsDBNull(7) ? DBNull.Value : (object?)SanitiseToolCalls(reader.GetString(7), sanitiser) ?? DBNull.Value;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            ids.Add(id);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (ids, from ?? DateTimeOffset.UnixEpoch, to ?? DateTimeOffset.UnixEpoch);
    }

    // Tool names stay (history labels depend on them); the 32-character argument previews are sanitised like text.
    private static string? SanitiseToolCalls(string json, TextSanitiser sanitiser)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<List<ToolCallSummaryEntry>>(json, Json);
            return entries is null ? null : JsonSerializer.Serialize(entries.Select(e => e with { Args = sanitiser.Sanitise(e.Args) }), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<MemorySeedSource>> ReadMemoriesAsync(MemoryMcpOptions live, string runDirectory, EvalProtection protection, CancellationToken cancellationToken)
    {
        if (!File.Exists(live.DatabasePath))
        {
            return [];
        }

        try
        {
            await using var corpus = await MemoryEvalCorpus.CreateAsync(live.DataDirectory, runDirectory, live, new NoEmbedding(), cancellationToken, protection: protection).ConfigureAwait(false);
            return await corpus.ListMemoriesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteDirectory(runDirectory);
        }
    }

    private static void WriteWorkspace(EvalProtection protection, string rawWorkspace, string target, IEnumerable<string> datasetLines, Dictionary<string, string> files, string prefix)
    {
        Directory.CreateDirectory(target);
        WriteSealed(protection, Path.Combine(target, "dataset.jsonl"), datasetLines, files, prefix + "/dataset.jsonl");
        foreach (var name in PlanFiles)
        {
            var text = File.ReadAllText(Path.Combine(rawWorkspace, name));
            File.WriteAllText(Path.Combine(target, name), text);
            files[prefix + "/" + name] = TextDigest(text);
        }
    }

    private static void WriteSealed(EvalProtection protection, string path, IEnumerable<string> lines, Dictionary<string, string> files, string key)
    {
        var text = string.Concat(lines.Select(l => l + "\n"));
        protection.WriteText(path, text);
        files[key] = TextDigest(text);
    }

    private static void ScanVersion(string partial, string dbPath, string? password, EvalProtection protection, LeakageScanner scanner)
    {
        using (var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(dbPath, password, SqliteOpenMode.ReadOnly, pooling: false)))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT UserId, ChannelId, Content, ToolCalls FROM Messages;";
            using var reader = cmd.ExecuteReader();
            string[] columns = ["UserId", "ChannelId", "Content", "ToolCalls"];
            while (reader.Read())
            {
                for (var i = 0; i < columns.Length; i++)
                {
                    scanner.Scan("messages." + columns[i], reader.IsDBNull(i) ? null : reader.GetString(i));
                }
            }
        }

        foreach (var line in protection.ReadLines(Path.Combine(partial, MemoriesFileName)))
        {
            scanner.Scan(MemoriesFileName, line);
        }

        foreach (var workspace in Workspaces.Select(w => Path.Combine(partial, w)).Where(Directory.Exists))
        {
            var name = Path.GetFileName(workspace);
            foreach (var line in protection.ReadLines(Path.Combine(workspace, "dataset.jsonl")))
            {
                scanner.Scan(name + "/dataset.jsonl", line);
            }

            scanner.Scan(name + "/analysis-plan.md", File.ReadAllText(Path.Combine(workspace, "analysis-plan.md")));
            scanner.Scan(name + "/ablation.json", File.ReadAllText(Path.Combine(workspace, "ablation.json")));
        }
    }

    private static Dictionary<string, string> RawRegistration(FreezeSources sources)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (prefix, path) in new[] { ("history", sources.History.PreregistrationPath), ("memory", sources.MemoryWorkspace.PreregistrationPath) })
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Json) is { } registered)
            {
                foreach (var (name, hash) in registered)
                {
                    hashes[prefix + "/" + name] = hash;
                }
            }
        }

        return hashes;
    }

    private static void DeleteDirectory(string path)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left for D3 and the next freeze; a .partial directory is never served
        }
    }

    // Listing memories never embeds; a call means a bug, not a degraded run.
    internal sealed class NoEmbedding : IEmbeddingService
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Freezing does not embed.");

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Freezing does not embed.");
    }
}
