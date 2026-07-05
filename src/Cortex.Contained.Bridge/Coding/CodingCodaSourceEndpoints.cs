using System.Diagnostics;
using Cortex.Contained.Contracts.Coding;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Resolved coda-source state for the coding settings UI: the selected <paramref name="Source"/>
/// (auto/host/bundled), the <paramref name="ResolvedPath"/> the resolver picked for it, the detected
/// <paramref name="Version"/> (best-effort <c>&lt;path&gt; --version</c>, null when unavailable), and
/// whether a bundled coda ships next to the Bridge (<paramref name="BundlePresent"/>).
/// </summary>
public sealed record CodaSourceState(string Source, string ResolvedPath, string? Version, bool BundlePresent);

/// <summary>
/// Minimal-API endpoints for getting and setting which coda binary the Bridge launches
/// (auto / host / bundled), shown in the coding settings UI. Persisted to
/// <see cref="CodaSourceStore"/>, which takes precedence over cortex.yml's <c>Coding:Coda:Source</c>.
/// A single setting per Bridge (like the MCP policy), so the routes are not tenant-scoped —
/// mirroring <see cref="CodingMcpEndpoints"/>. All endpoints require Bridge session authorization.
/// </summary>
public static class CodingCodaSourceEndpoints
{
    public static void MapCodaSourceEndpoints(this WebApplication app)
    {
        // GET — the effective source: the UI store when set, otherwise the cortex.yml value (NOT a
        // hardcoded default), so the UI reflects reality and a Save can't silently downgrade it.
        app.MapGet("/api/coding/coda-source", async (CodaSourceStore store, IOptionsMonitor<CodaOptions> codaOptions, CancellationToken cancellationToken) =>
        {
            var source = store.Get() ?? codaOptions.CurrentValue.Source;
            var state = await BuildStateAsync(source, cancellationToken).ConfigureAwait(false);
            return Results.Ok(state);
        }).RequireAuthorization();

        // PUT — validate + persist the selection, then return the freshly-resolved state so the UI
        // updates its resolved-path / version display without a second round-trip.
        app.MapPut("/api/coding/coda-source", async (HttpContext ctx, CodaSourceStore store, CancellationToken cancellationToken) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<CodaSourceRequest>(cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                return Results.BadRequest(new { error = "Request body is required" });
            }

            var (ok, source, error) = ParseSource(body.Source);
            if (!ok)
            {
                return Results.BadRequest(new { error });
            }

            store.Set(source);
            var state = await BuildStateAsync(source, cancellationToken).ConfigureAwait(false);
            return Results.Ok(state);
        }).RequireAuthorization();
    }

    // ── Static testable helpers ────────────────────────────────────────────────

    /// <summary>
    /// Parse a source string. Null/empty → <see cref="CodaSource.Auto"/>. Case-insensitive.
    /// Returns <c>(false, Auto, error)</c> for an unknown value.
    /// </summary>
    public static (bool ok, CodaSource source, string? error) ParseSource(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" => (true, CodaSource.Auto, null),
            "host" => (true, CodaSource.Host, null),
            "bundled" => (true, CodaSource.Bundled, null),
            _ => (false, CodaSource.Auto, $"Unknown coda source '{value}'. Valid values: auto, host, bundled."),
        };

    /// <summary>
    /// Assemble the resolved state for <paramref name="source"/>: resolve the binary path, check whether
    /// a bundled coda ships next to the Bridge, and best-effort probe its <c>--version</c>.
    /// </summary>
    internal static async Task<CodaSourceState> BuildStateAsync(CodaSource source, CancellationToken cancellationToken = default)
    {
        var (path, _) = CodaBinaryResolver.Resolve(source, AppContext.BaseDirectory, File.Exists);
        var bundlePresent = File.Exists(Path.Combine(AppContext.BaseDirectory, "coda", "coda.exe"));
        var version = await TryProbeVersionAsync(path, cancellationToken).ConfigureAwait(false);
        return new CodaSourceState(source.ToString().ToLowerInvariant(), path, version, bundlePresent);
    }

    /// <summary>
    /// Run <c>&lt;path&gt; --version</c> and return its first stdout line. Best-effort: returns null on
    /// any failure (missing binary, non-zero exit, no output) and never blocks longer than ~3s.
    /// </summary>
    internal static async Task<string?> TryProbeVersionAsync(string path, CancellationToken cancellationToken = default)
    {
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo(path, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            // Drain stderr concurrently so a binary that writes a banner/warning to stderr can't fill
            // the (finite) stderr pipe and stall its own stdout write — which would yield a false
            // "unknown version" after the full timeout. Fire-and-forget; swallow (best-effort probe).
            var drainStderr = DrainAsync(proc.StandardError, timeout.Token);

            var line = await proc.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            _ = drainStderr; // observed via its own try/catch; nothing to await here.
            return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
        }
        catch
        {
            // Missing binary, cancellation/timeout, or any I/O fault → version is simply "unknown".
            return null;
        }
        finally
        {
            try
            {
                if (proc is { HasExited: false })
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Process already gone / race — nothing to clean up.
            }

            proc?.Dispose();
        }
    }

    /// <summary>Read a stream to end to keep its pipe drained; swallow any fault (best-effort).</summary>
    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Timeout/cancellation or the process was killed — nothing to observe.
        }
    }

    private sealed class CodaSourceRequest
    {
        public string? Source { get; set; }
    }
}
