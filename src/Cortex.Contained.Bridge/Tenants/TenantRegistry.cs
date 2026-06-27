using System.Collections.Concurrent;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tenants;

/// <summary>
/// Manages the set of tenants. Provides CRUD operations and persists
/// tenant configuration to <c>cortex.yml</c> via a callback.
/// Thread-safe — all mutations go through a lock.
/// </summary>
public sealed partial class TenantRegistry
{
    private readonly ConcurrentDictionary<string, TenantConfig> tenants = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action persistCallback;
    private readonly ILogger<TenantRegistry> logger;
    private readonly object writeLock = new();

    public TenantRegistry(
        BridgeConfig config,
        Action persistCallback,
        ILogger<TenantRegistry> logger)
    {
        this.persistCallback = persistCallback;
        this.logger = logger;

        // Load tenants from config, migrating legacy fields
        foreach (var (id, tenantConfig) in config.Tenants)
        {
            tenantConfig.MigrateLegacyDiscordUsers();
            this.tenants[id] = tenantConfig;
        }

        // Bootstrap: if no tenants are configured but we have an agentHubUrl,
        // create a default tenant entry pointing to it. This makes the existing
        // single-container setup visible in the Tenants page without manual setup.
        if (this.tenants.IsEmpty && !string.IsNullOrWhiteSpace(config.AgentHubUrl))
        {
            var defaultConfig = new TenantConfig
            {
                Endpoint = config.AgentHubUrl,
                Default = true,
                Enabled = true,
                Port = 5100,
            };
            this.tenants["default"] = defaultConfig;
            this.LogDefaultTenantBootstrapped(config.AgentHubUrl);

            // Persist so it's there on next start
            config.Tenants["default"] = defaultConfig;
            Persist();
        }

        // Ensure exactly one default tenant exists
        EnsureDefaultTenant();
        this.LogTenantRegistryInitialized(this.tenants.Count);
    }

    /// <summary>Returns all tenant IDs and their configs.</summary>
    public IReadOnlyDictionary<string, TenantConfig> GetAll() => this.tenants;

    /// <summary>Returns the tenant config for the given ID, or null.</summary>
    public TenantConfig? GetTenant(string tenantId)
        => this.tenants.TryGetValue(tenantId, out var config) ? config : null;

    /// <summary>Returns the default tenant's ID and config, or null if none.</summary>
    public (string Id, TenantConfig Config)? GetDefaultTenant()
    {
        foreach (var (id, config) in this.tenants)
        {
            if (config.Default)
            {
                return (id, config);
            }
        }
        return null;
    }

    /// <summary>
    /// Resolves a channel ID to a tenant ID.
    /// Returns the tenant that has this channel explicitly assigned.
    /// If no tenant claims the channel, returns the default tenant.
    /// </summary>
    public string? ResolveChannel(string channelId)
    {
        foreach (var (id, config) in this.tenants)
        {
            if (config.Channels.Contains(channelId, StringComparer.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return GetDefaultTenant()?.Id;
    }

    /// <summary>
    /// Resolves a Discord user ID to a tenant ID.
    /// Returns the tenant ID if the user is explicitly linked, otherwise null.
    /// Does <b>not</b> fall back to the default tenant — the caller must decide
    /// how to handle unmapped users (e.g. setup code check).
    /// </summary>
    public string? ResolveDiscordUser(string discordUserId)
    {
        foreach (var (id, config) in this.tenants)
        {
            if (string.Equals(config.DiscordUserId, discordUserId, StringComparison.Ordinal))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up a tenant by its active setup code. Returns the tenant ID if found
    /// and the code has not expired, otherwise null.
    /// </summary>
    public string? ResolveSetupCode(string code)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (id, config) in this.tenants)
        {
            if (config.SetupCode is not null
                && string.Equals(config.SetupCode, code, StringComparison.OrdinalIgnoreCase)
                && (config.SetupCodeExpiresAt == 0 || config.SetupCodeExpiresAt > nowMs))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Links a Discord user to a tenant. Clears the setup code after pairing.
    /// </summary>
    public void PairDiscordUser(string tenantId, string discordUserId, string? discordUsername)
    {
        lock (this.writeLock)
        {
            if (!this.tenants.TryGetValue(tenantId, out var config))
            {
                throw new KeyNotFoundException($"Tenant '{tenantId}' not found.");
            }

            config.DiscordUserId = discordUserId;
            config.DiscordUsername = discordUsername;
            config.SetupCode = null;
            config.SetupCodeExpiresAt = 0;
            Persist();
        }
    }

    /// <summary>Unlinks the Discord user from a tenant.</summary>
    public void UnlinkDiscordUser(string tenantId)
    {
        lock (this.writeLock)
        {
            if (!this.tenants.TryGetValue(tenantId, out var config))
            {
                throw new KeyNotFoundException($"Tenant '{tenantId}' not found.");
            }

            config.DiscordUserId = null;
            config.DiscordUsername = null;
            Persist();
        }
    }

    /// <summary>Generates a setup code for a tenant. Returns the code string.</summary>
    public string GenerateSetupCode(string tenantId, TimeSpan expiry)
    {
        lock (this.writeLock)
        {
            if (!this.tenants.TryGetValue(tenantId, out var config))
            {
                throw new KeyNotFoundException($"Tenant '{tenantId}' not found.");
            }

            var code = $"{RandomString(4)}-{RandomString(4)}";
            config.SetupCode = code;
            config.SetupCodeExpiresAt = DateTimeOffset.UtcNow.Add(expiry).ToUnixTimeMilliseconds();
            Persist();
            return code;
        }
    }

    /// <summary>Revokes the active setup code for a tenant.</summary>
    public void RevokeSetupCode(string tenantId)
    {
        lock (this.writeLock)
        {
            if (!this.tenants.TryGetValue(tenantId, out var config))
            {
                throw new KeyNotFoundException($"Tenant '{tenantId}' not found.");
            }

            config.SetupCode = null;
            config.SetupCodeExpiresAt = 0;
            Persist();
        }
    }

    private static string RandomString(int length)
    {
        Span<byte> bytes = stackalloc byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return string.Create(length, bytes.ToArray(), static (span, b) =>
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I/O/0/1 for readability
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = chars[b[i] % chars.Length];
            }
        });
    }

    /// <summary>Adds a new tenant. Throws if the ID already exists.</summary>
    public void AddTenant(string tenantId, TenantConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(config);

        lock (this.writeLock)
        {
            if (this.tenants.ContainsKey(tenantId))
            {
                throw new InvalidOperationException($"Tenant '{tenantId}' already exists.");
            }

            if (config.Default)
            {
                ClearDefaultFlag();
            }

            this.tenants[tenantId] = config;
            EnsureDefaultTenant();
            Persist();
        }

        this.LogTenantAdded(tenantId);
    }

    /// <summary>Updates an existing tenant's config. Throws if not found.</summary>
    public void UpdateTenant(string tenantId, TenantConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(config);

        lock (this.writeLock)
        {
            if (!this.tenants.ContainsKey(tenantId))
            {
                throw new KeyNotFoundException($"Tenant '{tenantId}' not found.");
            }

            if (config.Default)
            {
                ClearDefaultFlag();
            }

            this.tenants[tenantId] = config;
            EnsureDefaultTenant();
            Persist();
        }

        this.LogTenantUpdated(tenantId);
    }

    /// <summary>Removes a tenant. Throws if not found.</summary>
    public void RemoveTenant(string tenantId)
    {
        lock (this.writeLock)
        {
            if (!this.tenants.TryRemove(tenantId, out _))
            {
                throw new KeyNotFoundException($"Tenant '{tenantId}' not found.");
            }

            EnsureDefaultTenant();
            Persist();
        }

        this.LogTenantRemoved(tenantId);
    }

    /// <summary>Sets a tenant as the default. Clears the flag on all others.</summary>
    public void SetDefault(string tenantId)
    {
        lock (this.writeLock)
        {
            if (!this.tenants.TryGetValue(tenantId, out var tenant))
            {
                throw new KeyNotFoundException($"Tenant '{tenantId}' not found.");
            }

            ClearDefaultFlag();
            tenant.Default = true;
            Persist();
        }

        this.LogTenantSetDefault(tenantId);
    }

    /// <summary>Syncs the registry contents back to <see cref="BridgeConfig.Tenants"/>.</summary>
    public void SyncToConfig(BridgeConfig config)
    {
        config.Tenants.Clear();
        foreach (var (id, tenantConfig) in this.tenants)
        {
            config.Tenants[id] = tenantConfig;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────

    private void ClearDefaultFlag()
    {
        foreach (var config in this.tenants.Values)
        {
            config.Default = false;
        }
    }

    private void EnsureDefaultTenant()
    {
        if (this.tenants.IsEmpty)
        {
            return;
        }

        // If no tenant is marked as default, mark the first one
        var defaultTenant = this.tenants.Values.FirstOrDefault(c => c.Default);
        if (defaultTenant is null)
        {
            this.tenants.Values.First().Default = true;
        }
    }

    private void Persist()
    {
        try
        {
            this.persistCallback();
        }
        catch (Exception ex)
        {
            this.LogPersistFailed(ex.Message);
        }
    }

    // ── Logging ───────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "TenantRegistry initialized with {Count} tenants")]
    private partial void LogTenantRegistryInitialized(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Default tenant bootstrapped from agentHubUrl: {Endpoint}")]
    private partial void LogDefaultTenantBootstrapped(string endpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant '{TenantId}' added")]
    private partial void LogTenantAdded(string tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant '{TenantId}' updated")]
    private partial void LogTenantUpdated(string tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant '{TenantId}' removed")]
    private partial void LogTenantRemoved(string tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant '{TenantId}' set as default")]
    private partial void LogTenantSetDefault(string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to persist tenant config: {Error}")]
    private partial void LogPersistFailed(string error);
}
