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
        // GET — current policy (defaults to "host" when unset) + curated dir + the selectable policies.
        app.MapGet("/api/coding/mcp-settings", (CodaMcpSettingsStore store) =>
        {
            var settings = store.Get();
            var policy = (settings.Mcp ?? CodaMcpPolicy.Host).ToString().ToLowerInvariant();
            return Results.Ok(new CodaMcpSettingsDto(policy, settings.CuratedMcpDir, PolicyNames()));
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
