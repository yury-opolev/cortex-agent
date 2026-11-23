using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>One pair of the owner's κ sample: the judge's grade, the texts to grade blind, and the owner's grade once given.</summary>
public sealed record KappaItem
{
    public required string Tool { get; init; }

    public required string QueryId { get; init; }

    public required string CandidateId { get; init; }

    public required int LlmGrade { get; init; }

    public required string Query { get; init; }

    public required string Candidate { get; init; }

    /// <summary>The owner's grade (0–2), or null while not graded.</summary>
    public int? HumanGrade { get; init; }
}

/// <summary>The κ sample of one workspace (<c>kappa-sample.json</c>, sealed: it holds query and candidate text).</summary>
public static class KappaSampleFile
{
    public const string FileName = "kappa-sample.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The items in <paramref name="directory"/>, or none.</summary>
    public static IReadOnlyList<KappaItem> Read(string directory, EvalProtection protection)
    {
        ArgumentNullException.ThrowIfNull(protection);
        var path = Path.Combine(directory, FileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<List<KappaItem>>(protection.ReadText(path), Json) ?? [] : [];
    }

    public static void Write(string directory, EvalProtection protection, IReadOnlyList<KappaItem> items)
    {
        ArgumentNullException.ThrowIfNull(protection);
        protection.WriteText(Path.Combine(directory, FileName), JsonSerializer.Serialize(items, Json));
    }
}
