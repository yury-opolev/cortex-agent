using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Executes a shell command inside the container.
/// Runs with a configurable timeout. Captures stdout and stderr.
/// </summary>
internal sealed class RunCommandTool : IAgentTool
{
    private readonly string sandboxRoot;
    private const int DefaultTimeoutSeconds = 60;
    private const int MaxTimeoutSeconds = 600; // 10 minutes hard cap (package installs can be slow)
    private const int MaxOutputBytes = 512 * 1024; // 512 KB max output

    public RunCommandTool(string sandboxRoot)
    {
        this.sandboxRoot = sandboxRoot;
    }

    public string Name => "run_command";

    public string Description =>
        "Execute a shell command inside the container. The working directory is the data directory. " +
        "Commands run with a timeout (default 60s, max 600s). " +
        "You have outbound internet access and can use: curl, wget, python3, pip, node, npm, " +
        "git, jq, ripgrep, build-essential (gcc/make). " +
        "To install additional tools at runtime use: brew install <pkg>, pip install <pkg>, " +
        "or npm install -g <pkg> — all work without root.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "command": {
              "type": "string",
              "description": "The shell command to execute"
            },
            "timeout": {
              "type": "integer",
              "description": "Timeout in seconds (default: 60, max: 600)"
            },
            "workdir": {
              "type": "string",
              "description": "Working directory relative to data root (default: data root)"
            }
          },
          "required": ["command"]
        }
        """;

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("command", out var commandElement))
            {
                return AgentToolResult.Fail("Missing required parameter: command");
            }

            var command = commandElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command))
            {
                return AgentToolResult.Fail("Command cannot be empty");
            }

            var timeoutSeconds = DefaultTimeoutSeconds;
            if (root.TryGetProperty("timeout", out var timeoutEl))
            {
                timeoutSeconds = Math.Clamp(timeoutEl.GetInt32(), 1, MaxTimeoutSeconds);
            }

            var workDir = Path.GetFullPath(this.sandboxRoot);
            if (root.TryGetProperty("workdir", out var workdirEl) && workdirEl.GetString() is { Length: > 0 } wd)
            {
                workDir = SandboxPathResolver.Resolve(this.sandboxRoot, wd);
                if (!Directory.Exists(workDir))
                {
                    return AgentToolResult.Fail($"Working directory not found: {wd}");
                }
            }

            // Determine shell based on OS — use bash login shell on Linux so that
            // Homebrew, pip --user, and npm global paths are on PATH.
            // IMPORTANT: Use ArgumentList (not Arguments) on Linux so the command
            // string is passed as a single argv entry to bash -c.  The Arguments
            // property on Unix is parsed by .NET's own splitter which does NOT
            // honour single-quote grouping, so 'curl https://...' would be split
            // into multiple argv entries — breaking multi-word commands.
            var isWindows = OperatingSystem.IsWindows();

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (isWindows)
            {
                process.StartInfo.Arguments = $"/c {command}";
            }
            else
            {
                process.StartInfo.ArgumentList.Add("-lc");
                process.StartInfo.ArgumentList.Add(command);
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var outputLock = new Lock();
            var totalOutputSize = 0;

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (outputLock)
                {
                    if (totalOutputSize < MaxOutputBytes)
                    {
                        stdout.AppendLine(e.Data);
                        totalOutputSize += e.Data.Length + 1;
                    }
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (outputLock)
                {
                    if (totalOutputSize < MaxOutputBytes)
                    {
                        stderr.AppendLine(e.Data);
                        totalOutputSize += e.Data.Length + 1;
                    }
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout -- kill the process
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Process already exited
                }

                return new AgentToolResult
                {
                    Success = false,
                    Content = stdout.ToString(),
                    Error = $"Command timed out after {timeoutSeconds}s",
                };
            }

            var output = new StringBuilder();

            if (stdout.Length > 0)
            {
                output.AppendLine("--- stdout ---");
                output.Append(stdout);
            }

            if (stderr.Length > 0)
            {
                output.AppendLine("--- stderr ---");
                output.Append(stderr);
            }

            if (totalOutputSize >= MaxOutputBytes)
            {
                output.AppendLine("--- output truncated ---");
            }

            var exitCode = process.ExitCode;
            output.AppendLine(CultureInfo.InvariantCulture, $"--- exit code: {exitCode} ---");

            return new AgentToolResult
            {
                Success = exitCode == 0,
                Content = output.ToString().TrimEnd(),
                Error = exitCode != 0 ? $"Command failed with exit code {exitCode}" : null,
            };
        }
        catch (ArgumentException ex)
        {
            return AgentToolResult.Fail(ex.Message);
        }
        catch (IOException ex)
        {
            return AgentToolResult.Fail($"IO error: {ex.Message}");
        }
    }
}
