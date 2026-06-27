namespace Cortex.Contained.Launcher.Services;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
