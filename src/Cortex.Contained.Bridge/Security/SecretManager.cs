using System.Text.Json;
using Cortex.Contained.Common.Security;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Security;

/// <summary>
/// Manages encrypted secrets stored on disk. On first run, auto-generates
/// a hub token and stores it encrypted via DPAPI.
/// </summary>
public sealed partial class SecretManager
{
    private const string SecretsFileName = "secrets.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ISecretStore store;
    private readonly ILogger<SecretManager> logger;
    private readonly string secretsDir;

    public SecretManager(ISecretStore store, ILogger<SecretManager> logger, string? secretsDir = null)
    {
        this.store = store;
        this.logger = logger;
        this.secretsDir = secretsDir
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cortex", "secrets");
    }

    /// <summary>
    /// Loads the hub token. If none exists, generates one, encrypts it, and saves to disk.
    /// Returns the plaintext token.
    /// </summary>
    public string GetOrCreateHubToken()
    {
        EnsureSecretsDirectory();

        var filePath = Path.Combine(this.secretsDir, SecretsFileName);
        var secrets = LoadSecrets(filePath);

        if (secrets.HubToken is not null)
        {
            try
            {
                var token = this.store.Unprotect(secrets.HubToken);
                this.LogHubTokenLoaded();
                return token;
            }
            catch (Exception ex)
            {
                this.LogHubTokenDecryptionFailed(ex.Message);
                // Fall through to regenerate
            }
        }

        // Generate new token
        var newToken = TokenGenerator.GenerateHubToken();
        secrets.HubToken = this.store.Protect(newToken);
        SaveSecrets(filePath, secrets);
        this.LogHubTokenGenerated();

        return newToken;
    }

    /// <summary>
    /// Stores an API key encrypted via DPAPI, keyed by provider name.
    /// </summary>
    public void StoreApiKey(string providerName, string apiKey)
    {
        EnsureSecretsDirectory();

        var filePath = Path.Combine(this.secretsDir, SecretsFileName);
        var secrets = LoadSecrets(filePath);

        secrets.ApiKeys ??= new Dictionary<string, string>();
        secrets.ApiKeys[providerName] = this.store.Protect(apiKey);
        SaveSecrets(filePath, secrets);

        this.LogApiKeyStored(providerName);
    }

    /// <summary>
    /// Removes a stored API key entry for the given provider name and saves.
    /// Unlike <see cref="StoreApiKey"/> with an empty value (which would attempt to
    /// encrypt an empty string — and throw on DPAPI), this reliably deletes the entry
    /// so that <see cref="GetApiKey"/> subsequently returns <c>null</c>.
    /// No-op (other than ensuring the directory exists) when no such key is stored.
    /// </summary>
    public void RemoveApiKey(string providerName)
    {
        EnsureSecretsDirectory();

        var filePath = Path.Combine(this.secretsDir, SecretsFileName);
        var secrets = LoadSecrets(filePath);

        if (secrets.ApiKeys?.Remove(providerName) == true)
        {
            SaveSecrets(filePath, secrets);
            this.LogApiKeyRemoved(providerName);
        }
    }

    /// <summary>
    /// Retrieves a decrypted API key for the given provider name.
    /// </summary>
    public string? GetApiKey(string providerName)
    {
        var filePath = Path.Combine(this.secretsDir, SecretsFileName);
        if (!File.Exists(filePath))
        {
            return null;
        }

        var secrets = LoadSecrets(filePath);

        if (secrets.ApiKeys is null || !secrets.ApiKeys.TryGetValue(providerName, out var encrypted))
        {
            return null;
        }

        try
        {
            return this.store.Unprotect(encrypted);
        }
        catch (Exception ex)
        {
            this.LogApiKeyDecryptionFailed(providerName, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Stores an OAuth access token, refresh token, and expiry (Unix ms) for an Anthropic provider.
    /// Access token is stored under the provider name key; refresh token under "{name}_refresh".
    /// </summary>
    public void StoreOAuthTokens(string providerName, string accessToken, string refreshToken, long expiresAtMs)
    {
        EnsureSecretsDirectory();

        var filePath = Path.Combine(this.secretsDir, SecretsFileName);
        var secrets = LoadSecrets(filePath);

        secrets.ApiKeys ??= new Dictionary<string, string>();
        secrets.ApiKeys[providerName] = this.store.Protect(accessToken);

        secrets.RefreshTokens ??= new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(refreshToken))
        {
            secrets.RefreshTokens[providerName] = this.store.Protect(refreshToken);
        }
        else
        {
            secrets.RefreshTokens.Remove(providerName);
        }

        secrets.TokenExpiries ??= new Dictionary<string, long>();
        secrets.TokenExpiries[providerName] = expiresAtMs;

        SaveSecrets(filePath, secrets);
        this.LogOAuthTokensStored(providerName);
    }

    /// <summary>
    /// Retrieves the decrypted OAuth refresh token for the given provider, or null if not stored.
    /// </summary>
    public string? GetRefreshToken(string providerName)
    {
        var filePath = Path.Combine(this.secretsDir, SecretsFileName);
        if (!File.Exists(filePath))
        {
            return null;
        }

        var secrets = LoadSecrets(filePath);
        if (secrets.RefreshTokens is null || !secrets.RefreshTokens.TryGetValue(providerName, out var encrypted))
        {
            return null;
        }

        try
        {
            return this.store.Unprotect(encrypted);
        }
        catch (Exception ex)
        {
            this.LogApiKeyDecryptionFailed(providerName + "_refresh", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Retrieves the stored token expiry (Unix ms) for the given provider. Returns 0 if not stored.
    /// </summary>
    public long GetTokenExpiry(string providerName)
    {
        var filePath = Path.Combine(this.secretsDir, SecretsFileName);
        if (!File.Exists(filePath))
        {
            return 0;
        }

        var secrets = LoadSecrets(filePath);
        if (secrets.TokenExpiries is null || !secrets.TokenExpiries.TryGetValue(providerName, out var expiry))
        {
            return 0;
        }

        return expiry;
    }

    /// <summary>
    /// Loads the database encryption key. If none exists, generates a 256-bit random key,
    /// encrypts it via DPAPI, and saves to disk. Returns the plaintext key (Base64-encoded).
    /// </summary>
    public string GetOrCreateDatabaseKey()
    {
        EnsureSecretsDirectory();

        var filePath = Path.Combine(this.secretsDir, SecretsFileName);
        var secrets = LoadSecrets(filePath);

        if (secrets.DatabaseKey is not null)
        {
            try
            {
                var key = this.store.Unprotect(secrets.DatabaseKey);
                this.LogDatabaseKeyLoaded();
                return key;
            }
            catch (Exception ex)
            {
                this.LogDatabaseKeyDecryptionFailed(ex.Message);
                // Fall through to regenerate
            }
        }

        // Generate new 256-bit key
        var newKey = TokenGenerator.GenerateHubToken(); // Reuses existing 256-bit generator
        secrets.DatabaseKey = this.store.Protect(newKey);
        SaveSecrets(filePath, secrets);
        this.LogDatabaseKeyGenerated();

        return newKey;
    }

    /// <summary>
    /// Regenerates the hub token, encrypts and saves the new one. Returns the plaintext.
    /// </summary>
    public string RegenerateHubToken()
    {
        EnsureSecretsDirectory();

        var filePath = Path.Combine(this.secretsDir, SecretsFileName);
        var secrets = LoadSecrets(filePath);

        var newToken = TokenGenerator.GenerateHubToken();
        secrets.HubToken = this.store.Protect(newToken);
        SaveSecrets(filePath, secrets);
        this.LogHubTokenRegenerated();

        return newToken;
    }

    /// <summary>
    /// Retrieves the stored bcrypt password hash, or null if no password has been set.
    /// </summary>
    public string? GetPasswordHash()
    {
        var filePath = Path.Combine(this.secretsDir, SecretsFileName);
        if (!File.Exists(filePath))
        {
            return null;
        }

        var secrets = LoadSecrets(filePath);
        if (secrets.PasswordHash is null)
        {
            return null;
        }

        try
        {
            return this.store.Unprotect(secrets.PasswordHash);
        }
        catch (Exception ex)
        {
            this.LogPasswordHashDecryptionFailed(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Stores a bcrypt password hash encrypted via DPAPI.
    /// </summary>
    public void SetPasswordHash(string bcryptHash)
    {
        EnsureSecretsDirectory();

        var filePath = Path.Combine(this.secretsDir, SecretsFileName);
        var secrets = LoadSecrets(filePath);

        secrets.PasswordHash = this.store.Protect(bcryptHash);
        SaveSecrets(filePath, secrets);

        this.LogPasswordHashStored();
    }

    /// <summary>
    /// Retrieves the decrypted Discord bot token, or null if not stored.
    /// </summary>
    public string? GetDiscordBotToken()
    {
        var filePath = Path.Combine(this.secretsDir, SecretsFileName);
        if (!File.Exists(filePath))
        {
            return null;
        }

        var secrets = LoadSecrets(filePath);
        if (secrets.DiscordBotToken is null)
        {
            return null;
        }

        try
        {
            return this.store.Unprotect(secrets.DiscordBotToken);
        }
        catch (Exception ex)
        {
            this.LogDiscordBotTokenDecryptionFailed(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Stores a Discord bot token encrypted via DPAPI.
    /// </summary>
    public void SetDiscordBotToken(string botToken)
    {
        EnsureSecretsDirectory();

        var filePath = Path.Combine(this.secretsDir, SecretsFileName);
        var secrets = LoadSecrets(filePath);

        secrets.DiscordBotToken = this.store.Protect(botToken);
        SaveSecrets(filePath, secrets);

        this.LogDiscordBotTokenStored();
    }

    // ── Tenant API Keys ────────────────────────────────────────────

    /// <summary>
    /// Generates, encrypts, and stores a new API key for a tenant.
    /// Returns the plaintext key.
    /// </summary>
    public string GenerateTenantApiKey(string tenantId)
    {
        var key = $"crtx-{tenantId}-{TokenGenerator.GenerateHubToken()[..32]}";
        StoreApiKey($"tenant-apikey-{tenantId}", key);
        return key;
    }

    /// <summary>Retrieves the decrypted API key for a tenant, or null.</summary>
    public string? GetTenantApiKey(string tenantId)
        => GetApiKey($"tenant-apikey-{tenantId}");

    /// <summary>Revokes a tenant's API key by removing it from encrypted storage.</summary>
    public void RevokeTenantApiKey(string tenantId)
    {
        EnsureSecretsDirectory();
        var filePath = Path.Combine(this.secretsDir, SecretsFileName);
        var secrets = LoadSecrets(filePath);

        var keyName = $"tenant-apikey-{tenantId}";
        if (secrets.ApiKeys?.Remove(keyName) == true)
        {
            SaveSecrets(filePath, secrets);
        }
    }

    /// <summary>
    /// Resolves a tenant ID from an API key. Returns null if no tenant matches.
    /// This is O(n) in the number of tenants but is only called on API channel requests.
    /// </summary>
    public string? ResolveTenantByApiKey(string apiKey, IEnumerable<string> tenantIds)
    {
        foreach (var tenantId in tenantIds)
        {
            var stored = GetTenantApiKey(tenantId);
            if (stored is not null && string.Equals(stored, apiKey, StringComparison.Ordinal))
            {
                return tenantId;
            }
        }
        return null;
    }

    private void EnsureSecretsDirectory()
    {
        if (!Directory.Exists(this.secretsDir))
        {
            Directory.CreateDirectory(this.secretsDir);
            this.LogSecretsDirectoryCreated(this.secretsDir);
        }
    }

    private static EncryptedSecrets LoadSecrets(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new EncryptedSecrets();
        }

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<EncryptedSecrets>(json) ?? new EncryptedSecrets();
    }

    private static void SaveSecrets(string filePath, EncryptedSecrets secrets)
    {
        var json = JsonSerializer.Serialize(secrets, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    // ── LoggerMessage ────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Hub token loaded from encrypted storage")]
    private partial void LogHubTokenLoaded();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to decrypt hub token: {ErrorMessage}. Regenerating...")]
    private partial void LogHubTokenDecryptionFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hub token auto-generated and saved to encrypted storage")]
    private partial void LogHubTokenGenerated();

    [LoggerMessage(Level = LogLevel.Information, Message = "Hub token regenerated and saved to encrypted storage")]
    private partial void LogHubTokenRegenerated();

    [LoggerMessage(Level = LogLevel.Information, Message = "API key stored for provider {ProviderName}")]
    private partial void LogApiKeyStored(string providerName);

    [LoggerMessage(Level = LogLevel.Information, Message = "API key removed for provider {ProviderName}")]
    private partial void LogApiKeyRemoved(string providerName);

    [LoggerMessage(Level = LogLevel.Information, Message = "OAuth tokens stored for provider {ProviderName}")]
    private partial void LogOAuthTokensStored(string providerName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to decrypt API key for provider {ProviderName}: {ErrorMessage}")]
    private partial void LogApiKeyDecryptionFailed(string providerName, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Secrets directory created at {DirectoryPath}")]
    private partial void LogSecretsDirectoryCreated(string directoryPath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database encryption key loaded from encrypted storage")]
    private partial void LogDatabaseKeyLoaded();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to decrypt database key: {ErrorMessage}. Regenerating...")]
    private partial void LogDatabaseKeyDecryptionFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database encryption key auto-generated and saved to encrypted storage")]
    private partial void LogDatabaseKeyGenerated();

    [LoggerMessage(Level = LogLevel.Information, Message = "Password hash stored in encrypted storage")]
    private partial void LogPasswordHashStored();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to decrypt password hash: {ErrorMessage}")]
    private partial void LogPasswordHashDecryptionFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord bot token stored in encrypted storage")]
    private partial void LogDiscordBotTokenStored();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to decrypt Discord bot token: {ErrorMessage}")]
    private partial void LogDiscordBotTokenDecryptionFailed(string errorMessage);

    /// <summary>On-disk format for DPAPI-encrypted secrets.</summary>
    internal sealed class EncryptedSecrets
    {
        public string? HubToken { get; set; }
        public Dictionary<string, string>? ApiKeys { get; set; }
        /// <summary>DPAPI-encrypted OAuth refresh tokens, keyed by provider name.</summary>
        public Dictionary<string, string>? RefreshTokens { get; set; }
        /// <summary>OAuth access-token expiry timestamps (Unix ms), keyed by provider name.</summary>
        public Dictionary<string, long>? TokenExpiries { get; set; }
        /// <summary>DPAPI-encrypted database encryption key (Base64-encoded 256-bit key).</summary>
        public string? DatabaseKey { get; set; }
        /// <summary>DPAPI-encrypted bcrypt password hash for Bridge web UI authentication.</summary>
        public string? PasswordHash { get; set; }
        /// <summary>DPAPI-encrypted Discord bot token.</summary>
        public string? DiscordBotToken { get; set; }
    }
}
