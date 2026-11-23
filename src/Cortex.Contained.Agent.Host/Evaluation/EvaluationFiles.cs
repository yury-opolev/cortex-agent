using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Evaluation;

/// <summary>
/// The owner-authored files of the evaluation workspaces, read and written through the Bridge (on an encrypted
/// install the files that carry text are sealed, so they cannot be edited in the container). Only allow-listed
/// file names of the configured workspaces are reachable; run outputs are not (they are plaintext numbers and
/// can be read in the container).
/// </summary>
public sealed class EvaluationFiles
{
    // Files that may carry personal text: sealed on an encrypted install.
    private static readonly HashSet<string> SealedNames = new(StringComparer.Ordinal)
    {
        "dataset.jsonl", "seeds.jsonl", "dictionary.jsonl", "dictionary.suggested.jsonl",
    };

    private static readonly Dictionary<string, (string[] Readable, string[] Writable)> Names = new(StringComparer.Ordinal)
    {
        ["history-eval"] = (["dataset.jsonl", "dataset.example.jsonl", "analysis-plan.md", "ablation.json", "preregistration.json", "judge-agreement.csv"],
            ["dataset.jsonl", "analysis-plan.md", "ablation.json", "judge-agreement.csv"]),
        ["memory-eval"] = (["dataset.jsonl", "dataset.example.jsonl", "seeds.jsonl", "pool.jsonl", "query-generation.md", "analysis-plan.md", "ablation.json", "preregistration.json", "judge-agreement.csv"],
            ["dataset.jsonl", "analysis-plan.md", "ablation.json", "judge-agreement.csv"]),
        ["eval-corpus"] = (["dictionary.jsonl", "dictionary.suggested.jsonl"], ["dictionary.jsonl"]),
    };

    private readonly IReadOnlyDictionary<string, string> directories;
    private readonly Func<EvalProtection> protection;

    /// <param name="directories">Workspace directory per tool.</param>
    /// <param name="protection">The protection (throws while the storage key is missing).</param>
    public EvaluationFiles(IReadOnlyDictionary<string, string> directories, Func<EvalProtection> protection)
    {
        this.directories = directories;
        this.protection = protection;
    }

    /// <summary>Reads a file (decrypted when sealed).</summary>
    public EvaluationFileDto Read(EvaluationFileRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (this.Resolve(request.Tool, request.Name, write: false) is not { } path)
        {
            return new EvaluationFileDto { Error = $"'{request.Name}' is not a readable {request.Tool} file." };
        }

        if (!File.Exists(path))
        {
            return new EvaluationFileDto { Exists = false };
        }

        try
        {
            var sealedFile = EvalProtection.IsSealed(path);
            var content = SealedNames.Contains(request.Name) || sealedFile ? this.protection().ReadText(path) : File.ReadAllText(path);
            return new EvaluationFileDto { Exists = true, Sealed = sealedFile, Content = content };
        }
        catch (InvalidOperationException ex)
        {
            return new EvaluationFileDto { Exists = true, Error = ex.Message };
        }
    }

    /// <summary>Writes a file, sealed when it carries text on an encrypted install; identical content is not rewritten.</summary>
    public EvaluationFileWriteResultDto Write(EvaluationFileWriteDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (this.Resolve(request.Tool, request.Name, write: true) is not { } path)
        {
            return new EvaluationFileWriteResultDto { Error = $"'{request.Name}' is not a writable {request.Tool} file." };
        }

        var current = this.Read(new EvaluationFileRequestDto { Tool = request.Tool, Name = request.Name });
        if (current is { Exists: true, Error: null } && string.Equals(current.Content, request.Content, StringComparison.Ordinal))
        {
            return new EvaluationFileWriteResultDto { Written = false, Unchanged = true, Sealed = current.Sealed };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (SealedNames.Contains(request.Name))
        {
            var protection = this.protection();
            protection.WriteText(path, request.Content);
            return new EvaluationFileWriteResultDto { Written = true, Sealed = protection.IsEncrypted };
        }

        File.WriteAllText(path, request.Content);
        return new EvaluationFileWriteResultDto { Written = true };
    }

    private string? Resolve(string tool, string name, bool write) =>
        Names.TryGetValue(tool, out var names)
        && (write ? names.Writable : names.Readable).Contains(name, StringComparer.Ordinal)
        && this.directories.TryGetValue(tool, out var directory)
            ? Path.Combine(directory, name)
            : null;
}
