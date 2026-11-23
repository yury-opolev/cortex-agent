using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation;

public sealed class EvalProtectionTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), $"eval-protection-{Guid.NewGuid():N}");

    public EvalProtectionTests() => Directory.CreateDirectory(this.dir);

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void WriteText_Encrypted_SealsTheFileAndReadsItBack()
    {
        var protection = EvalProtection.Encrypted(EvalTestKeys.Ring());
        var path = Path.Combine(this.dir, "dataset.jsonl");

        protection.WriteLines(path, ["{\"id\":\"e1\",\"text\":\"zqsecret\"}", string.Empty]);

        Assert.True(EvalProtection.IsSealed(path));
        Assert.DoesNotContain("zqsecret", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(["{\"id\":\"e1\",\"text\":\"zqsecret\"}"], protection.ReadLines(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void ReadText_EncryptedModeAndPlaintextFile_RefusedFailClosed()
    {
        var path = Path.Combine(this.dir, "dataset.jsonl");
        File.WriteAllText(path, "{\"id\":\"e1\"}");

        var error = Assert.Throws<InvalidOperationException>(() => EvalProtection.Encrypted(EvalTestKeys.Ring()).ReadText(path));

        Assert.Contains("plaintext on an encrypted install", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadText_PlaintextModeAndSealedFile_Refused()
    {
        var path = Path.Combine(this.dir, "dataset.jsonl");
        EvalProtection.Encrypted(EvalTestKeys.Ring()).WriteText(path, "x");

        Assert.Throws<InvalidOperationException>(() => EvalProtection.Plaintext.ReadText(path));
    }

    [Fact]
    public void ReadText_SealedFileRenamed_FailsAuthentication()
    {
        var protection = EvalProtection.Encrypted(EvalTestKeys.Ring());
        var seeds = Path.Combine(this.dir, "seeds.jsonl");
        protection.WriteText(seeds, "zqsecret");
        var dataset = Path.Combine(this.dir, "dataset.jsonl");
        File.Move(seeds, dataset);

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => protection.ReadText(dataset));
    }

    [Fact]
    public void Passwords_CopiesUseTheEvaluationRing_FrozenSourceReadsWithIt()
    {
        var live = EvalTestKeys.Ring();
        var protection = EvalProtection.Encrypted(live);

        Assert.Equal(live.PasswordFor(MessageStore.PasswordPurpose), protection.SourcePassword(MessageStore.PasswordPurpose));
        Assert.Equal(live.Derive(EvalProtection.Purpose).PasswordFor(MessageStore.PasswordPurpose), protection.CopyPassword(MessageStore.PasswordPurpose));
        Assert.Equal(protection.CopyPassword(MessageStore.PasswordPurpose), protection.ForFrozenSource().SourcePassword(MessageStore.PasswordPurpose));
        Assert.Null(EvalProtection.Plaintext.SourcePassword(MessageStore.PasswordPurpose));
        Assert.Null(EvalProtection.Plaintext.CopyMessageEncryption);
    }
}
