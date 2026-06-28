# Host MCP Plugin System — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax. Strict TDD: write the failing test, run it red, implement minimally, run it green, commit. One commit per task.

**Goal:** Let the Cortex agent use arbitrary third-party MCP servers (local stdio + remote HTTP) as native tools, with all server processes and authentication running on the host (Bridge), the agent staying credential-free, and full Web UI management.

**Architecture:** Bridge = MCP host + credential boundary. Bridge runs MCP clients (`ModelContextProtocol` C# SDK), discovers tools, pushes a namespaced catalog to the agent over the existing SignalR hub. Agent registers them as dynamic `mcp__<server>__<tool>` proxy tools; calls route Agent→SignalR→Bridge→MCP server with auth attached on the host. Spec: `docs/superpowers/specs/2026-06-28-host-mcp-plugin-system-design.md`.

**Tech Stack:** .NET 10, SignalR (typed `IAgentHub`/`IAgentHubClient`), `ModelContextProtocol` C# SDK, DPAPI secret storage, OAuth 2.1 (PKCE + DCR), ASP.NET Core minimal APIs + Kestrel `:5080`, vanilla JS + Alpine Web UI, xUnit + NSubstitute.

## Global Constraints

- .NET 10; `TreatWarningsAsErrors` ON — warning-clean.
- C# style (CLAUDE.md): `this.`-prefixed instance access; braces on ALL control blocks; one type per file; file-scoped namespaces; `var` when type obvious; `sealed` by default; `readonly` fields; `ConfigureAwait(false)` in library/Bridge/agent code; source-generated `[LoggerMessage]` partial classes for logging (structured, no interpolation).
- Central package management: add packages via `Directory.Packages.props` `<PackageVersion>` + bare `<PackageReference>` in csproj.
- SignalR routing by `nameof(...)`. Composed hub interfaces: add a new slice interface and include it in `IAgentHub`/`IAgentHubClient`.
- `IAgentTool.ExecuteAsync(string argumentsJson, ToolExecutionContext, CancellationToken)` returns `AgentToolResult` (`Ok(content)` / `Fail(error)`).
- Secrets NEVER in `cortex.yml` (only `secretRef` ids); secret values only in DPAPI; never logged.
- All enable flags default `true`; zero configured servers ⇒ agent tool list unchanged.
- Test naming `Method_Condition_Expected` (CA1707 suppressed in test projects).
- Telemetry: every cross-boundary op (catalog push, tool invoke, server connect, auth flow) emits a structured `[LoggerMessage]` with server key + outcome (never secret values).

## File structure (new components)

```
Contracts/Hub/
  HubTypes.Mcp.cs            McpToolDefinition, McpToolCatalog, McpToolInvocation, McpToolResult, McpServerStatusDto
  IMcpHub.cs                 Bridge→Agent: UpdateMcpToolCatalog
  IMcpHubClient.cs           Agent→Bridge: InvokeMcpTool
Contracts/Config/
  McpServerConfig.cs         per-server config (key, transport, url/command/args/env, auth, secretRef, toolAllowList, enabled)
  McpSettingsConfig.cs       master Enabled + List<McpServerConfig>  (added to BridgeConfig)

Agent.Host/Mcp/
  IMcpGateway.cs             agent-side: InvokeAsync(server, tool, argsJson, ct) -> McpToolResult
  SignalRMcpGateway.cs       impl over IBridgeClientProvider (mirror SignalRCodingAgent)
  McpToolStore.cs            mutable, thread-safe; holds McpProxyTool set + Version; Update(catalog)
  McpProxyTool.cs            IAgentTool built from McpToolDefinition + IMcpGateway
Agent.Host/Tools/
  ToolRegistry.cs            MODIFY: merge static + McpToolStore dynamic tools; dispatch; cache on both versions
Agent.Host/Hubs/
  AgentHub.Mcp.cs            partial: UpdateMcpToolCatalog handler -> McpToolStore.Update

Bridge/Mcp/
  IMcpServerConnection.cs    Connect/ListTools/CallTool/Status; one per server
  StdioMcpServerConnection.cs   spawns process (env from DPAPI), MCP SDK stdio transport
  HttpMcpServerConnection.cs    HttpClient + auth handler, MCP SDK http transport
  McpServerConnectionFactory.cs  builds the right connection from McpServerConfig + McpAuthManager
  McpHostService.cs          IHostedService: reconcile connections vs config+enable; aggregate catalog; route invoke
  McpCatalogPusher.cs        pushes catalog to agent over hub (mirror CredentialsPusher); re-push on change/reconnect
  McpConfigStore.cs          read/write mcpServers in cortex.yml (non-secret) + per-tenant
  Auth/IMcpAuthManager.cs    resolve auth per server (none|apiKey|oauth)
  Auth/McpStaticAuth.cs      apiKey/env injection from DPAPI
  Auth/McpOAuthManager.cs    OAuth2.1: metadata discovery, DCR, PKCE, loopback callback, token store, refresh
  Auth/McpOAuthMetadata.cs   pure parsers (401/WWW-Authenticate, protected-resource + AS metadata)
  Auth/McpTokenStore.cs      DPAPI-backed access/refresh token storage per server
Bridge/Hub/
  HubClient.Mcp.cs           partial: PushMcpToolCatalogAsync + OnInvokeMcpTool callback registration
Bridge/Endpoints/
  McpEndpoints.cs            REST: list/add/edit/delete servers, enable, connect(OAuth), test, allow-list
Bridge/wwwroot/
  js/pages/mcp-servers.js    Alpine page
  app.html                   MODIFY: MCP Servers tab + markup
```

---

# PHASE 1 — Contracts + agent dynamic-tool plumbing (fake catalog, no real servers)

Delivers: the agent can receive a pushed tool catalog and expose `mcp__*` tools that route to an `IMcpGateway`; everything testable with fakes. No Bridge MCP host yet.

### Task 1.1: MCP SignalR DTOs

**Files:** Create `src/Cortex.Contained.Contracts/Hub/HubTypes.Mcp.cs`; Test `tests/Cortex.Contained.Contracts.Tests/Mcp/McpDtoTests.cs` (create test project dir if needed — mirror existing Contracts tests location; if no Contracts.Tests project exists, put tests in `tests/Cortex.Contained.Bridge.Tests/Mcp/McpDtoTests.cs`).

**Interfaces — Produces:**
```csharp
namespace Cortex.Contained.Contracts.Hub;

public sealed record McpToolDefinition
{
    public required string ServerKey { get; init; }
    public required string ToolName { get; init; }
    /// <summary>Namespaced agent-facing name: mcp__{ServerKey}__{ToolName}.</summary>
    public required string FullName { get; init; }
    public required string Description { get; init; }
    /// <summary>JSON Schema (string) for the tool's parameters.</summary>
    public required string ParametersSchemaJson { get; init; }
}

public sealed record McpToolCatalog
{
    /// <summary>Full replace-all catalog of currently-available MCP tools across all enabled servers.</summary>
    public IReadOnlyList<McpToolDefinition> Tools { get; init; } = [];
}

public sealed record McpToolInvocation
{
    public required string ServerKey { get; init; }
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
    public string? ConversationId { get; init; }
}

public sealed record McpToolResult
{
    public required bool IsError { get; init; }
    /// <summary>Flattened MCP content (text/json) on success; empty on error.</summary>
    public string Content { get; init; } = string.Empty;
    public bool NeedsAuth { get; init; }
    public string? Error { get; init; }
    public static McpToolResult Ok(string content) => new() { IsError = false, Content = content };
    public static McpToolResult Fail(string error, bool needsAuth = false) => new() { IsError = true, Error = error, NeedsAuth = needsAuth };
}
```

- [ ] Step 1: failing test — assert `McpToolResult.Ok("x").IsError == false && .Content=="x"`; `Fail("e", needsAuth:true).NeedsAuth`; `new McpToolCatalog().Tools` empty; `FullName` round-trips through System.Text.Json (serialize+deserialize equality).
- [ ] Step 2: run red (types missing).
- [ ] Step 3: create the file above.
- [ ] Step 4: run green.
- [ ] Step 5: commit `feat(mcp): SignalR DTOs for tool catalog + invocation`.

### Task 1.2: Hub interface slices

**Files:** Create `Contracts/Hub/IMcpHub.cs`, `Contracts/Hub/IMcpHubClient.cs`; Modify `Contracts/Hub/IAgentHub.cs` (add `IMcpHub`), `Contracts/Hub/IAgentHubClient.cs` (add `IMcpHubClient`).

**Produces:**
```csharp
// IMcpHub.cs — Bridge→Agent (agent exposes; Bridge invokes)
public interface IMcpHub { Task UpdateMcpToolCatalog(McpToolCatalog catalog); }
// IMcpHubClient.cs — Agent→Bridge (Bridge handles; agent invokes via Clients proxy, Client Results)
public interface IMcpHubClient { Task<McpToolResult> InvokeMcpTool(McpToolInvocation invocation); }
```
Add `IMcpHub` to the `IAgentHub : ...` composition and `IMcpHubClient` to `IAgentHubClient : ...`.

- [ ] Step 1: failing test — a test that references `typeof(IAgentHub).GetInterfaces()` contains `IMcpHub` and `IAgentHubClient` contains `IMcpHubClient` (compile-time + reflection assert).
- [ ] Step 2: red (interfaces missing).
- [ ] Step 3: implement.
- [ ] Step 4: green; `dotnet build src/Cortex.Contained.Contracts`.
- [ ] Step 5: commit `feat(mcp): IMcpHub + IMcpHubClient hub slices`.

### Task 1.3: IMcpGateway + McpToolStore (dynamic, versioned)

**Files:** Create `Agent.Host/Mcp/IMcpGateway.cs`, `Agent.Host/Mcp/McpToolStore.cs`; Test `tests/Cortex.Contained.Agent.Host.Tests/Mcp/McpToolStoreTests.cs`.

**Produces:**
```csharp
public interface IMcpGateway
{
    Task<McpToolResult> InvokeAsync(string serverKey, string toolName, string argumentsJson, string? conversationId, CancellationToken cancellationToken);
}

// McpToolStore: singleton, thread-safe. Holds current IAgentTool proxies keyed by FullName + a Version (int) bumped on every Update.
public sealed class McpToolStore
{
    public int Version { get; }                  // bumped on Update
    public IReadOnlyCollection<IAgentTool> Tools { get; }   // snapshot of McpProxyTool instances
    public bool TryGet(string fullName, out IAgentTool tool);
    public void Update(McpToolCatalog catalog);  // rebuilds proxies via injected IMcpGateway
}
```
Inject `IMcpGateway` into `McpToolStore` ctor so `Update` builds `McpProxyTool` per definition.

- [ ] Step 1: failing test — empty store: Version 0, Tools empty, TryGet false. After `Update(catalog with 2 defs)`: Version incremented, Tools has 2, `TryGet("mcp__srv__a")` true. After `Update(empty)`: Tools empty, Version incremented again. Use a `Substitute.For<IMcpGateway>()`.
- [ ] Step 2: red.
- [ ] Step 3: implement with `lock` + swap of an immutable `FrozenDictionary<string,IAgentTool>`; `Interlocked`-style version bump under lock.
- [ ] Step 4: green.
- [ ] Step 5: commit `feat(mcp): McpToolStore dynamic versioned tool store + IMcpGateway`.

### Task 1.4: McpProxyTool

**Files:** Create `Agent.Host/Mcp/McpProxyTool.cs`; Test `tests/.../Mcp/McpProxyToolTests.cs`.

**Produces:** `McpProxyTool : IAgentTool` with `Name = def.FullName`, `Description = def.Description`, `ParametersSchema = def.ParametersSchemaJson`; `ExecuteAsync` calls `gateway.InvokeAsync(def.ServerKey, def.ToolName, argumentsJson, context.ConversationId, ct)` and maps `McpToolResult`→`AgentToolResult` (`IsError` → `Fail`, with `NeedsAuth` surfaced in the error text e.g. "needs authorization: ..."; else `Ok(content)`). Emits `[LoggerMessage]` on invoke + outcome (server, tool, isError — no args/secrets).

- [ ] Step 1: failing test — fake gateway returns `Ok("hi")` ⇒ `ExecuteAsync` returns `Success, Content "hi"`. Gateway returns `Fail("boom")` ⇒ `Success false, Error contains "boom"`. `NeedsAuth` result ⇒ error text mentions auth. Name/schema mirror the def.
- [ ] Step 2: red. Step 3: implement. Step 4: green.
- [ ] Step 5: commit `feat(mcp): McpProxyTool maps MCP results to agent tool results`.

### Task 1.5: ToolRegistry merges static + dynamic MCP tools

**Files:** Modify `Agent.Host/Tools/ToolRegistry.cs`; Test extend `tests/.../Tools/ToolRegistryMcpTests.cs`.

**Design:** Inject optional `McpToolStore? mcpToolStore = null` into `ToolRegistry`. 
- `GetDefinitions()`/`GetDefinitionsForConversation()`: union the static `toolList` with `mcpToolStore?.Tools`; invalidate the cache when EITHER `activeChannelStore.Version` OR `mcpToolStore.Version` changes (track a composite `(channelVersion, mcpVersion)`).
- `ExecuteAsync`: if name not in the static frozen dict, fall back to `mcpToolStore.TryGet(name, out tool)`; if found, execute it (with the same truncation path); else `Fail("Unknown tool")`.
- Gates still apply (an MCP tool can be hidden by an `IConversationToolGate`).

- [ ] Step 1: failing tests — with a store holding 1 MCP tool: `GetDefinitions()` includes it; calling it via `ExecuteAsync` dispatches to the proxy; bumping the store Version rebuilds the cached definitions (new tool appears). Static tools still work; unknown name still fails. (Construct ToolRegistry with a real `McpToolStore` backed by a `Substitute.For<IMcpGateway>()`.)
- [ ] Step 2: red. Step 3: implement (composite version cache key; union; fallback dispatch). Step 4: green; run the FULL existing `ToolRegistry` tests to ensure no regression.
- [ ] Step 5: commit `feat(mcp): ToolRegistry merges dynamic MCP tools (cache-invalidated, dispatchable)`.

### Task 1.6: SignalRMcpGateway (agent proxy over the hub)

**Files:** Create `Agent.Host/Mcp/SignalRMcpGateway.cs`; Test `tests/.../Mcp/SignalRMcpGatewayTests.cs`. Mirror `Agent.Host/Coding/SignalRCodingAgent.cs` exactly (its `IBridgeClientProvider`, timeout, `Client` accessor, `OperationCanceledException`→bridge-unreachable mapping).

**Produces:** `SignalRMcpGateway : IMcpGateway` whose `InvokeAsync` calls `this.Client.InvokeMcpTool(new McpToolInvocation{...})` with the configured invoke timeout; on timeout/disconnect returns `McpToolResult.Fail("MCP bridge unreachable")`.

- [ ] Step 1: failing test — substitute `IBridgeClientProvider` whose `Client.InvokeMcpTool(...)` returns `Ok("x")` ⇒ gateway returns it. When `Client` is null (disconnected) ⇒ `Fail` with unreachable message (no throw).
- [ ] Step 2: red. Step 3: implement (copy SignalRCodingAgent structure). Step 4: green.
- [ ] Step 5: commit `feat(mcp): SignalRMcpGateway routes tool calls to the Bridge`.

### Task 1.7: AgentHub.UpdateMcpToolCatalog handler + DI wiring

**Files:** Create `Agent.Host/Hubs/AgentHub.Mcp.cs` (partial); Modify `Agent.Host/Program.cs` (register `McpToolStore` singleton, `IMcpGateway`→`SignalRMcpGateway`, inject store into `ToolRegistry` + `AgentHub`); Test `tests/.../Hubs/AgentHubMcpTests.cs`.

**Produces:** `public Task UpdateMcpToolCatalog(McpToolCatalog catalog) { this.mcpToolStore.Update(catalog.Tools is null ? new() : catalog); return Task.CompletedTask; }` with `[LoggerMessage]` "MCP catalog updated: {Count} tools". Inject `McpToolStore` into `AgentHub` ctor (mirror `memorySettingsStore`/`speakerIdSettingsStore`).

- [ ] Step 1: failing test — construct `AgentHub` (or call the partial method on a minimal harness like the existing AgentHub tests) with a real `McpToolStore`; `UpdateMcpToolCatalog(catalog 2 tools)` ⇒ store has 2 tools, Version bumped.
- [ ] Step 2: red. Step 3: implement + Program.cs registration. Step 4: green; `dotnet build src/Cortex.Contained.Agent.Host`.
- [ ] Step 5: commit `feat(mcp): agent receives + registers pushed MCP tool catalog`.

### Phase 1 verification + review
- [ ] `dotnet test tests/Cortex.Contained.Agent.Host.Tests` + `tests/Cortex.Contained.Contracts.Tests` (or Bridge.Tests) green; full-suite no regression.
- [ ] CODE REVIEW (subagent): correctness of dynamic registration + cache invalidation + thread safety on `McpToolStore`; no static-tool regression. Fix findings.

---

# PHASE 2 — Bridge MCP host: stdio + http transports, none/apiKey auth, yaml config, catalog push

Delivers: real MCP servers configured in `cortex.yml` connect on the Bridge; their tools flow to the agent and calls round-trip. No OAuth, no Web UI yet (config by hand-editing yaml).

### Task 2.1: Add `ModelContextProtocol` package
- [ ] Add `<PackageVersion Include="ModelContextProtocol" Version="<latest stable>" />` to `Directory.Packages.props`; `<PackageReference Include="ModelContextProtocol" />` to `Cortex.Contained.Bridge.csproj`. Run `dotnet restore`; confirm the client types (`McpClientFactory`/`IMcpClient`, stdio + http transport) resolve. Commit `chore(mcp): add ModelContextProtocol C# SDK`.
  > Implementer: pin the exact version available; verify the client API surface (`ListToolsAsync`, `CallToolAsync`, transport constructors) against the package and adapt the connection wrappers below to the real SDK signatures.

### Task 2.2: Config DTOs
**Files:** Create `Contracts/Config/McpServerConfig.cs`, `Contracts/Config/McpSettingsConfig.cs`; Modify `Contracts/Config/BridgeConfig.cs` (add `public McpSettingsConfig Mcp { get; set; } = new();`). Tests for defaults (Enabled true; lists empty; transport enum).
```csharp
public enum McpTransport { Stdio, Http }
public enum McpAuthMode { Auto, None, ApiKey, OAuth }
public sealed class McpServerConfig {
  public string Key {get;set;}=""; public bool Enabled {get;set;}=true;
  public McpTransport Transport {get;set;}
  public string? Url {get;set;}                  // http
  public string? Command {get;set;} public List<string> Args {get;set;}=[]; // stdio
  public Dictionary<string,string> Env {get;set;}=[];   // stdio: VALUE may be a secretRef token "${secret:id}"
  public McpAuthMode Auth {get;set;}=McpAuthMode.Auto;
  public string? ApiKeyHeader {get;set;}         // apiKey+http (default Authorization Bearer)
  public string? SecretRef {get;set;}            // DPAPI key id for apiKey
  public List<string> ToolAllowList {get;set;}=[];   // empty = all
}
public sealed class McpSettingsConfig { public bool Enabled {get;set;}=true; public List<McpServerConfig> Servers {get;set;}=[]; }
```
Commit `feat(mcp): config DTOs (McpServerConfig/McpSettingsConfig)`.

### Task 2.3: Namespacing helper (pure)
**Files:** `Bridge/Mcp/McpToolNamer.cs` + tests. `Full(serverKey, toolName) => "mcp__"+serverKey+"__"+toolName` (validate serverKey `[a-z0-9_-]+`, lowercased; throw/validate on bad keys); `TryParse(fullName, out server, out tool)`. Commit.

### Task 2.4: IMcpServerConnection + stdio + http connections
**Files:** `Bridge/Mcp/IMcpServerConnection.cs` (`Task ConnectAsync(ct)`, `IReadOnlyList<McpToolDefinition> Tools`, `McpServerStatus Status`, `Task<McpToolResult> CallToolAsync(tool, argsJson, ct)`, `Task DisposeAsync`); `StdioMcpServerConnection.cs`; `HttpMcpServerConnection.cs`; factory. Tests use the SDK against an in-repo **fake MCP server** (a tiny stdio echo server script + an in-proc http handler) — handshake→list→call→result; allow-list filtering applied to `Tools`; secret env injected (verify a provided secret reaches the process env via a server that echoes it). Status transitions (Connecting/Connected/Error). Commit per connection type.
  > Reuse the existing host-process spawn approach used by Coda/coding (look at `Bridge/Coding/CodaSession` `SpawnAndConnectAsync`) for stdio process management + clean-env spawning.

### Task 2.5: McpStaticAuth + secret storage seam
**Files:** `Bridge/Mcp/Auth/IMcpAuthManager.cs`, `Bridge/Mcp/Auth/McpStaticAuth.cs`; uses the Bridge's existing DPAPI `SecretManager` (grep `SecretManager`/`ISecretStore`). `apiKey` → resolve `SecretRef`→token→ inject (env for stdio, `Authorization: Bearer`/custom header for http). `none` → no-op. (OAuth deferred to Phase 3 — `Auto`/`OAuth` modes return "needs auth: oauth not yet configured" placeholder that Phase 3 replaces.) Tests: mode resolution, header vs env injection, secretRef→DPAPI lookup (substitute the secret store), redaction (never log the value). Commit.

### Task 2.6: McpConfigStore + McpHostService + catalog aggregation
**Files:** `Bridge/Mcp/McpConfigStore.cs` (read `BridgeConfig.Mcp`; persist via `BridgeSettingsWriter` with secret redaction); `Bridge/Mcp/McpHostService.cs` (`IHostedService`: on start + on config-change reconcile connections vs enabled flags — start-if-should/stop-if-disabled, mirror `EmbeddingsSidecarLifecycle` shape; aggregate all connections' `Tools` into one `McpToolCatalog`; expose `Task<McpToolResult> InvokeAsync(server, tool, argsJson, ct)` routing to the connection or `Fail`/`NeedsAuth` if down). Tests with fake connections: aggregation, allow-list, reconcile add/remove/disable, invoke routing, master-Enabled gate. Commit.

### Task 2.7: HubClient.Mcp + McpCatalogPusher + agent-invoke handler wiring
**Files:** `Bridge/Hub/HubClient.Mcp.cs` (partial: `PushMcpToolCatalogAsync(catalog,ct)` → `InvokeAsync(nameof(IAgentHub.UpdateMcpToolCatalog),...)`; `OnInvokeMcpTool` event + `RegisterMcpCallbacks` → `connection.On<McpToolInvocation,McpToolResult>(nameof(IAgentHubClient.InvokeMcpTool), req => OnInvokeMcpTool?.Invoke(req) ?? Fail)`); call `RegisterMcpCallbacks` from `RegisterCallbacks`. `Bridge/Mcp/McpCatalogPusher.cs` pushes on host-service catalog-change + on agent (re)connect (mirror `CredentialsPusher` + how SpeakerId/Memory push on connect via `Worker.cs`/`TenantConnectionBootstrapper.cs`). Wire `OnInvokeMcpTool` → `McpHostService.InvokeAsync`. Tests: pusher pushes on change; handler routes to host service. Commit.

### Task 2.8: DI + startup wiring + end-to-end
**Files:** Modify `Bridge/Program.cs` (register `McpHostService` hosted service, `McpConfigStore`, `IMcpAuthManager`/`McpStaticAuth`, `McpServerConnectionFactory`, `McpCatalogPusher`; wire `HubClient.OnInvokeMcpTool`; push catalog on connect/reconnect). End-to-end test (Bridge.Tests, fake MCP server + a fake agent hub) OR a documented manual check: configure a real stdio server (e.g. `@modelcontextprotocol/server-filesystem`) in cortex.yml, start Bridge, confirm the agent lists `mcp__filesystem__*` and a call round-trips. Commit `feat(mcp): Bridge MCP host end-to-end (stdio+http, apiKey/none)`.

### Phase 2 verification + review
- [ ] Full Bridge.Tests green; manual stdio + http server round-trip.
- [ ] CODE REVIEW (subagent): transport correctness, secret redaction (no secret in yaml/logs), reconcile lifecycle, invoke error mapping, thread safety. Fix.

---

# PHASE 3 — OAuth 2.1 auto-discovery (HTTP), DPAPI token store, refresh

Delivers: HTTP servers requiring OAuth complete a host browser consent flow and stay authorized.

### Task 3.1: OAuth metadata parsers (pure, fully unit-tested)
**Files:** `Bridge/Mcp/Auth/McpOAuthMetadata.cs` + tests. Pure functions: parse `WWW-Authenticate` for `resource_metadata` URL; parse protected-resource-metadata JSON → authorization server(s); parse AS metadata / OIDC config → `{authorization_endpoint, token_endpoint, registration_endpoint, scopes}`. Table-driven tests with sample docs. Commit.

### Task 3.2: PKCE + DCR helpers (pure)
**Files:** `Bridge/Mcp/Auth/McpPkce.cs` (verifier/challenge S256), `Bridge/Mcp/Auth/McpDynamicClientRegistration.cs` (build RFC 7591 request; parse response → client_id/secret). Unit tests (challenge = BASE64URL(SHA256(verifier)); DCR request/response shape). Commit.

### Task 3.3: McpTokenStore (DPAPI)
**Files:** `Bridge/Mcp/Auth/McpTokenStore.cs` over the Bridge DPAPI secret store; stores `{access, refresh, expiresAt, clientId, clientSecret}` per server key; get/set/clear. Tests with substituted secret store: round-trip, redaction, expiry check. Commit.

### Task 3.4: McpOAuthManager (flow orchestration + loopback callback)
**Files:** `Bridge/Mcp/Auth/McpOAuthManager.cs`; loopback callback registered on the existing `:5080` Kestrel (`GET /mcp/oauth/callback`) — add to `McpEndpoints` or a minimal endpoint. Flow: on 401→discover→DCR(if needed)→open system browser (`Process.Start` the auth URL with PKCE+state)→await code on the loopback (keyed by `state`)→exchange→store tokens. `BuildAuthorizationUrlAsync(server)` returns the URL + a pending-state; `CompleteAsync(state, code)` finishes. `GetAccessTokenAsync(server)` returns a valid token, refreshing if expired (integrate with the Bridge token-refresh background service pattern). Tests: drive the flow with a stub authorization server (no real browser; call `CompleteAsync` directly); refresh-on-expiry; 401→refresh→retry in the http connection. Commit.

### Task 3.5: Wire OAuth into HttpMcpServerConnection + Auto discovery
**Files:** Modify `HttpMcpServerConnection` + `McpStaticAuth`/`IMcpAuthManager`: `Auto` mode probes; on 401 with OAuth metadata → mark `NeedsAuth` (status `NeedsLogin`) until the user completes "Connect"; once tokens exist, attach bearer + transparent refresh-and-retry on 401. Tests with stub server. Commit `feat(mcp): OAuth 2.1 auto-discovery for HTTP MCP servers`.

### Phase 3 verification + review
- [ ] Full Bridge.Tests green; manual: a real OAuth MCP server → browser consent → connected → tool call works; restart preserves tokens (DPAPI); refresh works.
- [ ] CODE REVIEW (subagent): OAuth correctness (PKCE, state validation, no token leakage, redaction), refresh, callback security (state binding, single-use). Fix.

---

# PHASE 4 — Web UI "MCP Servers" page + REST endpoints

Delivers: full management UX on the host.

### Task 4.1: REST endpoints
**Files:** `Bridge/Endpoints/McpEndpoints.cs` (+ request DTOs in `SetupHelpers.cs` pattern). `GET /api/mcp/servers` (list + status + tool counts, secrets redacted), `POST /api/mcp/servers` (add), `PUT /api/mcp/servers/{key}` (edit incl. enable + allow-list + secret→DPAPI), `DELETE /api/mcp/servers/{key}`, `POST /api/mcp/servers/{key}/test` (spawn/handshake, return discovered tools or stderr), `POST /api/mcp/servers/{key}/connect` (start OAuth → returns auth URL / opens browser), `POST /api/mcp/toggle` (master). All `RequireAuthorization()`; persist via `McpConfigStore`; live re-push catalog. Pure apply/validation helpers extracted + unit-tested (key uniqueness, secret redaction in responses, allow-list apply). Commit.

### Task 4.2: Web UI page
**Files:** `Bridge/wwwroot/js/pages/mcp-servers.js` + `app.html` tab. Server list with status badges (connected/error/needs login/disabled) + tool count; add/edit modal (transport-aware fields; auth selector Auto/None/ApiKey/OAuth; secret input → never echoed back); master + per-server enable toggles (live); "Connect" button for OAuth (opens browser, polls status → connected); per-tool allow-list checkboxes; "Test connect" surfacing stderr. Match existing `global-settings.js` style + the toast/restart-modal components. Manual verification checklist (no JS test harness). Commit `feat(mcp): web UI MCP Servers management page`.

### Phase 4 verification + review
- [ ] Manual: add stdio + http(OAuth) servers via UI; enable/disable live; allow-list; connect flow; secrets never round-trip to the browser.
- [ ] CODE REVIEW (subagent): endpoint authz, secret redaction in all responses, validation, UX correctness. Fix.

---

# PHASE 5 — Hardening, telemetry, security pass

### Task 5.1: Reconnect/backoff + structured telemetry
Auto-restart crashed stdio/dropped http with exponential backoff; `[LoggerMessage]` for connect/disconnect/invoke/auth-flow/catalog-push (server key + outcome, never secrets). Counters/metrics where the codebase already exposes them (grep existing `Metrics`/`Meter`). Tests for backoff + reconnect re-push. Commit.

### Task 5.2: Kill-switch + gates + collision validation
Master `mcp.enabled` and per-server enable instantly drop tools (live, no restart) — reuse the subsystem-toggle pattern; duplicate server-key/tool collision rejected at save; optional `IConversationToolGate` to hide a server's tools. Tests. Commit.

### Task 5.3: Security review pass
Run the `security-review` skill (or a focused subagent) over the whole MCP surface: DPAPI at rest, no secret in yaml/logs/UI responses, clean-env stdio spawn, OAuth state/PKCE/single-use callback, authz on all endpoints, container never receives secrets. Fix findings. Commit.

### Phase 5 verification + review
- [ ] Full solution test suite green; security review clean; manual end-to-end on all transports + auth modes.

---

## Final delivery (after Phase 5)
- [ ] Full solution build + test green (warning-clean).
- [ ] Bump version; rebuild agent image (`Build-AgentImage.ps1 -BumpVersion`); `docker compose up -d --force-recreate cortex-agent`; rebuild + force-install MSIX (`Build-Launcher.ps1`); verify Bridge + agent `/health` + a real MCP tool round-trip.
- [ ] Merge `feat/mcp-plugin-system` → `main`; push origin.
- [ ] Tell the user it's ready to test, with a short "how to add your first MCP server" note.

## Self-review (plan vs spec)
- Spec coverage: both transports ✅ (2,3), all 3 auth modes ✅ (2.5 apiKey/none, 3 oauth), flatten + namespacing ✅ (1.4/1.5/2.3), agent credential-free ✅ (all auth in Bridge), Web UI ✅ (4), per-tenant (config is per-tenant via BridgeConfig/TenantRouter — carried through Phase 2/4; single default tenant in practice), telemetry ✅ (5.1), encryption at rest ✅ (DPAPI 2.5/3.3), security ✅ (5.3), kill-switch ✅ (5.2), tools-only ✅ (resources/prompts out of scope). 
- Phasing: each phase is independently shippable and testable (Phase 1 with fakes, 2 with real stdio/http, 3 OAuth, 4 UI, 5 hardening).
- Open items deferred to implementer notes (SDK API surface in 2.1; reuse coda spawn in 2.4) — flagged inline, not placeholders.
