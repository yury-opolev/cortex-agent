namespace Cortex.Contained.Bridge.Setup;

/// <summary>
/// Raised when a GitHub Copilot setup step (device-flow init, token poll/exchange) fails in a
/// way the wizard should report to the user. Carries the structured detail GitHub returned —
/// HTTP status, the OAuth <c>error</c> code, and a human-readable description — so the endpoint
/// can both surface the real cause to the user and emit greppable <c>[copilot-setup]</c>
/// telemetry, instead of the old bare "400 Bad Request".
/// </summary>
public sealed class CopilotSetupException : Exception
{
    /// <summary>HTTP status code from GitHub, when the failure was an HTTP response (else null).</summary>
    public int? StatusCode { get; }

    /// <summary>GitHub's machine-readable OAuth error code (e.g. <c>device_flow_disabled</c>), if any.</summary>
    public string? GitHubError { get; }

    /// <summary>The GitHub auth host the failing request targeted.</summary>
    public string Host { get; }

    public CopilotSetupException(string message, string host, int? statusCode = null, string? gitHubError = null, Exception? innerException = null)
        : base(message, innerException)
    {
        this.Host = host;
        this.StatusCode = statusCode;
        this.GitHubError = gitHubError;
    }
}
