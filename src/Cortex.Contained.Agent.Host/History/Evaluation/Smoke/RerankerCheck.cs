using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Cortex.Contained.Agent.Host.Reranking;

namespace Cortex.Contained.Agent.Host.History.Evaluation.Smoke;

/// <summary>Outcome of the live reranker check: PASS/FAIL lines without any history text.</summary>
public sealed record RerankerCheckResult(bool Passed, IReadOnlyList<string> Lines);

/// <summary>
/// Live reranker check for increment 5 (§4.3), run with <c>history-smoke reranker-check</c> against the
/// real sidecar: the endpoint and key pass the exposure guard, the model loads, requests without the key
/// are refused, slot monitoring is off, a relevant passage ranks first, and the smallest and largest
/// budget cells answer within <c>Reranker:TimeoutMs</c>. Uses synthetic text only.
/// </summary>
public static class RerankerCheck
{
    private const string Query = "when does the night train to TownB leave";

    /// <summary>Runs every check; stops early when the endpoint may not be used or never becomes ready.</summary>
    public static async Task<RerankerCheckResult> RunAsync(
        LlamaServerReranker reranker, RerankerOptions options, string? apiKey, HttpClient http, TimeSpan readyTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(http);
        var lines = new List<string>();
        var passed = true;
        void Report(bool ok, string text)
        {
            passed &= ok;
            lines.Add((ok ? "PASS " : "FAIL ") + text);
        }

        if (RerankerEndpointGuard.Check(options.Endpoint, apiKey, options.AllowedHosts()) is { } reason)
        {
            Report(false, "endpoint: " + reason);
            return new RerankerCheckResult(false, lines);
        }

        Report(true, $"endpoint {new Uri(options.Endpoint).Host} is allowed and a key is set");

        var watch = Stopwatch.StartNew();
        while (!await reranker.ProbeHealthAsync(cancellationToken).ConfigureAwait(false))
        {
            if (watch.Elapsed > readyTimeout)
            {
                Report(false, string.Create(CultureInfo.InvariantCulture, $"the model did not load within {readyTimeout.TotalSeconds:F0} s (GET /health)"));
                return new RerankerCheckResult(false, lines);
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        Report(true, string.Create(CultureInfo.InvariantCulture, $"model ready after {watch.ElapsedMilliseconds} ms"));

        var baseUri = options.Endpoint.TrimEnd('/');
        using (var unauthenticated = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri + "/v1/rerank"))
        {
            Content = new StringContent("""{"query":"q","documents":["a","b"]}""", Encoding.UTF8, "application/json"),
        })
        using (var response = await http.SendAsync(unauthenticated, cancellationToken).ConfigureAwait(false))
        {
            Report(response.StatusCode == HttpStatusCode.Unauthorized,
                $"a request without the key is refused (HTTP {(int)response.StatusCode}, expected 401)");
        }

        using (var slots = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri + "/slots")))
        {
            slots.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await http.SendAsync(slots, cancellationToken).ConfigureAwait(false);
            Report(!response.IsSuccessStatusCode, $"slot monitoring is off (GET /slots: HTTP {(int)response.StatusCode})");
        }

        watch.Restart();
        var scores = await reranker.RerankAsync(
            Query,
            ["we had coffee and talked about the budget", "the night train to TownB leaves at 22:10 from platform 4", "invoice INV2041 is paid"],
            cancellationToken).ConfigureAwait(false);
        var best = scores.OrderByDescending(s => s.Score).First();
        Report(best.Index == 1, string.Create(CultureInfo.InvariantCulture,
            $"the relevant passage ranks first (score {best.Score:F3}, {watch.ElapsedMilliseconds} ms)"));

        foreach (var budget in new[] { new RerankBudget(10, 600), new RerankBudget(30, 2000) })
        {
            var documents = Enumerable.Range(0, budget.TopN).Select(i => EmbedderCheck.Synthetic(budget.MaxDocChars + i)[i..]).ToList();
            watch.Restart();
            await reranker.RerankAsync(Query, documents, cancellationToken).ConfigureAwait(false);
            var ms = watch.ElapsedMilliseconds;
            Report(ms <= options.TimeoutMs, string.Create(CultureInfo.InvariantCulture,
                $"{budget.Label}: {ms} ms (Reranker:TimeoutMs {options.TimeoutMs}; slower calls keep the fused order)"));
        }

        return new RerankerCheckResult(passed, lines);
    }
}
