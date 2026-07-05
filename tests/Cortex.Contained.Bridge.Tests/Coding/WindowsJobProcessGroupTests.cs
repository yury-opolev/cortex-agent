using System.Diagnostics;
using Cortex.Contained.Bridge.Coding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Coding;

/// <summary>
/// Verifies the Windows Job Object enrollment: a process registered in the group is force-killed by
/// the OS when the group's job handle closes (KILL_ON_JOB_CLOSE) — the Bridge-death safety net that
/// makes orphaned <c>coda serve</c> processes impossible after a Bridge restart/crash.
/// </summary>
public sealed class WindowsJobProcessGroupTests
{
    [Fact]
    public void Dispose_KillsEnrolledProcess()
    {
        // cmd.exe with redirected (but unwritten) stdin blocks, so it is a reliable long-runner.
        using var proc = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        try
        {
            var group = new WindowsJobProcessGroup(NullLogger<WindowsJobProcessGroup>.Instance);
            group.Register(proc);
            Assert.False(proc.HasExited);

            // Closing the last handle to a KILL_ON_JOB_CLOSE job terminates every enrolled process.
            group.Dispose();

            Assert.True(proc.WaitForExit(5000), "disposing the job must reap the enrolled process");
        }
        finally
        {
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
        }
    }
}
