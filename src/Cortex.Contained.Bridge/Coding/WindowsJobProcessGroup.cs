using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// A Windows Job Object configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>. Every coda
/// process enrolled via <see cref="Register"/> is terminated by the OS the instant the last handle
/// to the job closes — which happens automatically when the Bridge process exits for ANY reason
/// (graceful shutdown, unhandled exception, or a hard kill). That makes "the Bridge died and left
/// orphaned <c>coda serve</c> processes" structurally impossible, without any startup scan.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsJobProcessGroup : ICodaProcessGroup, IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly ILogger<WindowsJobProcessGroup> logger;
    private readonly nint jobHandle;
    private bool disposed;

    public WindowsJobProcessGroup(ILogger<WindowsJobProcessGroup> logger)
    {
        this.logger = logger;

        this.jobHandle = CreateJobObject(nint.Zero, null);
        if (this.jobHandle == nint.Zero)
        {
            throw new InvalidOperationException(
                $"CreateJobObject failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var infoPtr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, infoPtr, fDeleteOld: false);
            if (!SetInformationJobObject(this.jobHandle, JobObjectExtendedLimitInformation, infoPtr, (uint)length))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(this.jobHandle);
                throw new InvalidOperationException(
                    $"SetInformationJobObject failed (Win32 error {error}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
        }

        this.LogJobCreated();
    }

    public void Register(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (this.disposed)
        {
            return;
        }

        try
        {
            if (!AssignProcessToJobObject(this.jobHandle, process.Handle))
            {
                this.LogAssignFailed(process.Id, Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            // Best-effort: the per-session reactive reap still handles crashes during the Bridge's
            // lifetime; only the Bridge-death safety net degrades for this one process.
            this.LogAssignError(process.Id, ex);
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        // Closing the last handle to a KILL_ON_JOB_CLOSE job terminates every enrolled process.
        // (The OS also does this automatically when the Bridge process dies, so a missed Dispose —
        // e.g. on a hard crash — still reaps the coda children.)
        CloseHandle(this.jobHandle);
    }

    // ── native interop ─────────────────────────────────────────────────────────

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint hJob, int jobObjectInformationClass, nint lpJobObjectInformation, uint cbJobObjectInformationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LoggerMessage(EventId = 9310, Level = LogLevel.Information,
        Message = "Coda process group ready: coda children will be force-killed when the Bridge exits (KILL_ON_JOB_CLOSE)")]
    private partial void LogJobCreated();

    [LoggerMessage(EventId = 9311, Level = LogLevel.Warning,
        Message = "Failed to enroll coda process {pid} in the Bridge job (Win32 error {error}); Bridge-death cleanup degraded for it")]
    private partial void LogAssignFailed(int pid, int error);

    [LoggerMessage(EventId = 9312, Level = LogLevel.Warning,
        Message = "Error enrolling coda process {pid} in the Bridge job")]
    private partial void LogAssignError(int pid, Exception ex);
}
