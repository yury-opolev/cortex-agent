using Microsoft.Extensions.Options;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>Response DTO for GET /api/coding/mcp-settings.</summary>
public sealed record CodaMcpSettingsDto(string Mcp, string? CuratedMcpDir, IReadOnlyList<string> AvailablePolicies);

/// <summary>
/// Minimal-API endpoints for getting and setting the coda MCP policy (host / curated / off) shown in
/// the coding settings UI. Persisted to <see cref="CodaMcpSettingsStore"/>, which takes precedence
/// over cortex.yml. All endpoints require Bridge session authorization.
/// </summary>
public static class CodingMcpEndpoints
{
    public static void MapCodingMcpEndpoints(this WebApplication app)
    {
        // GET — the effective policy: the UI store when set, otherwise the cortex.yml value (NOT a
        // hardcoded default), so the dropdown reflects reality and a Save can't silently downgrade it.
        app.MapGet("/api/coding/mcp-settings", (CodaMcpSettingsStore store, IOptionsMonitor<CodaOptions> codaOptions) =>
        {
            var yaml = codaOptions.CurrentValue;
            return Results.Ok(BuildDto(store.Get(), yaml.Mcp, yaml.CuratedMcpDir));
        }).RequireAuthorization();

        // PUT — validate + persist the selection.
        app.MapPut("/api/coding/mcp-settings", async (HttpContext ctx, CodaMcpSettingsStore store) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<McpSettingsRequest>().ConfigureAwait(false);
            if (body is null)
            {
                return Results.BadRequest(new { error = "Request body is required" });
            }

            var (ok, policy, error) = ParsePolicy(body.Mcp);
            if (!ok)
            {
                return Results.BadRequest(new { error });
            }

            if (policy == CodaMcpPolicy.Curated && string.IsNullOrWhiteSpace(body.CuratedMcpDir))
            {
                return Results.BadRequest(new { error = "The 'curated' policy requires a curated MCP directory." });
            }

            store.Set(policy, body.CuratedMcpDir);
            return Results.Ok(new { saved = true });
        }).RequireAuthorization();
    }

    // ── Static testable helpers ────────────────────────────────────────────────

    /// <summary>
    /// Build the display DTO: the UI store's values win, otherwise the cortex.yml <paramref name="yamlMcp"/>
    /// / <paramref name="yamlCuratedDir"/> — so an unset store shows the true effective policy.
    /// </summary>
    public static CodaMcpSettingsDto BuildDto(CodaMcpSettings stored, CodaMcpPolicy yamlMcp, string? yamlCuratedDir)
    {
        var policy = (stored.Mcp ?? yamlMcp).ToString().ToLowerInvariant();
        var curatedDir = string.IsNullOrWhiteSpace(stored.CuratedMcpDir) ? yamlCuratedDir : stored.CuratedMcpDir;
        return new CodaMcpSettingsDto(policy, curatedDir, PolicyNames());
    }

    /// <summary>The selectable MCP policies, in display order.</summary>
    public static IReadOnlyList<string> PolicyNames() => ["host", "curated", "off"];

    /// <summary>
    /// Parse a policy string. Null/empty → <see cref="CodaMcpPolicy.Host"/>. Returns
    /// <c>(false, Host, error)</c> for an unknown value.
    /// </summary>
    public static (bool ok, CodaMcpPolicy policy, string? error) ParsePolicy(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "host" => (true, CodaMcpPolicy.Host, null),
            "curated" => (true, CodaMcpPolicy.Curated, null),
            "off" => (true, CodaMcpPolicy.Off, null),
            _ => (false, CodaMcpPolicy.Host, $"Unknown MCP policy '{value}'. Valid values: host, curated, off."),
        };

    private sealed class McpSettingsRequest
    {
        public string? Mcp { get; set; }

        public string? CuratedMcpDir { get; set; }
    }
}
