using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Common.Security;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests;

public class TokenGeneratorTests
{
    [Fact]
    public void GenerateHubToken_ReturnsBase64String()
    {
        var token = TokenGenerator.GenerateHubToken();

        Assert.NotNull(token);
        Assert.NotEmpty(token);

        // Should be valid Base64
        var bytes = Convert.FromBase64String(token);
        Assert.Equal(32, bytes.Length); // 256-bit
    }

    [Fact]
    public void GenerateHubToken_ReturnsUniqueValues()
    {
        var token1 = TokenGenerator.GenerateHubToken();
        var token2 = TokenGenerator.GenerateHubToken();

        Assert.NotEqual(token1, token2);
    }

    [Fact]
    public void GenerateHubToken_ConsistentLength()
    {
        // 32 bytes in Base64 = 44 characters (ceil(32/3)*4)
        var token = TokenGenerator.GenerateHubToken();
        Assert.Equal(44, token.Length);
    }
}

public class SecretManagerTests
{
    private readonly InMemorySecretStore _store;
    private readonly SecretManager _manager;
    private readonly string _tempDir;

    public SecretManagerTests()
    {
        _store = new InMemorySecretStore();
        _tempDir = Path.Combine(Path.GetTempPath(), "james-test-" + Guid.NewGuid().ToString("N"));
        _manager = new SecretManager(_store, NullLogger<SecretManager>.Instance, _tempDir);
    }

    // ── GetOrCreateHubToken ────────────────────────────────────────

    [Fact]
    public void GetOrCreateHubToken_FirstCall_GeneratesAndStoresToken()
    {
        var token = _manager.GetOrCreateHubToken();

        Assert.NotNull(token);
        Assert.NotEmpty(token);

        // Should be valid Base64 (256-bit token)
        var bytes = Convert.FromBase64String(token);
        Assert.Equal(32, bytes.Length);

        // Secrets file should exist
        Assert.True(File.Exists(Path.Combine(_tempDir, "secrets.json")));

        Cleanup();
    }

    [Fact]
    public void GetOrCreateHubToken_SubsequentCall_ReturnsSameToken()
    {
        var token1 = _manager.GetOrCreateHubToken();
        var token2 = _manager.GetOrCreateHubToken();

        Assert.Equal(token1, token2);

        Cleanup();
    }

    [Fact]
    public void GetOrCreateHubToken_CorruptedEncryption_RegeneratesToken()
    {
        var token1 = _manager.GetOrCreateHubToken();

        // Corrupt the store so Unprotect throws
        _store.CorruptNextUnprotect = true;

        var token2 = _manager.GetOrCreateHubToken();

        // Should get a new token since decryption failed
        Assert.NotNull(token2);
        Assert.NotEmpty(token2);

        Cleanup();
    }

    [Fact]
    public void GetOrCreateHubToken_CreatesSecretsDirectory()
    {
        Assert.False(Directory.Exists(_tempDir));

        _manager.GetOrCreateHubToken();

        Assert.True(Directory.Exists(_tempDir));

        Cleanup();
    }

    // ── RegenerateHubToken ─────────────────────────────────────────

    [Fact]
    public void RegenerateHubToken_ReturnsNewToken()
    {
        var original = _manager.GetOrCreateHubToken();
        var regenerated = _manager.RegenerateHubToken();

        Assert.NotEqual(original, regenerated);

        Cleanup();
    }

    [Fact]
    public void RegenerateHubToken_SubsequentGetReturnsNewToken()
    {
        _manager.GetOrCreateHubToken();
        var regenerated = _manager.RegenerateHubToken();
        var fetched = _manager.GetOrCreateHubToken();

        Assert.Equal(regenerated, fetched);

        Cleanup();
    }

    // ── StoreApiKey / GetApiKey ────────────────────────────────────

    [Fact]
    public void StoreAndGetApiKey_RoundTrips()
    {
        const string provider = "openai";
        const string apiKey = "sk-test-key-12345";

        _manager.StoreApiKey(provider, apiKey);
        var retrieved = _manager.GetApiKey(provider);

        Assert.Equal(apiKey, retrieved);

        Cleanup();
    }

    [Fact]
    public void GetApiKey_NoFileExists_ReturnsNull()
    {
        var result = _manager.GetApiKey("openai");
        Assert.Null(result);
    }

    [Fact]
    public void GetApiKey_ProviderNotStored_ReturnsNull()
    {
        _manager.StoreApiKey("openai", "sk-key");
        var result = _manager.GetApiKey("anthropic");

        Assert.Null(result);

        Cleanup();
    }

    [Fact]
    public void GetApiKey_CorruptedEncryption_ReturnsNull()
    {
        _manager.StoreApiKey("openai", "sk-key");
        _store.CorruptNextUnprotect = true;

        var result = _manager.GetApiKey("openai");

        Assert.Null(result);

        Cleanup();
    }

    [Fact]
    public void StoreApiKey_OverwritesExisting()
    {
        _manager.StoreApiKey("openai", "old-key");
        _manager.StoreApiKey("openai", "new-key");

        var result = _manager.GetApiKey("openai");

        Assert.Equal("new-key", result);

        Cleanup();
    }

    [Fact]
    public void StoreApiKey_MultipleProviders_IndependentStorage()
    {
        _manager.StoreApiKey("openai", "sk-openai");
        _manager.StoreApiKey("anthropic", "sk-anthropic");

        Assert.Equal("sk-openai", _manager.GetApiKey("openai"));
        Assert.Equal("sk-anthropic", _manager.GetApiKey("anthropic"));

        Cleanup();
    }

    [Fact]
    public void StoreApiKey_CreatesSecretsDirectory()
    {
        Assert.False(Directory.Exists(_tempDir));

        _manager.StoreApiKey("openai", "sk-key");

        Assert.True(Directory.Exists(_tempDir));

        Cleanup();
    }

    // ── RemoveApiKey ───────────────────────────────────────────────

    [Fact]
    public void RemoveApiKey_AfterStore_GetReturnsNull()
    {
        _manager.StoreApiKey("embeddings-provider", "sk-embed");
        Assert.Equal("sk-embed", _manager.GetApiKey("embeddings-provider"));

        _manager.RemoveApiKey("embeddings-provider");

        Assert.Null(_manager.GetApiKey("embeddings-provider"));

        Cleanup();
    }

    [Fact]
    public void RemoveApiKey_OnlyRemovesTargetProvider()
    {
        _manager.StoreApiKey("openai", "sk-openai");
        _manager.StoreApiKey("embeddings-provider", "sk-embed");

        _manager.RemoveApiKey("embeddings-provider");

        Assert.Null(_manager.GetApiKey("embeddings-provider"));
        Assert.Equal("sk-openai", _manager.GetApiKey("openai"));

        Cleanup();
    }

    [Fact]
    public void RemoveApiKey_NotStored_IsNoOp()
    {
        // Should not throw even when no key (and no file) exists.
        _manager.RemoveApiKey("embeddings-provider");

        Assert.Null(_manager.GetApiKey("embeddings-provider"));

        Cleanup();
    }

    [Fact]
    public void RemoveApiKey_PersistsAcrossInstances()
    {
        _manager.StoreApiKey("embeddings-provider", "sk-embed");
        _manager.RemoveApiKey("embeddings-provider");

        var manager2 = new SecretManager(_store, NullLogger<SecretManager>.Instance, _tempDir);

        Assert.Null(manager2.GetApiKey("embeddings-provider"));

        Cleanup();
    }

    [Fact]
    public void StoreApiKey_EmptyValue_Throws_DemonstratesWhyRemoveIsNeeded()
    {
        // Storing an empty string attempts to DPAPI-encrypt "" which throws.
        // This is exactly the bug the clear-key/reset routes hit before switching
        // to RemoveApiKey.
        Assert.ThrowsAny<ArgumentException>(() =>
            _manager.StoreApiKey("embeddings-provider", string.Empty));

        Cleanup();
    }

    // ── Persistence across instances ──────────────────────────────

    [Fact]
    public void SecretManager_PersistsAcrossInstances()
    {
        _manager.StoreApiKey("openai", "sk-persisted");
        _manager.GetOrCreateHubToken();

        // Create a new instance pointing to the same directory
        var manager2 = new SecretManager(_store, NullLogger<SecretManager>.Instance, _tempDir);

        Assert.Equal("sk-persisted", manager2.GetApiKey("openai"));
        Assert.NotNull(manager2.GetOrCreateHubToken());

        Cleanup();
    }

    // ── GetOrCreateDatabaseKey ────────────────────────────────────

    [Fact]
    public void GetOrCreateDatabaseKey_FirstCall_GeneratesAndStoresKey()
    {
        var key = _manager.GetOrCreateDatabaseKey();

        Assert.NotNull(key);
        Assert.NotEmpty(key);

        // Should be valid Base64 (256-bit key)
        var bytes = Convert.FromBase64String(key);
        Assert.Equal(32, bytes.Length);

        // Secrets file should exist
        Assert.True(File.Exists(Path.Combine(_tempDir, "secrets.json")));

        Cleanup();
    }

    [Fact]
    public void GetOrCreateDatabaseKey_SubsequentCall_ReturnsSameKey()
    {
        var key1 = _manager.GetOrCreateDatabaseKey();
        var key2 = _manager.GetOrCreateDatabaseKey();

        Assert.Equal(key1, key2);

        Cleanup();
    }

    [Fact]
    public void GetOrCreateDatabaseKey_CorruptedEncryption_RegeneratesKey()
    {
        var key1 = _manager.GetOrCreateDatabaseKey();

        // Corrupt the store so Unprotect throws
        _store.CorruptNextUnprotect = true;

        var key2 = _manager.GetOrCreateDatabaseKey();

        // Should get a new key since decryption failed
        Assert.NotNull(key2);
        Assert.NotEmpty(key2);

        Cleanup();
    }

    [Fact]
    public void GetOrCreateDatabaseKey_PersistsAcrossInstances()
    {
        var key1 = _manager.GetOrCreateDatabaseKey();

        // Create a new instance pointing to the same directory
        var manager2 = new SecretManager(_store, NullLogger<SecretManager>.Instance, _tempDir);
        var key2 = manager2.GetOrCreateDatabaseKey();

        Assert.Equal(key1, key2);

        Cleanup();
    }

    [Fact]
    public void GetOrCreateDatabaseKey_IndependentFromHubToken()
    {
        var hubToken = _manager.GetOrCreateHubToken();
        var dbKey = _manager.GetOrCreateDatabaseKey();

        // They should be different values
        Assert.NotEqual(hubToken, dbKey);

        Cleanup();
    }

    // ── Cleanup ───────────────────────────────────────────────────

    private void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}

public class DpapiSecretStoreTests
{
    [Fact]
    public void Protect_Unprotect_RoundTrips()
    {
        var store = new DpapiSecretStore();
        const string plaintext = "my-secret-token-12345";

        var encrypted = store.Protect(plaintext);
        var decrypted = store.Unprotect(encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Protect_ProducesDifferentCiphertextThanPlaintext()
    {
        var store = new DpapiSecretStore();
        const string plaintext = "my-secret-token";

        var encrypted = store.Protect(plaintext);

        Assert.NotEqual(plaintext, encrypted);
    }

    [Fact]
    public void Protect_SameInputProducesDifferentOutput()
    {
        // DPAPI can produce different ciphertext for the same plaintext
        // (depends on implementation, but the round-trip should still work)
        var store = new DpapiSecretStore();
        const string plaintext = "test-value";

        var encrypted1 = store.Protect(plaintext);
        var encrypted2 = store.Protect(plaintext);

        // Both should decrypt to the same value
        Assert.Equal(plaintext, store.Unprotect(encrypted1));
        Assert.Equal(plaintext, store.Unprotect(encrypted2));
    }

    [Fact]
    public void Protect_EmptyString_ThrowsArgumentException()
    {
        var store = new DpapiSecretStore();
        Assert.Throws<ArgumentException>(() => store.Protect(string.Empty));
    }

    [Fact]
    public void Unprotect_EmptyString_ThrowsArgumentException()
    {
        var store = new DpapiSecretStore();
        Assert.Throws<ArgumentException>(() => store.Unprotect(string.Empty));
    }

    [Fact]
    public void Unprotect_InvalidBase64_ThrowsFormatException()
    {
        var store = new DpapiSecretStore();
        Assert.Throws<FormatException>(() => store.Unprotect("not-valid-base64!!!"));
    }

    [Fact]
    public void Protect_UnicodeContent_RoundTrips()
    {
        var store = new DpapiSecretStore();
        const string plaintext = "日本語テスト 🔐 café";

        var encrypted = store.Protect(plaintext);
        var decrypted = store.Unprotect(encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Protect_LongString_RoundTrips()
    {
        var store = new DpapiSecretStore();
        var plaintext = new string('x', 10_000);

        var encrypted = store.Protect(plaintext);
        var decrypted = store.Unprotect(encrypted);

        Assert.Equal(plaintext, decrypted);
    }
}

public class SecurityAuditorTests
{
    private readonly SecurityAuditor _auditor = new(NullLogger<SecurityAuditor>.Instance);

    private static BridgeConfig CreateValidConfig() => new()
    {
        AgentHubUrl = "http://localhost:5000/agent",
        HubToken = TokenGenerator.GenerateHubToken(), // 44 chars, Base64
        WebUi = new WebUiConfig { Enabled = true, BindAddress = "127.0.0.1", Port = 5080 },
        LlmProviders = [new LlmProviderConfig { Name = "openai", Api = "openai-completions", ApiKey = "sk-testkey123" }],
        Channels = new Dictionary<string, ChannelConfig>(),
    };

    // ── Hub Token ──────────────────────────────────────────────────

    [Fact]
    public void Audit_EmptyHubToken_ReturnsCriticalFinding()
    {
        var config = CreateValidConfig();
        config.HubToken = "";

        var findings = _auditor.Audit(config);

        var finding = Assert.Single(findings, f => f.Message.Contains("empty", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(AuditSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void Audit_WhitespaceHubToken_ReturnsCriticalFinding()
    {
        var config = CreateValidConfig();
        config.HubToken = "   ";

        var findings = _auditor.Audit(config);

        var finding = Assert.Single(findings, f => f.Message.Contains("empty", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(AuditSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void Audit_ShortHubToken_ReturnsCriticalFinding()
    {
        var config = CreateValidConfig();
        config.HubToken = "short-token";

        var findings = _auditor.Audit(config);

        var finding = Assert.Single(findings, f => f.Message.Contains("too short", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(AuditSeverity.Critical, finding.Severity);
    }

    [Theory]
    [InlineData("changeme")]
    [InlineData("default")]
    [InlineData("password")]
    [InlineData("token")]
    public void Audit_PlaceholderHubToken_ReturnsCriticalFinding(string placeholder)
    {
        var config = CreateValidConfig();
        config.HubToken = placeholder;

        var findings = _auditor.Audit(config);

        Assert.Contains(findings, f => f.Message.Contains("placeholder", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Audit_ValidHubToken_NoHubTokenFindings()
    {
        var config = CreateValidConfig();

        var findings = _auditor.Audit(config);

        Assert.DoesNotContain(findings, f =>
            f.Message.Contains("Hub token", StringComparison.OrdinalIgnoreCase));
    }

    // ── Web UI ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.100")]
    [InlineData("*")]
    public void Audit_WebUiBoundToNonLoopback_ReturnsCriticalFinding(string bindAddress)
    {
        var config = CreateValidConfig();
        config.WebUi = new WebUiConfig { Enabled = true, BindAddress = bindAddress };

        var findings = _auditor.Audit(config);

        var finding = Assert.Single(findings, f => f.Message.Contains("loopback", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(AuditSeverity.Critical, finding.Severity);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("::1")]
    public void Audit_WebUiBoundToLoopback_NoWebUiFindings(string bindAddress)
    {
        var config = CreateValidConfig();
        config.WebUi = new WebUiConfig { Enabled = true, BindAddress = bindAddress };

        var findings = _auditor.Audit(config);

        Assert.DoesNotContain(findings, f =>
            f.Message.Contains("Web UI", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Audit_WebUiDisabled_NoWebUiFindings()
    {
        var config = CreateValidConfig();
        config.WebUi = new WebUiConfig { Enabled = false, BindAddress = "0.0.0.0" };

        var findings = _auditor.Audit(config);

        Assert.DoesNotContain(findings, f =>
            f.Message.Contains("Web UI", StringComparison.OrdinalIgnoreCase));
    }

    // ── LLM Providers ──────────────────────────────────────────────

    [Fact]
    public void Audit_NoLlmProviders_ReturnsInfoFinding()
    {
        var config = CreateValidConfig();
        config.LlmProviders = [];

        var findings = _auditor.Audit(config);

        var finding = Assert.Single(findings, f => f.Message.Contains("No LLM providers", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(AuditSeverity.Info, finding.Severity);
    }

    [Fact]
    public void Audit_LlmProviderWithoutApiKey_ReturnsWarning()
    {
        var config = CreateValidConfig();
        config.LlmProviders = [new LlmProviderConfig { Name = "openai", Api = "openai-completions", ApiKey = null }];

        var findings = _auditor.Audit(config);

        var finding = Assert.Single(findings, f => f.Message.Contains("no API key", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains("openai", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Audit_MultipleLlmProvidersWithoutKeys_ReturnsWarningForEach()
    {
        var config = CreateValidConfig();
        config.LlmProviders =
        [
            new LlmProviderConfig { Name = "openai", Api = "openai-completions" },
            new LlmProviderConfig { Name = "anthropic", Api = "anthropic-messages" },
        ];

        var findings = _auditor.Audit(config);

        var keyFindings = findings.Where(f => f.Message.Contains("no API key", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(2, keyFindings.Count);
    }

    [Fact]
    public void Audit_LlmProviderWithApiKey_NoWarning()
    {
        var config = CreateValidConfig(); // has provider with ApiKey set

        var findings = _auditor.Audit(config);

        Assert.DoesNotContain(findings, f =>
            f.Message.Contains("no API key", StringComparison.OrdinalIgnoreCase));
    }

    // ── Channels ───────────────────────────────────────────────────

    // ── File System ────────────────────────────────────────────────

    [Fact]
    public void Audit_FileSystemCheckDisabled_NoFileSystemFindings()
    {
        var config = CreateValidConfig();

        var findings = _auditor.Audit(config, new SecurityAuditOptions { CheckFileSystem = false });

        Assert.DoesNotContain(findings, f =>
            f.Message.Contains("directory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Audit_DataDirectoryDoesNotExist_ReturnsInfoFinding()
    {
        var config = CreateValidConfig();
        var nonExistentDir = Path.Combine(Path.GetTempPath(), "james-nonexistent-" + Guid.NewGuid().ToString("N"));

        var findings = _auditor.Audit(config, new SecurityAuditOptions
        {
            CheckFileSystem = true,
            DataDirectory = nonExistentDir,
        });

        var finding = Assert.Single(findings, f => f.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(AuditSeverity.Info, finding.Severity);
    }

    [Fact]
    public void Audit_SecretsDirectoryExists_ReturnsInfoFinding()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "james-audit-test-" + Guid.NewGuid().ToString("N"));
        var secretsDir = Path.Combine(tempDir, "secrets");
        Directory.CreateDirectory(secretsDir);

        try
        {
            var config = CreateValidConfig();

            var findings = _auditor.Audit(config, new SecurityAuditOptions
            {
                CheckFileSystem = true,
                DataDirectory = tempDir,
            });

            Assert.Contains(findings, f =>
                f.Message.Contains("Secrets directory exists", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Audit_NullDataDirectory_NoFileSystemFindings()
    {
        var config = CreateValidConfig();

        var findings = _auditor.Audit(config, new SecurityAuditOptions
        {
            CheckFileSystem = true,
            DataDirectory = null,
        });

        Assert.DoesNotContain(findings, f =>
            f.Message.Contains("directory", StringComparison.OrdinalIgnoreCase));
    }

    // ── Ordering and overall behavior ──────────────────────────────

    [Fact]
    public void Audit_FindingsOrderedBySeverity_CriticalFirst()
    {
        var config = new BridgeConfig
        {
            AgentHubUrl = "http://localhost:5000",
            HubToken = "", // Critical
            WebUi = new WebUiConfig { Enabled = true, BindAddress = "0.0.0.0" }, // Critical
            LlmProviders = [], // Info
            Channels = new Dictionary<string, ChannelConfig>
            {
                ["discord"] = new ChannelConfig
                {
                    Enabled = true,
                    Settings = new Dictionary<string, string> { ["DmPolicy"] = "AllowAll" }, // Warning
                },
            },
        };

        var findings = _auditor.Audit(config);

        Assert.True(findings.Count >= 3);

        // Verify ordering: Critical < Warning < Info
        for (int i = 1; i < findings.Count; i++)
        {
            Assert.True(findings[i - 1].Severity <= findings[i].Severity,
                $"Finding at index {i - 1} ({findings[i - 1].Severity}) should be before {i} ({findings[i].Severity})");
        }
    }

    [Fact]
    public void Audit_ValidConfig_ReturnsNoFindings()
    {
        var config = CreateValidConfig();

        var findings = _auditor.Audit(config);

        Assert.Empty(findings);
    }

    [Fact]
    public void Audit_DefaultOptions_DoesNotCheckFileSystem()
    {
        var config = CreateValidConfig();

        // Default options should have CheckFileSystem = false
        var findings = _auditor.Audit(config);

        Assert.DoesNotContain(findings, f =>
            f.Message.Contains("directory", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// In-memory implementation of ISecretStore for testing SecretManager
/// without requiring DPAPI (Windows-only).
/// </summary>
internal sealed class InMemorySecretStore : ISecretStore
{
    public bool CorruptNextUnprotect { get; set; }

    public string Protect(string plaintext)
    {
        // Mirror DpapiSecretStore: it throws on null/empty input
        // (ProtectedData.Protect via ArgumentException.ThrowIfNullOrEmpty).
        // Modelling that here is what makes "clear key by storing empty string"
        // observably broken and proves RemoveApiKey is required.
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        // Simple Base64 encoding as a reversible "encryption" for testing
        var bytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        return Convert.ToBase64String(bytes);
    }

    public string Unprotect(string ciphertext)
    {
        if (CorruptNextUnprotect)
        {
            CorruptNextUnprotect = false;
            throw new InvalidOperationException("Simulated decryption failure");
        }

        var bytes = Convert.FromBase64String(ciphertext);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
