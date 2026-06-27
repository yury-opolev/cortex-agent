using System.Diagnostics;

namespace Cortex.Contained.Launcher.Services;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Process StartBackground(
        string fileName,
        string arguments,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        Dictionary<string, string>? environmentVariables = null);
}
