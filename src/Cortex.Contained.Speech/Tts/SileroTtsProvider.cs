using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SileroSharp;

namespace Cortex.Contained.Speech.Tts;

/// <summary>
/// TTS provider for Silero v5 Russian voices via TorchSharp.
/// Outputs 48kHz mono 16-bit PCM. Supports streaming (sentence-by-sentence).
/// Variant selection controls which voices are available:
/// V5Russian (4 voices, CC BY-NC) or V5CisBase (28 voices, MIT).
/// </summary>
public sealed partial class SileroTtsProvider : ITtsProvider
{
    /// <summary>Placeholder download URL — update when GitHub release is created.</summary>
    private const string DownloadUrlV5Russian = "https://github.com/yury-opolev/silero-sharp/releases/download/v1.0.0/silero_v5_4_ru.tar.gz";
    private const string DownloadUrlV5CisBase = "https://github.com/yury-opolev/silero-sharp/releases/download/v1.0.0/silero_v5_cis_base.tar.gz";

    private readonly SileroTextToSpeech? engine;
    private readonly List<TtsVoiceInfo> voices;
    private readonly ILogger<SileroTtsProvider> logger;
    private readonly string modelDir;
    private readonly string? modelPath;
    private readonly SileroModelVariant variant;
    private bool disposed;

    public SileroTtsProvider(
        ILoggerFactory loggerFactory,
        string? modelDir = null,
        SileroModelVariant variant = SileroModelVariant.V5Russian,
        float outputGain = SileroTextToSpeech.DefaultOutputGain)
    {
        this.logger = loggerFactory.CreateLogger<SileroTtsProvider>();
        modelDir ??= SileroTextToSpeech.GetDefaultModelDir();
        this.modelDir = modelDir;
        this.modelPath = FindModelFile(modelDir);
        this.variant = variant;

        // Build voice metadata from the variant
        this.voices = BuildVoiceMetadata(variant);

        // Try to load the engine (graceful — sets IsReady = false if model missing)
        if (this.modelPath is not null)
        {
            try
            {
                this.engine = new SileroTextToSpeech(
                    loggerFactory.CreateLogger<SileroTextToSpeech>(),
                    this.modelDir,
                    this.voices[0].Name,
                    variant,
                    this.modelPath,
                    outputGain);
                var variantName = variant == SileroModelVariant.V5CisBase ? "v5-cis-base" : "v5-russian";
                this.LogProviderReady(this.modelPath, variantName, this.voices.Count);
            }
            catch (Exception ex)
            {
                this.LogProviderLoadFailed(this.modelPath, ex.Message);
                this.engine = null;
            }
        }
        else
        {
            this.LogModelNotFound(this.modelDir);
        }
    }

    /// <inheritdoc />
    public string Name => this.variant == SileroModelVariant.V5CisBase
        ? "silero-v5-cis-base"
        : "silero-v5-russian";

    /// <inheritdoc />
    public IReadOnlyList<TtsVoiceInfo> Voices => this.voices;

    /// <inheritdoc />
    public bool IsReady => this.engine is not null;

    /// <inheritdoc />
    public string StatusDetail => this.engine is not null
        ? $"{this.Name} model ready"
        : $"{this.Name} model not found at {this.modelDir}";

    /// <inheritdoc />
    public AudioFormat OutputFormat => AudioFormat.Silero;

    /// <inheritdoc />
    public bool SupportsStreaming => true;

    /// <inheritdoc />
    public bool CanDownloadModel => true;

    /// <inheritdoc />
    public string? DownloadLabel => this.variant == SileroModelVariant.V5CisBase
        ? "Download Silero v5 CIS Base / MIT (~110 MB)"
        : "Download Silero v5 Russian / CC BY-NC (~112 MB)";

    /// <inheritdoc />
    public async Task<bool> DownloadModelAsync(HttpClient httpClient, Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var url = this.variant == SileroModelVariant.V5CisBase ? DownloadUrlV5CisBase : DownloadUrlV5Russian;
        this.LogDownloadStarted(url);

        try
        {
            Directory.CreateDirectory(this.modelDir);
            var tempFile = Path.Combine(this.modelDir, "download.tmp");

            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long downloadedBytes = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                downloadedBytes += bytesRead;
                if (totalBytes > 0)
                {
                    progress?.Invoke((double)downloadedBytes / totalBytes);
                }
            }

            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            fileStream.Close();

            // If it's a tar.gz, extract it; otherwise just rename to model file
            if (url.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractTarGzAsync(tempFile, this.modelDir, cancellationToken).ConfigureAwait(false);
                File.Delete(tempFile);
            }
            else
            {
                var targetFile = Path.Combine(this.modelDir, SileroTextToSpeech.DefaultModelFileName);
                File.Move(tempFile, targetFile, overwrite: true);
            }

            this.LogDownloadComplete(this.modelDir);
            return FindModelFile(this.modelDir) is not null;
        }
        catch (Exception ex)
        {
            this.LogDownloadFailed(url, ex.Message);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> SynthesizeAsync(string text, string voiceName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        if (this.engine is null)
        {
            throw new InvalidOperationException("Silero TTS engine is not ready (model not loaded).");
        }

        this.engine.SetVoice(voiceName);
        return await this.engine.SynthesizeAsync(text, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<byte[]> SynthesizeStreamingAsync(
        string text, string voiceName, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        if (this.engine is null)
        {
            throw new InvalidOperationException("Silero TTS engine is not ready (model not loaded).");
        }

        this.engine.SetVoice(voiceName);
        await foreach (var chunk in this.engine.SynthesizeStreamingAsync(text, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.engine?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Builds voice metadata with language and gender for the given variant.
    /// V5Russian: aidar (M), baya (F), kseniya (F), xenia (F).
    /// V5CisBase: 28 voices with ru_ prefix.
    /// </summary>
    /// <summary>Languages supported by the v5_cis_base model (only those detectable by Lingua).</summary>
    private static readonly string[] cisLanguages = ["ru", "kk", "uk"];

    private static List<TtsVoiceInfo> BuildVoiceMetadata(SileroModelVariant variant)
    {
        if (variant == SileroModelVariant.V5CisBase)
        {
            var voiceNames = SileroTextToSpeech.GetVoiceNamesForVariant(SileroModelVariant.V5CisBase);
            var result = new List<TtsVoiceInfo>();

            // Each CIS voice can speak all CIS languages
            foreach (var lang in cisLanguages)
            {
                foreach (var name in voiceNames)
                {
                    var gender = GuessCisGender(name);
                    var prettyName = name.Replace("ru_", "", StringComparison.Ordinal);
                    prettyName = char.ToUpperInvariant(prettyName[0]) + prettyName[1..];
                    result.Add(new TtsVoiceInfo(name, lang, gender, $"{gender} — {prettyName} (MIT)"));
                }
            }

            return result;
        }

        // V5Russian — hardcode known genders
        return
        [
            new TtsVoiceInfo("aidar", "ru", VoiceGender.Male, "Male — Aidar (high quality, CC BY-NC)"),
            new TtsVoiceInfo("baya", "ru", VoiceGender.Female, "Female — Baya (high quality, CC BY-NC)"),
            new TtsVoiceInfo("kseniya", "ru", VoiceGender.Female, "Female — Kseniya (high quality, CC BY-NC)"),
            new TtsVoiceInfo("xenia", "ru", VoiceGender.Female, "Female — Xenia (high quality, CC BY-NC)"),
        ];
    }

    /// <summary>Guess gender for CIS base voices by name convention.</summary>
    private static VoiceGender GuessCisGender(string name)
    {
        // Known male names in the CIS base model
        var maleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ru_alexandr", "ru_bogdan", "ru_dmitriy", "ru_eduard",
            "ru_gamat", "ru_igor", "ru_kejilgan", "ru_marat",
            "ru_onaoy", "ru_roman", "ru_safarhuja", "ru_sibday",
        };

        return maleNames.Contains(name) ? VoiceGender.Male : VoiceGender.Female;
    }

    /// <summary>
    /// Finds the TorchScript model file in the given directory.
    /// Handles variant-specific filenames (silero_v5_jit.pt, silero_v5_cis_jit.pt, etc.).
    /// Returns the full path if found, or null.
    /// </summary>
    private static string? FindModelFile(string modelDir)
    {
        if (!Directory.Exists(modelDir))
        {
            return null;
        }

        // Check for any .pt file matching the silero pattern
        var ptFiles = Directory.GetFiles(modelDir, "silero_*.pt");
        return ptFiles.Length > 0 ? ptFiles[0] : null;
    }

    /// <summary>Extracts a .tar.gz archive to the target directory using System.IO.Compression.</summary>
    private static async Task ExtractTarGzAsync(string archivePath, string targetDir, CancellationToken cancellationToken)
    {
        await using var fileStream = File.OpenRead(archivePath);
        await using var gzStream = new System.IO.Compression.GZipStream(fileStream, System.IO.Compression.CompressionMode.Decompress);
        await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(gzStream, targetDir, overwriteFiles: true, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Silero TTS provider ready: {ModelPath}, variant={Variant}, {VoiceCount} voices")]
    private partial void LogProviderReady(string modelPath, string variant, int voiceCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Silero TTS provider failed to load from {ModelPath}: {Error}")]
    private partial void LogProviderLoadFailed(string modelPath, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Silero model not found at {ModelPath}")]
    private partial void LogModelNotFound(string modelPath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Silero model download started from {Url}")]
    private partial void LogDownloadStarted(string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Silero model download complete, extracted to {TargetDir}")]
    private partial void LogDownloadComplete(string targetDir);

    [LoggerMessage(Level = LogLevel.Error, Message = "Silero model download failed from {Url}: {Error}")]
    private partial void LogDownloadFailed(string url, string error);
}
