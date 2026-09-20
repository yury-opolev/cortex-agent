using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>Pure construction of the <c>coda serve</c> CLI arguments for a session.</summary>
public static class CodaServeArgsBuilder
{
    public static List<string> Build(
        string workingFolder,
        CodingPolicy policy,
        bool isResume,
        string? goal = null,
        CodaMcpPolicy mcp = CodaMcpPolicy.Host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingFolder);

        var args = new List<string>
        {
            "serve",
            "--cwd", workingFolder,
            "--permission-mode", PermissionModeArg(policy),
            // Always force diagnostics on for Cortex-spawned coda so every run is observable,
            // independent of the machine ~/.coda/settings.json (early-dev requirement). This
            // replaces the retired --telemetry flag: the per-run log that coda reports back as
            // `telemetryLogPath` from `initialize` is produced by the diagnostics logger.
            "--diagnostic-verbosity", "debug",
        };

        // NOTE: the session id is NOT passed on the command line. coda retired --session-id;
        // resuming is done by passing `sessionId` to the `initialize` request instead
        // (see CodaJsonRpcConnection.InitializeAsync), which is what the Bridge already does.

        // NOTE: we intentionally do NOT pass --provider or --model. coda is single-provider and
        // self-resolves its one connected provider; the model comes from coda's own configured
        // default (~/.coda/settings.json defaultModel), so neither can be pinned here.
        if (!string.IsNullOrWhiteSpace(goal))
        {
            args.Add("--goal");
            args.Add(goal);
        }

        // MCP policy: Off disables coda's MCP client outright. Curated selects an orchestrator set via
        // CODA_USER_MCP_DIR (see CodaMcpEnvironment) AND suppresses the repo's <cwd>/.mcp.json with
        // --no-project-mcp, so the coding engine sees only the vetted set (true isolation). Host adds
        // nothing — coda sees the full host config.
        if (mcp == CodaMcpPolicy.Off)
        {
            args.Add("--no-mcp");
        }
        else if (mcp == CodaMcpPolicy.Curated)
        {
            args.Add("--no-project-mcp");
        }

        return args;
    }

    /// <summary>
    /// Maps a Cortex policy onto a coda permission mode.
    /// </summary>
    /// <remarks>
    /// coda's accepted set is <c>default</c>, <c>acceptEdits</c>, <c>plan</c> and
    /// <c>bypassPermissions</c>; an unrecognised value is a hard startup failure, not a
    /// silent downgrade. <see cref="CodingPolicy.YoloSafe"/> therefore maps to
    /// <c>acceptEdits</c> — coda retired <c>yolo-safe</c>, and <c>acceptEdits</c> is its
    /// nearest surviving meaning: file edits apply without prompting while genuinely risky
    /// actions (shell commands) still raise <c>request/permission</c>. Mapping it to
    /// <c>bypassPermissions</c> instead would silently widen a policy the operator chose
    /// precisely because it was narrower than Yolo.
    /// </remarks>
    private static string PermissionModeArg(CodingPolicy policy) => policy switch
    {
        CodingPolicy.Prompt => "default",
        CodingPolicy.YoloSafe => "acceptEdits",
        CodingPolicy.Yolo => "bypassPermissions",
        _ => "default",
    };
}
