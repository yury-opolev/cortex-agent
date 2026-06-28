using System.Collections.Frozen;
using Cortex.Contained.Contracts.Messages;

namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// Loads and validates an image file from the agent's sandbox for outbound
/// delivery as a <see cref="MediaAttachment"/>. Isolated from the tool so the
/// validation rules (sandbox containment, existence, type, size) are
/// independently unit-testable.
/// </summary>
internal sealed class AttachmentLoader
{
    /// <summary>Per-file size cap — safe for non-boosted Discord and the &lt;10 MB SignalR/in-memory budget.</summary>
    internal const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>Allowed image extensions → MIME type. Mirrors the Discord channel's image media types.</summary>
    private static readonly FrozenDictionary<string, string> ImageMimeByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly string sandboxRoot;

    public AttachmentLoader(string sandboxRoot)
    {
        this.sandboxRoot = sandboxRoot;
    }

    /// <summary>
    /// Resolve <paramref name="userPath"/> within the sandbox, validate it as an
    /// image within the size cap, and read its bytes into a <see cref="MediaAttachment"/>.
    /// Returns a typed failure (never throws for expected validation problems) so the
    /// tool can relay a clear message to the LLM.
    /// </summary>
    public AttachmentLoadResult Load(string userPath)
    {
        string resolved;
        try
        {
            resolved = SandboxPathResolver.ResolveAndVerify(this.sandboxRoot, userPath);
        }
        catch (ArgumentException ex)
        {
            return AttachmentLoadResult.Fail($"Invalid attachment path '{userPath}': {ex.Message}");
        }

        if (!File.Exists(resolved))
        {
            return AttachmentLoadResult.Fail($"Attachment not found: {userPath}");
        }

        var extension = Path.GetExtension(resolved);
        if (!ImageMimeByExtension.TryGetValue(extension, out var mimeType))
        {
            return AttachmentLoadResult.Fail(
                $"Unsupported image type '{extension}' for '{userPath}'. Allowed: .png, .jpg, .jpeg, .gif, .webp");
        }

        var fileInfo = new FileInfo(resolved);
        if (fileInfo.Length == 0)
        {
            return AttachmentLoadResult.Fail($"Attachment '{userPath}' is empty.");
        }

        if (fileInfo.Length > MaxBytes)
        {
            return AttachmentLoadResult.Fail(
                $"Attachment '{userPath}' is {fileInfo.Length:N0} bytes; the maximum is {MaxBytes:N0} bytes (8 MB).");
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(resolved);
        }
        catch (IOException ex)
        {
            return AttachmentLoadResult.Fail($"Failed to read attachment '{userPath}': {ex.Message}");
        }

        return AttachmentLoadResult.Ok(new MediaAttachment
        {
            MimeType = mimeType,
            FileName = Path.GetFileName(resolved),
            Data = data,
            SizeBytes = data.LongLength,
        });
    }
}

/// <summary>Outcome of <see cref="AttachmentLoader.Load"/>.</summary>
internal sealed record AttachmentLoadResult
{
    public required bool Success { get; init; }

    /// <summary>The loaded attachment when <see cref="Success"/> is true.</summary>
    public MediaAttachment? Attachment { get; init; }

    /// <summary>Human-readable failure reason when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }

    public static AttachmentLoadResult Ok(MediaAttachment attachment) =>
        new() { Success = true, Attachment = attachment };

    public static AttachmentLoadResult Fail(string error) =>
        new() { Success = false, Error = error };
}
