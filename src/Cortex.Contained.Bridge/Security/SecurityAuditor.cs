using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Security;

/// <summary>
/// Performs a security audit of the Bridge configuration.
/// Reports critical, warning, and informational findings.
/// </summary>
public sealed partial class SecurityAuditor
{
    private readonly ILogger<SecurityAuditor> logger;

    public SecurityAuditor(ILogger<SecurityAuditor> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Runs the security audit against the provided configuration.
    /// Returns a list of findings ordered by severity.
    /// </summary>
    public List<AuditFinding> Audit(BridgeConfig config, SecurityAuditOptions? options = null)
    {
        var opts = options ?? new SecurityAuditOptions();
        List<AuditFinding> findings = [];

        AuditHubToken(config, findings);
        AuditWebUi(config, findings);
        AuditLlmProviders(config, findings);
        AuditChannels(config, findings);

        if (opts.CheckFileSystem)
        {
            AuditFileSystem(opts.DataDirectory, findings);
        }

        this.LogAuditCompleted(findings.Count);

        return findings
            .OrderBy(static f => f.Severity)
            .ToList();
    }

    private static void AuditHubToken(BridgeConfig config, List<AuditFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(config.HubToken))
        {
            findings.Add(new AuditFinding(
                AuditSeverity.Critical,
                "Hub token is empty or not configured.",
                "Set a hub token in configuration or let SecretManager auto-generate one."));
            return;
        }

        if (config.HubToken.Length < 32)
        {
            findings.Add(new AuditFinding(
                AuditSeverity.Critical,
                $"Hub token is too short ({config.HubToken.Length} chars). Minimum 32 characters recommended.",
                "Regenerate the hub token using SecretManager or set a longer token."));
        }

        // Check if it looks like a placeholder
        if (config.HubToken is "changeme" or "default" or "password" or "token")
        {
            findings.Add(new AuditFinding(
                AuditSeverity.Critical,
                "Hub token appears to be a placeholder value.",
                "Generate a cryptographically random token."));
        }
    }

    private static void AuditWebUi(BridgeConfig config, List<AuditFinding> findings)
    {
        if (!config.WebUi.Enabled)
        {
            return;
        }

        if (config.WebUi.BindAddress is not ("127.0.0.1" or "localhost" or "::1"))
        {
            findings.Add(new AuditFinding(
                AuditSeverity.Critical,
                $"Web UI is bound to '{config.WebUi.BindAddress}' instead of loopback.",
                "Bind the Web UI to 127.0.0.1 to prevent network access, or add authentication."));
        }
    }

    private static void AuditLlmProviders(BridgeConfig config, List<AuditFinding> findings)
    {
        foreach (var provider in config.LlmProviders)
        {
            if (string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                findings.Add(new AuditFinding(
                    AuditSeverity.Warning,
                    $"LLM provider '{provider.Name}' has no API key configured.",
                    "Set the API key in configuration or use SecretManager to store it encrypted."));
            }
        }

        if (config.LlmProviders.Count == 0)
        {
            findings.Add(new AuditFinding(
                AuditSeverity.Info,
                "No LLM providers configured.",
                "Add at least one LLM provider for the agent to function."));
        }
    }

    private static void AuditChannels(BridgeConfig config, List<AuditFinding> findings)
    {
        // Channel-specific security checks can be added here as channels evolve
    }

    private static void AuditFileSystem(string? dataDirectory, List<AuditFinding> findings)
    {
        if (dataDirectory is null)
        {
            return;
        }

        if (!Directory.Exists(dataDirectory))
        {
            findings.Add(new AuditFinding(
                AuditSeverity.Info,
                $"Data directory '{dataDirectory}' does not exist yet.",
                "It will be created on first run."));
            return;
        }

        var secretsDir = Path.Combine(dataDirectory, "secrets");
        if (Directory.Exists(secretsDir))
        {
            findings.Add(new AuditFinding(
                AuditSeverity.Info,
                "Secrets directory exists. Verify that ACLs restrict access to the current user only.",
                "Run the install script to set proper ACLs, or check manually."));
        }
    }

    // ── LoggerMessage ────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Security audit completed with {FindingCount} findings")]
    private partial void LogAuditCompleted(int findingCount);
}

/// <summary>Severity level for a security audit finding.</summary>
public enum AuditSeverity
{
    /// <summary>Must be fixed immediately — security risk.</summary>
    Critical = 0,

    /// <summary>Should be addressed — potential security concern.</summary>
    Warning = 1,

    /// <summary>Informational — good to know.</summary>
    Info = 2,
}

/// <summary>A single finding from the security audit.</summary>
public sealed record AuditFinding(
    AuditSeverity Severity,
    string Message,
    string Recommendation);

/// <summary>Options for running the security audit.</summary>
public sealed record SecurityAuditOptions
{
    /// <summary>Whether to check file system permissions.</summary>
    public bool CheckFileSystem { get; init; }

    /// <summary>The data directory to audit (e.g. C:\ProgramData\James).</summary>
    public string? DataDirectory { get; init; }
}
