using System.Diagnostics;
using System.Globalization;
using System.Text;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.History.Evaluation.Smoke;

/// <summary>Outcome of the live embedder check: PASS/FAIL lines without any history text.</summary>
public sealed record EmbedderCheckResult(bool Passed, IReadOnlyList<string> Lines);

/// <summary>
/// Live embedder check for increment 3 (§4.2), run with <c>history-smoke embedder-check</c> against the
/// real sidecar: the endpoint is allowed for history text, vectors have the configured dimensions,
/// a maximum-size chunk fits <c>num_ctx</c>, an input beyond <c>num_ctx</c> fails instead of being
/// truncated, and query embedding with the instruction works. Uses synthetic text only.
/// </summary>
public static class EmbedderCheck
{
    /// <summary>Runs every check; stops early when the endpoint is not allowed.</summary>
    public static async Task<EmbedderCheckResult> RunAsync(
        IEmbeddingService embeddings, MemoryMcpOptions memory, HistoryOptions history, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(history);
        var lines = new List<string>();
        var passed = true;
        void Report(bool ok, string text)
        {
            passed &= ok;
            lines.Add((ok ? "PASS " : "FAIL ") + text);
        }

        var ollama = memory.Ollama;
        if (HistoryEmbedderGuard.Check(ollama.Endpoint, ollama.ApiKey, history.Dense.AllowedHosts()) is { } reason)
        {
            Report(false, "endpoint: " + reason);
            return new EmbedderCheckResult(false, lines);
        }

        Report(true, $"endpoint {new Uri(ollama.Endpoint).Host} is allowed for history text");

        var watch = Stopwatch.StartNew();
        var vector = await embeddings.EmbedAsync("embedder check", cancellationToken).ConfigureAwait(false);
        Report(vector.Length == ollama.Dimensions, string.Create(CultureInfo.InvariantCulture,
            $"dimensions {vector.Length} (configured {ollama.Dimensions}), {watch.ElapsedMilliseconds} ms"));

        var maxChunk = Synthetic(history.Dense.MaxChunkChars);
        try
        {
            watch.Restart();
            await embeddings.EmbedBatchAsync([maxChunk], cancellationToken).ConfigureAwait(false);
            Report(true, string.Create(CultureInfo.InvariantCulture,
                $"a {maxChunk.Length}-character mixed-script chunk fits num_ctx={ollama.NumCtx} ({watch.ElapsedMilliseconds} ms)"));
        }
        catch (EmbeddingInputTooLongException)
        {
            Report(false, string.Create(CultureInfo.InvariantCulture,
                $"a {maxChunk.Length}-character chunk exceeds num_ctx={ollama.NumCtx}: raise MemoryMcp:Ollama:NumCtx or lower History:Dense:MaxChunkChars"));
        }

        var tooLong = Synthetic(ollama.NumCtx * 8);
        try
        {
            await embeddings.EmbedBatchAsync([tooLong], cancellationToken).ConfigureAwait(false);
            Report(false, "an input far beyond num_ctx was embedded: truncation is not disabled");
        }
        catch (EmbeddingInputTooLongException)
        {
            Report(true, "an input beyond num_ctx fails instead of being truncated");
        }

        var instruction = history.Dense.UseQueryInstruction ? history.Dense.QueryInstruction : null;
        var query = await embeddings.EmbedQueryAsync("when did we talk about the trip", instruction, cancellationToken).ConfigureAwait(false);
        Report(query.Length == ollama.Dimensions, $"query embedding with instruction={(instruction is null ? "off" : "on")}");
        return new EmbedderCheckResult(passed, lines);
    }

    /// <summary>Deterministic mixed Latin/Cyrillic/digit text of exactly <paramref name="chars"/> characters.</summary>
    public static string Synthetic(int chars)
    {
        string[] words = ["togbillet", "поездка", "meeting", "2041", "køreplan", "ёлка", "budget", "x7"];
        var text = new StringBuilder(chars + 16);
        for (var i = 0; text.Length < chars; i++)
        {
            text.Append(words[i % words.Length]).Append(' ');
        }

        return text.ToString(0, chars);
    }
}
