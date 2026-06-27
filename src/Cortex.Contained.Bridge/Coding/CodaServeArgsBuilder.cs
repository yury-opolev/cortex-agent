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
        string? provider = null,
        string? goal = null,
        bool sessionMemory = false)
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

        // NOTE: we intentionally do NOT pass --model. Cortex pins only the provider; the model is
        // resolved by coda from its own configured default (~/.coda/settings.json defaultModel),
        // so a stale model can't be injected here.
        if (!string.IsNullOrWhiteSpace(provider))
        {
            args.Add("--provider");
            args.Add(provider);
        }

        if (!string.IsNullOrWhiteSpace(goal))
        {
            args.Add("--goal");
            args.Add(goal);
        }

        if (sessionMemory)
        {
            args.Add("--session-memory");
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
