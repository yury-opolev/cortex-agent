namespace Cortex.Contained.Bridge.Coding;

// ── Public DTOs (top-level so they can be referenced from tests without nesting) ──

/// <summary>Available coda provider entry (id + display label).</summary>
public sealed record CodaProviderOption(string Id, string Label);

/// <summary>Response DTO for GET /api/coding/model-settings.</summary>
public sealed record CodaModelSettingsDto(
    string? Provider,
    string? Model,
    IReadOnlyList<CodaProviderOption> AvailableProviders);

/// <summary>
/// Minimal-API endpoints for getting and setting the coda provider/model selection.
/// All endpoints require Bridge session authorization.
/// </summary>
public static class CodingModelEndpoints
{
    /// <summary>Maps the provider/model settings endpoints onto <paramref name="app"/>.</summary>
    public static void MapCodingModelEndpoints(this WebApplication app)
    {
        // GET /api/coding/model-settings — return current store values + available providers
        app.MapGet("/api/coding/model-settings", (CodaModelSettingsStore store) =>
        {
            var settings = store.Get();
            var dto = new CodaModelSettingsDto(settings.Provider, settings.Model, KnownProviders());
            return Results.Ok(dto);
        }).RequireAuthorization();

        // PUT /api/coding/model-settings — validate + persist new provider/model selection
        app.MapPut("/api/coding/model-settings", async (HttpContext ctx, CodaModelSettingsStore store) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<ModelSettingsRequest>().ConfigureAwait(false);
            if (body is null)
            {
                return Results.BadRequest(new { error = "Request body is required" });
            }

            var (ok, error) = ValidateProvider(body.Provider);
            if (!ok)
            {
                return Results.BadRequest(new { error });
            }

            store.Set(body.Provider, body.Model);
            return Results.Ok(new { saved = true });
        }).RequireAuthorization();
    }

    // ── Static testable helpers ────────────────────────────────────────────────

    /// <summary>
    /// Returns the list of known coda provider options.
    /// These correspond to the provider IDs that <c>coda serve --provider</c> accepts.
    /// </summary>
    public static IReadOnlyList<CodaProviderOption> KnownProviders() =>
    [
        new CodaProviderOption("claude", "Claude (claude.ai)"),
        new CodaProviderOption("copilot", "GitHub Copilot"),
        new CodaProviderOption("apikey", "Anthropic API Key"),
    ];

    /// <summary>
    /// Validates a provider id. Null/empty means "use coda's default" and is always valid.
    /// A non-empty value must be one of the known provider ids.
    /// Returns <c>(true, null)</c> on success, <c>(false, errorMessage)</c> on failure.
    /// </summary>
    public static (bool ok, string? error) ValidateProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return (true, null);
        }

        var known = KnownProviders();
        if (known.Any(p => string.Equals(p.Id, provider, StringComparison.Ordinal)))
        {
            return (true, null);
        }

        var validIds = string.Join(", ", known.Select(p => p.Id));
        return (false, $"Unknown provider '{provider}'. Valid values: {validIds}, or empty for coda's default.");
    }

    // ── Request DTOs ──────────────────────────────────────────────────────────

    private sealed class ModelSettingsRequest
    {
        public string? Provider { get; set; }
        public string? Model { get; set; }
    }
}
