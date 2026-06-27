using System.Collections.Concurrent;
using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tenants;

/// <summary>
/// Manages multiple <see cref="HubClient"/> instances, one per tenant.
/// Routes messages to the correct tenant's agent container based on tenant ID.
/// Each tenant gets its own HubClient → HubMessageDispatcher pair to avoid
/// conversation ID collisions between tenants.
/// </summary>
public sealed partial class TenantRouter : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TenantConnection> connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly TenantRegistry registry;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<TenantRouter> logger;
    private bool disposed;

    /// <summary>
    /// Called after a <see cref="HubClient"/> is successfully connected.
    /// The Worker sets this before connecting to wire dispatcher and hub events on every tenant.
    /// Parameters: (HubClient client, string tenantId).
    /// </summary>
    public Action<HubClient, string>? OnClientConnected { get; set; }

    public TenantRouter(TenantRegistry registry, ILoggerFactory loggerFactory, ILogger<TenantRouter> logger)
    {
        this.registry = registry;
        this.loggerFactory = loggerFactory;
        this.logger = logger;
    }

    /// <summary>
    /// Gets or creates a <see cref="HubClient"/> for the given tenant.
    /// Returns null if the tenant is not registered or not enabled.
    /// </summary>
    public HubClient? GetClient(string tenantId)
    {
        if (this.disposed)
        {
            return null;
        }

        if (this.connections.TryGetValue(tenantId, out var conn) && conn.Client.IsConnected)
        {
            return conn.Client;
        }

        var config = this.registry.GetTenant(tenantId);
        if (config is null || !config.Enabled)
        {
            this.LogTenantNotFound(tenantId);
            return null;
        }

        // Create or reconnect
        conn = this.connections.GetOrAdd(tenantId, _ => CreateConnection(tenantId, config));
        return conn.Client;
    }

    /// <summary>
    /// Gets the <see cref="HubClient"/> for the default tenant.
    /// Returns null if no default tenant is configured.
    /// </summary>
    public HubClient? GetDefaultClient()
    {
        var defaultTenant = this.registry.GetDefaultTenant();
        return defaultTenant.HasValue ? GetClient(defaultTenant.Value.Id) : null;
    }

    /// <summary>
    /// Returns the tenant ID for a given Discord user, or null if not mapped.
    /// Does <b>not</b> fall back to the default tenant.
    /// </summary>
    public string? ResolveDiscordUser(string discordUserId)
        => this.registry.ResolveDiscordUser(discordUserId);

    /// <summary>
    /// Returns the tenant ID that owns a given channel, or the default tenant.
    /// </summary>
    public string? ResolveChannel(string channelId)
        => this.registry.ResolveChannel(channelId);

    /// <summary>
    /// Looks up a tenant by its active setup code. Returns null if no match or expired.
    /// </summary>
    public string? ResolveSetupCode(string code)
        => this.registry.ResolveSetupCode(code);

    /// <summary>
    /// Links a Discord user to a tenant and clears the setup code.
    /// </summary>
    public void PairDiscordUser(string tenantId, string discordUserId, string? discordUsername)
        => this.registry.PairDiscordUser(tenantId, discordUserId, discordUsername);

    /// <summary>Returns true if the given tenant has an active connection.</summary>
    public bool IsConnected(string tenantId)
        => this.connections.TryGetValue(tenantId, out var conn) && conn.Client.IsConnected;

    /// <summary>
    /// Disconnects and removes the connection for a tenant.
    /// Called when a tenant is stopped or deleted.
    /// </summary>
    public async Task DisconnectTenantAsync(string tenantId)
    {
        if (this.connections.TryRemove(tenantId, out var conn))
        {
            await conn.Client.DisposeAsync().ConfigureAwait(false);
            this.LogTenantDisconnected(tenantId);
        }
    }

    /// <summary>Gets all connected tenant IDs.</summary>
    public IReadOnlyList<string> GetConnectedTenantIds()
        => this.connections.Where(kv => kv.Value.Client.IsConnected).Select(kv => kv.Key).ToList();

    /// <summary>Gets status info for all registered tenants.</summary>
    public IReadOnlyList<TenantStatus> GetAllStatuses()
    {
        var result = new List<TenantStatus>();
        foreach (var (id, config) in this.registry.GetAll())
        {
            var isConnected = this.connections.TryGetValue(id, out var conn) && conn.Client.IsConnected;
            result.Add(new TenantStatus
            {
                TenantId = id,
                Endpoint = config.Endpoint,
                IsDefault = config.Default,
                Enabled = config.Enabled,
                Connected = isConnected,
                Port = config.Port,
                ImageVersion = config.ImageVersion,
                Channels = config.Channels,
                DiscordUserId = config.DiscordUserId,
                DiscordUsername = config.DiscordUsername,
                HasSetupCode = config.SetupCode is not null
                    && (config.SetupCodeExpiresAt == 0
                        || config.SetupCodeExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                SetupCodeExpiresAt = config.SetupCodeExpiresAt,
                ApiEnabled = config.ApiEnabled,
            });
        }
        return result;
    }

    // ── Connection Management ────────────────────────────────────────

    /// <summary>
    /// Creates and connects the default tenant's <see cref="HubClient"/>.
    /// This is the primary connection method — called once at Bridge startup.
    /// Returns the connected client for event wiring.
    /// </summary>
    public async Task<HubClient> ConnectDefaultAsync(string hubUrl, string hubToken, CancellationToken cancellationToken)
    {
        var defaultTenant = this.registry.GetDefaultTenant()
            ?? throw new InvalidOperationException("No default tenant configured. Cannot connect.");

        var conn = this.connections.GetOrAdd(defaultTenant.Id, _ => CreateConnection(defaultTenant.Id, defaultTenant.Config));

        // Override endpoint in case config was bootstrapped before we had the URL
        if (conn.Endpoint != hubUrl)
        {
            conn = new TenantConnection(conn.Client, hubUrl);
            this.connections[defaultTenant.Id] = conn;
        }

        await conn.Client.ConnectAsync(hubUrl, hubToken, cancellationToken).ConfigureAwait(false);
        this.LogTenantConnected(defaultTenant.Id, hubUrl);

        this.OnClientConnected?.Invoke(conn.Client, defaultTenant.Id);

        return conn.Client;
    }

    /// <summary>
    /// Rebuilds the default tenant's underlying hub connection on the EXISTING
    /// <see cref="HubClient"/> instance. Used by the Worker's stuck-connection
    /// watchdog when SignalR's automatic reconnect has wedged (e.g. a hung
    /// WebSocket connect that never times out). Deliberately does NOT fire
    /// <see cref="OnClientConnected"/> — the client instance is reused, so its
    /// dispatcher/hub events are already wired and re-firing would double-wire them.
    /// </summary>
    public async Task ReconnectDefaultAsync(string hubUrl, string hubToken, CancellationToken cancellationToken)
    {
        var defaultTenant = this.registry.GetDefaultTenant()
            ?? throw new InvalidOperationException("No default tenant configured. Cannot reconnect.");

        if (!this.connections.TryGetValue(defaultTenant.Id, out var conn))
        {
            await ConnectDefaultAsync(hubUrl, hubToken, cancellationToken).ConfigureAwait(false);
            return;
        }

        await conn.Client.ConnectAsync(hubUrl, hubToken, cancellationToken).ConfigureAwait(false);
        this.LogTenantConnected(defaultTenant.Id, hubUrl);
    }

    /// <summary>
    /// Connects a non-default tenant's <see cref="HubClient"/> to its Agent Hub endpoint.
    /// </summary>
    public async Task ConnectTenantAsync(string tenantId, string hubToken, CancellationToken cancellationToken)
    {
        var config = this.registry.GetTenant(tenantId);
        if (config is null)
        {
            return;
        }

        var conn = this.connections.GetOrAdd(tenantId, _ => CreateConnection(tenantId, config));

        if (!conn.Client.IsConnected)
        {
            await conn.Client.ConnectAsync(conn.Endpoint, hubToken, cancellationToken).ConfigureAwait(false);
            this.LogTenantConnected(tenantId, conn.Endpoint);

            this.OnClientConnected?.Invoke(conn.Client, tenantId);
        }
    }

    // ── Private ───────────────────────────────────────────────────────

    private TenantConnection CreateConnection(string tenantId, TenantConfig config)
    {
        var hubLogger = this.loggerFactory.CreateLogger<HubClient>();
        var client = new HubClient(hubLogger);

        this.LogTenantConnectionCreated(tenantId, config.Endpoint);
        return new TenantConnection(client, config.Endpoint);
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        foreach (var (id, conn) in this.connections)
        {
            try
            {
                await conn.Client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best effort cleanup
            }
        }
        this.connections.Clear();
    }

    // ── Logging ───────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant '{TenantId}' not found or not enabled")]
    private partial void LogTenantNotFound(string tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created connection for tenant '{TenantId}' at {Endpoint}")]
    private partial void LogTenantConnectionCreated(string tenantId, string endpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant '{TenantId}' connected to {Endpoint}")]
    private partial void LogTenantConnected(string tenantId, string endpoint);


    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant '{TenantId}' disconnected")]
    private partial void LogTenantDisconnected(string tenantId);
}

/// <summary>Holds a HubClient and its endpoint for a tenant connection.</summary>
internal sealed record TenantConnection(HubClient Client, string Endpoint);

/// <summary>Status of a tenant for the admin dashboard.</summary>
public sealed record TenantStatus
{
    public required string TenantId { get; init; }
    public required string Endpoint { get; init; }
    public required bool IsDefault { get; init; }
    public required bool Enabled { get; init; }
    public required bool Connected { get; init; }
    public required int Port { get; init; }
    public string? ImageVersion { get; init; }
    public List<string> Channels { get; init; } = [];

    // Discord pairing
    public string? DiscordUserId { get; init; }
    public string? DiscordUsername { get; init; }
    public bool HasSetupCode { get; init; }
    public long SetupCodeExpiresAt { get; init; }

    // API channel
    public bool ApiEnabled { get; init; }
}
