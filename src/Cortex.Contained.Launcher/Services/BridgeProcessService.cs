using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Launcher.Services;

public sealed partial class BridgeProcessService : IDisposable
{
    private readonly IProcessRunner processRunner;
    private readonly ILogger<BridgeProcessService> logger;
    private Process? bridgeProcess;

    public BridgeProcessService(IProcessRunner processRunner, ILogger<BridgeProcessService> logger)
    {
        this.processRunner = processRunner;
        this.logger = logger;
    }

    public bool IsRunning => this.bridgeProcess is not null && !this.bridgeProcess.HasExited;

    /// <summary>
    /// Raised when the Bridge child process exits. The argument is the process
    /// exit code — consumers use it to distinguish a Web-UI-initiated restart
    /// (code 73, see <c>Cortex.Contained.Bridge.Control.RestartCoordinator</c>)
    /// from a normal stop or crash. Subscribers receive the exit code on the
    /// thread that .NET fires <see cref="Process.Exited"/> on; marshal to the
    /// Avalonia UI thread before touching UI state.
    /// </summary>
    public event Action<int>? OnExited;

    public void Start(string bridgeExePath, string hubToken)
    {
        if (this.IsRunning)
        {
            this.LogBridgeAlreadyRunning();
            return;
        }

        this.LogStartingBridge(bridgeExePath);

        this.bridgeProcess = this.processRunner.StartBackground(
            bridgeExePath,
            string.Empty,
            line => this.LogBridgeOutput(line),
            line => this.LogBridgeError(line),
            environmentVariables: new Dictionary<string, string>
            {
                ["CORTEX_HUB_TOKEN"] = hubToken,
            });

        this.bridgeProcess.EnableRaisingEvents = true;
        this.bridgeProcess.Exited += (_, _) =>
        {
            var code = this.bridgeProcess.ExitCode;
            this.LogBridgeExited(code);
            this.OnExited?.Invoke(code);
        };
    }

    /// <summary>
    /// Test seam: fires <see cref="OnExited"/> synchronously with the given
    /// code. Needed because <see cref="Process.Exited"/> cannot be raised from
    /// outside the running process, and substituting <see cref="Process"/> in
    /// tests is brittle. Production code never calls this.
    /// </summary>
    internal void RaiseExitedForTesting(int exitCode) => this.OnExited?.Invoke(exitCode);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!this.IsRunning || this.bridgeProcess is null)
        {
            return;
        }

        this.LogStoppingBridge();

        // Send Ctrl+C signal for graceful shutdown
        this.bridgeProcess.CloseMainWindow();

        try
        {
            await this.bridgeProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            this.LogBridgeKilled();
            this.bridgeProcess.Kill(entireProcessTree: true);
        }
    }

    public void Dispose()
    {
        if (this.bridgeProcess is not null)
        {
            try
            {
                if (!this.bridgeProcess.HasExited)
                {
                    this.bridgeProcess.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Process was never started or is no longer accessible — nothing to kill.
            }

            this.bridgeProcess.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting Bridge: {BridgePath}")]
    private partial void LogStartingBridge(string bridgePath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bridge is already running")]
    private partial void LogBridgeAlreadyRunning();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Bridge stdout: {Line}")]
    private partial void LogBridgeOutput(string line);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bridge stderr: {Line}")]
    private partial void LogBridgeError(string line);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bridge process exited with code {ExitCode}")]
    private partial void LogBridgeExited(int exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping Bridge process")]
    private partial void LogStoppingBridge();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bridge did not exit gracefully, killing process")]
    private partial void LogBridgeKilled();
}
