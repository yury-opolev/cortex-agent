using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>Pure construction of the <c>coda serve</c> CLI arguments for a session.</summary>
public static class CodaServeArgsBuilder
{
    public static List<string> Build(
        string sessionId,
        string workingFolder,
        CodingPolicy policy,
        bool isResume,
        string? goal = null,
        bool sessionMemory = false,
        CodaMcpPolicy mcp = CodaMcpPolicy.Host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingFolder);

        var args = new List<string>
        {
            "serve",
            "--cwd", workingFolder,
            "--session-id", sessionId,
            "--permission-mode", PermissionModeArg(policy),
            // Always force telemetry on for Cortex-spawned coda so every run is observable,
            // independent of the machine ~/.coda/settings.json (early-dev requirement).
            "--telemetry",
        };

        // NOTE: we intentionally do NOT pass --provider or --model. coda is single-provider and
        // self-resolves its one connected provider; the model comes from coda's own configured
        // default (~/.coda/settings.json defaultModel), so neither can be pinned here.
        if (!string.IsNullOrWhiteSpace(goal))
        {
            args.Add("--goal");
            args.Add(goal);
        }

        if (sessionMemory)
        {
            args.Add("--session-memory");
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

    private static string PermissionModeArg(CodingPolicy policy) => policy switch
    {
        CodingPolicy.Prompt => "default",
        CodingPolicy.YoloSafe => "yolo-safe",
        CodingPolicy.Yolo => "yolo",
        _ => "default",
    };
}
