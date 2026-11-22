namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Best-effort erasure of plaintext files: each file is overwritten with zeros and flushed before it is
/// unlinked. Copy-on-write or journaling filesystems and SSD remapping may keep older blocks; like volume
/// snapshots, that residue is outside what the application can reach (documented).
/// </summary>
public static class SecureErase
{
    private const int ChunkBytes = 1024 * 1024;

    /// <summary>Erases every file below <paramref name="directory"/>, then the directory. Returns the number of files erased.</summary>
    public static int Directory(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return 0;
        }

        var files = System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
        foreach (var file in files)
        {
            File(file);
        }

        System.IO.Directory.Delete(directory, recursive: true);
        return files.Count;
    }

    /// <summary>Overwrites <paramref name="path"/> with zeros, flushes it to disk and deletes it.</summary>
    public static void File(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return;
        }

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var zeros = new byte[ChunkBytes];
            var remaining = stream.Length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, zeros.Length);
                stream.Write(zeros, 0, count);
                remaining -= count;
            }

            stream.Flush(flushToDisk: true);
        }

        System.IO.File.Delete(path);
    }
}
