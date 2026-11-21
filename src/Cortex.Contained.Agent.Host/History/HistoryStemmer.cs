namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// The stemmer of the history index's stemmed shadow column (§4.2 keyword quality):
/// <see cref="SnowballStemmer"/> (ru/da/en on folded tokens). Its version is part of the index fingerprint.
/// </summary>
public static class HistoryStemmer
{
    /// <summary>Stemmer version (part of the index fingerprint).</summary>
    public const int Version = SnowballStemmer.Version;

    /// <summary>The stemmed shadow body of folded text: stems of every token, space-separated.</summary>
    public static string StemBody(string folded) => SnowballStemmer.StemBody(folded);

    /// <summary>The distinct folded stems of one folded token.</summary>
    public static IReadOnlyList<string> Stems(string token) => SnowballStemmer.Stems(token);
}
