using Cortex.Contained.Contracts.Coding;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Minimal-API endpoints for managing coding folders and checking coda auth status.
/// All endpoints require Bridge session authorization (same as all other /api/* admin endpoints).
/// </summary>
public static class CodingFoldersEndpoints
{
    private const string AuthHint = "Sign coda into a provider once by running `coda` and `/login` on the host.";

    /// <summary>Maps coding-folder and coda auth-status endpoints onto <paramref name="app"/>.</summary>
    public static void MapCodingFoldersEndpoints(this WebApplication app)
    {
        // GET /api/coding-folders — list all configured folders with existence check
        app.MapGet("/api/coding-folders", (CodingFoldersStore store) =>
        {
            var entries = store.Get();
            var dtos = entries.Select(ToDto).ToList();
            return Results.Ok(dtos);
        }).RequireAuthorization();

        // POST /api/coding-folders — add a folder
        app.MapPost("/api/coding-folders", async (HttpContext ctx, CodingFoldersStore store) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<AddFolderRequest>().ConfigureAwait(false);
            if (body is null)
            {
                return Results.BadRequest(new { error = "Request body is required" });
            }

            var (ok, error) = ValidateAddRequest(body.Path, body.Policy);
            if (!ok)
            {
                return Results.BadRequest(new { error });
            }

            var policy = ParsePolicy(body.Policy);
            var normalized = Path.GetFullPath(body.Path!).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var added = store.Add(normalized, body.Label, policy);
            return Results.Ok(new { added });
        }).RequireAuthorization();

        // DELETE /api/coding-folders — remove a folder
        app.MapDelete("/api/coding-folders", async (HttpContext ctx, CodingFoldersStore store) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<RemoveFolderRequest>().ConfigureAwait(false);
            if (body is null || string.IsNullOrWhiteSpace(body.Path))
            {
                return Results.BadRequest(new { error = "path is required" });
            }

            var removed = store.Remove(body.Path);
            return Results.Ok(new { removed });
        }).RequireAuthorization();

        // GET /api/coding/auth-status — coda binary presence check (no process spawn)
        app.MapGet("/api/coding/auth-status", (IOptions<CodaOptions> options) =>
        {
            var status = AuthStatus(options.Value.CodaBinaryPath);
            return Results.Ok(status);
        }).RequireAuthorization();
    }

    // ── Static testable helpers ────────────────────────────────────────

    /// <summary>
    /// Validates an add-folder request. Returns <c>(true, null)</c> on success,
    /// or <c>(false, errorMessage)</c> on failure.
    /// </summary>
    public static (bool ok, string? error) ValidateAddRequest(string? path, string? policy)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (false, "path is required");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            return (false, "path must be an absolute (fully-qualified) path");
        }

        if (!Directory.Exists(path))
        {
            return (false, $"path does not exist: {path}");
        }

        if (!string.IsNullOrWhiteSpace(policy)
            && !Enum.TryParse<CodingPolicy>(policy, ignoreCase: true, out _))
        {
            return (false, $"policy '{policy}' is not valid; expected Prompt, YoloSafe, or Yolo");
        }

        return (true, null);
    }

    /// <summary>
    /// Parses a policy string to <see cref="CodingPolicy"/>.
    /// Returns <see cref="CodingPolicy.YoloSafe"/> when <paramref name="policy"/> is null or empty.
    /// </summary>
    public static CodingPolicy ParsePolicy(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return CodingPolicy.YoloSafe;
        }

        return Enum.TryParse<CodingPolicy>(policy, ignoreCase: true, out var parsed)
            ? parsed
            : CodingPolicy.YoloSafe;
    }

    /// <summary>
    /// Maps a <see cref="CodingFolderEntry"/> to a named-property DTO for the REST response.
    /// The policy is serialized as its string name (e.g. "YoloSafe") so the web UI can match it.
    /// </summary>
    public static CodingFolderDto ToDto(CodingFolderEntry entry)
    {
        return new CodingFolderDto(
            entry.Path,
            entry.Label,
            entry.DefaultPolicy.ToString(),
            Directory.Exists(entry.Path));
    }

    /// <summary>
    /// Returns a best-effort coda auth status given the configured binary path.
    /// Does NOT spawn coda; simply checks whether the binary file exists.
    /// </summary>
    public static CodaAuthStatus AuthStatus(string codaBinaryPath)
    {
        bool binaryFound;

        if (string.IsNullOrWhiteSpace(codaBinaryPath))
        {
            binaryFound = false;
        }
        else if (File.Exists(codaBinaryPath))
        {
            binaryFound = true;
        }
        else
        {
            // PATH-based binary (e.g. "coda") — attempt to resolve via where/which.
            // We do a lightweight check: if codaBinaryPath has no directory component it may be on PATH.
            // We don't spawn the process; this is a best-effort hint.
            binaryFound = !Path.IsPathRooted(codaBinaryPath)
                && FindOnPath(codaBinaryPath);
        }

        return new CodaAuthStatus(binaryFound, AuthHint);
    }

    private static bool FindOnPath(string executableName)
    {
        // Search PATH entries for the executable (Windows: append .exe if needed).
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = new[] { string.Empty, ".exe", ".cmd", ".bat" };

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), executableName + ext);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ── Response DTOs (records → named camelCase JSON; ValueTuples do NOT serialize named fields) ──

    /// <summary>One coding folder row for the web UI. Policy is the string name (e.g. "YoloSafe").</summary>
    public sealed record CodingFolderDto(string Path, string? Label, string Policy, bool Exists);

    /// <summary>Coda auth/binary status for the web UI.</summary>
    public sealed record CodaAuthStatus(bool BinaryFound, string Hint);

    // ── Request DTOs ──────────────────────────────────────────────────

    private sealed class AddFolderRequest
    {
        public string? Path { get; set; }
        public string? Label { get; set; }
        public string? Policy { get; set; }
    }

    private sealed class RemoveFolderRequest
    {
        public string? Path { get; set; }
    }
}
