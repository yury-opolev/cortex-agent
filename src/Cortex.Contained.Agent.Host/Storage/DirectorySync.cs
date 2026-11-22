using System.Runtime.InteropServices;
using System.Text;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>Best-effort fsync of a directory, so a rename inside it survives a power loss (Unix; a no-op on Windows).</summary>
public static partial class DirectorySync
{
    /// <summary>Returns true when the directory was flushed; never throws.</summary>
    public static bool TryFlush(string directory)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            var fd = Open(Encoding.UTF8.GetBytes(directory + '\0'), 0);
            if (fd < 0)
            {
                return false;
            }

            try
            {
                return Fsync(fd) == 0;
            }
            finally
            {
                _ = Close(fd);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Open(byte[] path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Fsync(int fd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Close(int fd);
}
