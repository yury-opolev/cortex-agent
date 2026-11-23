using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cortex.Contained.Agent.Host.Evaluation.Sanitising;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Llm;
using Cortex.Contained.Contracts.Security;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Evaluation;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Evaluation.Frozen;

/// <summary>What <c>eval-corpus</c> works on: the live stores, the two workspaces and the optional LLM for suggestions.</summary>
public sealed record EvalCorpusContext(
    string EvaluationDirectory,
    string MessagesDb,
    MemoryMcpOptions Memory,
    string HistoryWorkspace,
    string MemoryWorkspace,
    TimeProvider Time,
    ILlmClient? Llm = null,
    string? LlmModel = null);

/// <summary>
/// The frozen evaluation corpus command (run as an evaluation job inside the agent):
/// <c>eval-corpus &lt;suggest|check|freeze|list|export|uploaded|import|compare|delete&gt;</c>. Output holds
/// versions, counts, hashes and paths only.
/// </summary>
public static class EvalCorpusCli
{
    /// <summary>Usage text.</summary>
    public const string Usage =
        "usage: eval-corpus <suggest [--llm] [--max-batches 50] | check --version vN | freeze | list | export --version vN | " +
        "uploaded --version vN --content-id ID --sha256 HEX | import --package FILE.cxpkg | compare --version vN [--tool history-eval|memory-eval] [--raw RUN] [--sanitised RUN] | delete --version vN>\n" +
        "export and import need the package passphrase (Invoke-CortexEval.ps1 -PromptPassphrase).";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Runs one command; returns the exit code.</summary>
    public static async Task<int> RunAsync(EvaluationRun run, EvalCorpusContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(context);
        var output = run.Output;
        if (run.Args.Count == 0)
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        var store = new FrozenCorpusStore(context.EvaluationDirectory);
        var corpusDirectory = Path.Combine(context.EvaluationDirectory, "corpus");
        var lexiconPath = Path.Combine(corpusDirectory, "dictionary.jsonl");
        try
        {
            switch (run.Args[0])
            {
                case "suggest":
                    return await SuggestAsync(run, context, lexiconPath, Path.Combine(corpusDirectory, "dictionary.suggested.jsonl"), cancellationToken).ConfigureAwait(false);
                case "check":
                    return await CheckAsync(run, store, lexiconPath).ConfigureAwait(false);
                case "freeze":
                    return await FreezeAsync(run, context, store, lexiconPath, cancellationToken).ConfigureAwait(false);
                case "list":
                    foreach (var m in store.List())
                    {
                        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                            $"{m.Version}  {m.CreatedAt:yyyy-MM-dd}  range {m.CorpusFrom:yyyy-MM-dd}..{m.CorpusTo:yyyy-MM-dd}  messages {m.Messages}  memories {m.Memories}  queries {m.HistoryQueries}/{m.MemoryQueries}  exported {(m.ExportedAt is { } e ? e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "no")}  uploaded {(m.Offsite is null ? "no" : "yes")}{(m.Imported ? "  imported" : string.Empty)}")).ConfigureAwait(false);
                    }

                    foreach (var pending in store.PendingDeletions())
                    {
                        await output.WriteLineAsync($"PENDING off-machine deletion: {pending.Version} — delete file {pending.PackageFileName} of SafeExchange secret {pending.Secret}{(pending.ContentId is null ? string.Empty : " (contentId " + pending.ContentId + ")")}.").ConfigureAwait(false);
                    }

                    return 0;
                case "export":
                    return await ExportAsync(run, store, context).ConfigureAwait(false);
                case "uploaded":
                    return await UploadedAsync(run, store, context).ConfigureAwait(false);
                case "import":
                    return await ImportAsync(run, store).ConfigureAwait(false);
                case "compare":
                    return await CompareAsync(run, store, context).ConfigureAwait(false);
                case "delete":
                    var version = Required(run.Args, "--version");
                    _ = store.Read(version) ?? throw new InvalidOperationException($"No frozen corpus {version}.");
                    var gone = store.Destroy(version, context.Time.GetUtcNow());
                    await output.WriteLineAsync($"{version} deleted{(gone ? string.Empty : " (some files could not be removed yet)")}. " + OffsiteReminder(store, version)).ConfigureAwait(false);
                    return gone ? 0 : 1;
                default:
                    await output.WriteLineAsync(Usage).ConfigureAwait(false);
                    return 2;
            }
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
            return 2;
        }
        catch (InvalidDataException ex)
        {
            await output.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
            return 2;
        }
    }

    private static async Task<int> SuggestAsync(EvaluationRun run, EvalCorpusContext context, string lexiconPath, string suggestedPath, CancellationToken cancellationToken)
    {
        var lexicon = File.Exists(lexiconPath) ? SanitiserLexicon.Parse(run.Protection.ReadLines(lexiconPath)) : SanitiserLexicon.Empty;
        var texts = ReadMessageTexts(context.MessagesDb, run.Protection.SourcePassword(MessageStore.PasswordPurpose));
        var memoryRun = Path.Combine(context.EvaluationDirectory, "corpus", "tmp-memory");
        if (File.Exists(context.Memory.DatabasePath))
        {
            await using var memories = await MemoryEvalCorpus.CreateAsync(context.Memory.DataDirectory, memoryRun, context.Memory, new FrozenCorpusBuilder.NoEmbedding(), cancellationToken, protection: run.Protection).ConfigureAwait(false);
            texts.AddRange((await memories.ListMemoriesAsync(cancellationToken).ConfigureAwait(false)).Select(m => m.Title + "\n" + string.Join(' ', m.Tags) + "\n" + m.Content));
        }

        var suggestions = LexiconSuggester.Capitalised(texts, lexicon).ToList();
        if (run.Args.Contains("--llm", StringComparer.Ordinal))
        {
            if (context.Llm is null || string.IsNullOrWhiteSpace(context.LlmModel))
            {
                throw new InvalidOperationException("No LLM provider is configured for this agent.");
            }

            var batches = Option(run.Args, "--max-batches") is { } text && int.TryParse(text, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 50;
            suggestions.AddRange(await LexiconSuggester.FromLlmAsync(texts, lexicon, context.Llm, context.LlmModel, batches, cancellationToken).ConfigureAwait(false));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(suggestedPath)!);
        run.Protection.WriteLines(suggestedPath, suggestions.Select(s => JsonSerializer.Serialize(s, JsonSerializerOptions.Web)));
        await run.Output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{suggestions.Count} suggestions ({suggestions.Count(s => s.Source == "llm")} from the LLM) in dictionary.suggested.jsonl. Review them (Edit-CortexEvalFile.ps1 -Tool eval-corpus -Name dictionary.suggested.jsonl -Pull) and move accepted terms into dictionary.jsonl.")).ConfigureAwait(false);
        if (Directory.Exists(memoryRun))
        {
            Directory.Delete(memoryRun, recursive: true);
        }

        return 0;
    }

    // A version frozen before the dictionary grew is checked again for the new terms (the other patterns were checked at freeze).
    private static async Task<int> CheckAsync(EvaluationRun run, FrozenCorpusStore store, string lexiconPath)
    {
        var version = Required(run.Args, "--version");
        var manifest = store.Read(version) ?? throw new InvalidOperationException($"No frozen corpus {version}.");
        var lexicon = File.Exists(lexiconPath) ? SanitiserLexicon.Parse(run.Protection.ReadLines(lexiconPath)) : SanitiserLexicon.Empty;
        var scanner = new LeakageScanner(lexicon, [], termsOnly: true);
        var protection = run.Protection.ForFrozenSource();
        var directory = store.DirectoryOf(version);
        foreach (var text in ReadMessageTexts(Path.Combine(directory, "messages.db"), protection.SourcePassword(MessageStore.PasswordPurpose)))
        {
            scanner.Scan("messages.Content", text);
        }

        foreach (var relative in new[] { FrozenCorpusBuilder.MemoriesFileName, "history/dataset.jsonl", "memory/dataset.jsonl" })
        {
            foreach (var line in protection.ReadLines(Path.Combine(directory, relative)))
            {
                scanner.Scan(relative, line);
            }
        }

        foreach (var finding in scanner.Findings)
        {
            await run.Output.WriteLineAsync($"finding: {finding.Location} {finding.Kind} ×{finding.Count}").ConfigureAwait(false);
        }

        await run.Output.WriteLineAsync(scanner.Passed
            ? $"{manifest.Version}: no dictionary term found in {scanner.Scanned} texts."
            : $"{manifest.Version} holds terms the dictionary now lists: delete it (eval-corpus delete --version {version}) and freeze again.").ConfigureAwait(false);
        return scanner.Passed ? 0 : 1;
    }

    private static async Task<int> FreezeAsync(EvaluationRun run, EvalCorpusContext context, FrozenCorpusStore store, string lexiconPath, CancellationToken cancellationToken)
    {
        var sources = new FreezeSources(
            context.MessagesDb,
            context.Memory,
            new EvalWorkspace(context.HistoryWorkspace, run.Protection),
            new MemoryEvalWorkspace(context.MemoryWorkspace, run.Protection),
            lexiconPath);
        var result = await FrozenCorpusBuilder.FreezeAsync(sources, store, run.Protection, context.Time, cancellationToken).ConfigureAwait(false);
        if (result.Manifest is not { } manifest)
        {
            foreach (var finding in result.Findings)
            {
                await run.Output.WriteLineAsync($"leak: {finding.Location} {finding.Kind} ×{finding.Count}").ConfigureAwait(false);
            }

            await run.Output.WriteLineAsync(
                "Freeze refused: personal values remain after sanitising (nothing was kept). Add the missing spellings to dictionary.jsonl " +
                "(eval-corpus suggest helps find them) and freeze again.").ConfigureAwait(false);
            return 1;
        }

        await run.Output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Frozen {manifest.Version}: {manifest.Messages} messages, {manifest.Memories} memories, {manifest.HistoryQueries} history and {manifest.MemoryQueries} memory queries; leakage check passed on {manifest.LeakageTextsScanned} texts. Next: eval-corpus export --version {manifest.Version}.")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ExportAsync(EvaluationRun run, FrozenCorpusStore store, EvalCorpusContext context)
    {
        var version = Required(run.Args, "--version");
        if (run.Passphrase is not { Length: >= PassphraseKdf.MinimumPassphraseLength } passphrase)
        {
            throw new InvalidOperationException($"export needs a package passphrase of at least {PassphraseKdf.MinimumPassphraseLength} characters.");
        }

        var package = FrozenCorpusPackage.Export(store, version, run.Protection, passphrase, context.Time);
        await run.Output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Package {package.FileName}: {package.Length} bytes, sha256 {package.Sha256}. Download it from the Bridge (Export-CortexEvalCorpus.ps1), attach it to the SafeExchange secret {OffsiteCopy.SecretName} under that file name, then record the upload: eval-corpus uploaded --version {version} --content-id <id> --sha256 {package.Sha256}.")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> UploadedAsync(EvaluationRun run, FrozenCorpusStore store, EvalCorpusContext context)
    {
        var version = Required(run.Args, "--version");
        var manifest = store.Read(version) ?? throw new InvalidOperationException($"No frozen corpus {version}.");
        var sha256 = Required(run.Args, "--sha256").ToLowerInvariant();
        if (!string.Equals(manifest.PackageSha256, sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The uploaded file's SHA-256 is not the exported package's: upload the package again.");
        }

        (manifest with { Offsite = new OffsiteCopy { ContentId = Required(run.Args, "--content-id"), Sha256 = sha256, UploadedAt = context.Time.GetUtcNow() } })
            .Write(Path.Combine(store.DirectoryOf(version), FrozenCorpusManifest.FileName));
        await run.Output.WriteLineAsync($"Recorded: {version} is attached to {OffsiteCopy.SecretName}.").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ImportAsync(EvaluationRun run, FrozenCorpusStore store)
    {
        var name = Path.GetFileName(Required(run.Args, "--package"));
        if (!name.EndsWith(".cxpkg", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("--package takes the file name of a .cxpkg in the packages directory.");
        }

        if (run.Passphrase is not { Length: > 0 } passphrase)
        {
            throw new InvalidOperationException("import needs the package passphrase.");
        }

        var manifest = FrozenCorpusPackage.Import(store, Path.Combine(store.PackagesDirectory, name), run.Protection, passphrase);
        await run.Output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Imported {manifest.Version}: {manifest.Messages} messages, {manifest.Memories} memories; every file matched the package manifest. Evaluate it with --corpus frozen:{manifest.Version}.")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> CompareAsync(EvaluationRun run, FrozenCorpusStore store, EvalCorpusContext context)
    {
        var version = Required(run.Args, "--version");
        _ = store.Read(version) ?? throw new InvalidOperationException($"No frozen corpus {version}.");
        var tool = Option(run.Args, "--tool") ?? "history-eval";
        var (rawWorkspace, sanitisedWorkspace) = tool switch
        {
            "history-eval" => (context.HistoryWorkspace, Path.Combine(store.DirectoryOf(version), "history")),
            "memory-eval" => (context.MemoryWorkspace, Path.Combine(store.DirectoryOf(version), "memory")),
            _ => throw new InvalidOperationException("--tool takes history-eval or memory-eval."),
        };
        var rawRun = RunDirectory(Path.Combine(rawWorkspace, "runs"), Option(run.Args, "--raw"));
        var sanitisedRun = RunDirectory(Path.Combine(sanitisedWorkspace, "runs"), Option(run.Args, "--sanitised"));
        var raw = ReadReport(rawRun);
        var sanitised = ReadReport(sanitisedRun);
        var configs = SanitisedComparison.Configs(raw, sanitised);
        var comparisons = SanitisedComparison.Comparisons(raw, sanitised);
        var directory = Path.Combine(context.EvaluationDirectory, "compare");
        Directory.CreateDirectory(directory);
        var name = string.Create(CultureInfo.InvariantCulture, $"{tool}-{version}-{context.Time.GetUtcNow():yyyyMMdd-HHmmss}");
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new { tool, version, raw = Path.GetFileName(rawRun), sanitised = Path.GetFileName(sanitisedRun), configs, comparisons }, Json)).ConfigureAwait(false);
        var report = SanitisedComparison.Markdown($"{tool} {Path.GetFileName(rawRun)} vs {version}/{Path.GetFileName(sanitisedRun)}", configs, comparisons);
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".md"), report).ConfigureAwait(false);
        await run.Output.WriteLineAsync(report).ConfigureAwait(false);
        await run.Output.WriteLineAsync($"Report: {Path.Combine(directory, name + ".md")}").ConfigureAwait(false);
        return 0;
    }

    // The named run, or the newest holdout run.
    private static string RunDirectory(string runs, string? name)
    {
        if (name is not null)
        {
            var path = Path.Combine(runs, Path.GetFileName(name));
            return File.Exists(Path.Combine(path, "results.json")) ? path : throw new InvalidOperationException($"No results.json in run {Path.GetFileName(name)}.");
        }

        return (Directory.Exists(runs) ? Directory.EnumerateDirectories(runs, "holdout-*") : [])
            .Where(d => File.Exists(Path.Combine(d, "results.json")))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .FirstOrDefault() ?? throw new InvalidOperationException($"No holdout run with results in {runs}: run holdout first (and on the frozen copy with --corpus frozen:<version>).");
    }

    private static AblationReport ReadReport(string run) =>
        JsonSerializer.Deserialize<AblationReport>(File.ReadAllText(Path.Combine(run, "results.json")), Json)
        ?? throw new InvalidDataException($"{Path.GetFileName(run)}/results.json is empty.");

    private static List<string> ReadMessageTexts(string db, string? password)
    {
        var texts = new List<string>();
        if (!File.Exists(db))
        {
            return texts;
        }

        using var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(db, password, SqliteOpenMode.ReadOnly, pooling: false));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Content FROM Messages;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            texts.Add(reader.GetString(0));
        }

        return texts;
    }

    private static string OffsiteReminder(FrozenCorpusStore store, string version) =>
        store.PendingDeletions().FirstOrDefault(p => p.Version == version) is { } pending
            ? $"Its off-machine copy must be deleted too: file {pending.PackageFileName} of SafeExchange secret {pending.Secret}; then acknowledge it on the Recall page."
            : "It was never exported.";

    private static string Required(IReadOnlyList<string> args, string name) =>
        Option(args, name) ?? throw new InvalidOperationException($"{name} is required.");

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
