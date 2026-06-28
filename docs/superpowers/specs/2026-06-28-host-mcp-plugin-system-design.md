# Host MCP Plugin System — Design Spec

> Status: **Design approved (brainstorming)** — pending spec review, then implementation plan.
> Date: 2026-06-28 · Branch target: feature branch off `main`.

## Goal

Let the Cortex agent use **arbitrary third-party MCP (Model Context Protocol) servers** — both **local stdio** servers (npx/uvx/binary) and **remote HTTP/SSE** servers — as if their tools were native agent tools. All server processes, network egress, and **authentication run on the Windows host (the Bridge)**; the agent (in the Docker container) stays **credential-free** and simply calls tools by name. Everything is **configurable in the host Web UI**.

## Core principle — the Bridge is the MCP host *and* the credential boundary

The agent never sees an MCP credential. It only ever sees tool **names + JSON schemas** and calls them by name. The Bridge attaches the correct auth at the moment it talks to the MCP server — entirely on the host. The agent in the container is a **pure tool consumer**; the Bridge is a **thin transport + auth proxy** that owns everything the sandboxed container cannot do: spawning processes, network egress, browser-based OAuth consent, encrypted token storage, and token refresh.

This extends Cortex's existing security model (container holds no stored secrets; Bridge owns DPAPI secret storage; credentials are pushed/held in memory only) — it is not a new trust model.

## Non-goals (v1)

- MCP **resources** and **prompts** primitives (tools only in v1; resources/prompts are a future item).
- MCP **sampling** (server-initiated LLM calls back into the agent) — future.
- A "gateway / meta-tool" discovery model (`mcp_list_servers`/`mcp_call`) — explicitly rejected (see Agent-facing model); the on-demand tool gate is the scale escape-hatch if ever needed.
- Reworking Coda/coding-CLI onto MCP — Coda stays bespoke (its streaming/steering/permission semantics don't map to MCP's request/response model). The MCP layer is designed cleanly enough that Coda *could* migrate later, but v1 does not touch it.

---

## Architecture

```
┌─────────────────────────── Docker container ───────────────────────────┐
│ Agent Host                                                              │
│   ToolRegistry  ── flat, namespaced MCP proxy tools (mcp__srv__tool)    │
│        │  call mcp__github__create_issue(args)                          │
│        ▼                                                                │
│   McpProxyTool ──► IMcpGateway (SignalR client proxy)                   │
└────────────────────────────────│───────────────────────────────────────┘
                                  │  SignalR hub (existing IAgentHub)
                                  │  InvokeMcpToolAsync(server, tool, argsJson)
                                  │  + push: McpToolCatalog (names+schemas)
┌─────────────────────────────────▼─────────── Windows host ──────────────┐
│ Bridge                                                                  │
│   McpHostService ── manages all servers; routes tools/call             │
│     ├─ stdio client  ── spawns process (env secrets from DPAPI)         │
│     ├─ http  client  ── HttpClient + auth handler                       │
│   McpAuthManager  ── none | apiKey | OAuth2.1(auto-discover, DCR, PKCE) │
│     └─ DPAPI secret storage + TokenRefresh background service           │
│   McpConfigStore  ── cortex.yml `mcpServers:` (non-secret)              │
│   Web UI: "MCP Servers" page (CRUD, auth, enable, allow-list, status)  │
│                                                                         │
│   ▼ talks to ▼                                                          │
│   local stdio MCP processes   ·   remote HTTP/SSE MCP endpoints          │
└─────────────────────────────────────────────────────────────────────────┘
```

The Bridge uses the official **`ModelContextProtocol` C# SDK** as the MCP *client* (handshake, `tools/list`, `tools/call`, transports). We do not hand-roll the protocol.

### Components

| Component | Process | Responsibility |
|---|---|---|
| `McpConfigStore` | Bridge | Read/write the `mcpServers:` block in `cortex.yml` (non-secret fields). Per-tenant. |
| `McpAuthManager` | Bridge | Resolve credentials per server: `none` / `apiKey` (static) / `oauth` (auto-discovered). Owns DPAPI secret read/write + OAuth flow + refresh. |
| `McpServerConnection` | Bridge | One live connection to one server (stdio or http). Handshake, cache `tools/list`, execute `tools/call`, surface status/errors, reconnect. |
| `McpHostService` | Bridge | Owns the set of `McpServerConnection`s; reconciles them against config + enable flags (start/stop, like the sidecar lifecycles); aggregates the **tool catalog**; routes `InvokeMcpToolAsync` to the right connection. |
| `McpCatalogPusher` | Bridge | Pushes the namespaced tool catalog (names + descriptions + JSON schemas) to the agent over SignalR; re-pushes on any change. Mirrors `CredentialsPusher`. |
| `IMcpGateway` / `SignalRMcpGateway` | Agent | Agent-side proxy: given (server, tool, argsJson) invokes the Bridge over the hub and returns the result. Mirrors `SignalRExternalAgent`. |
| `McpProxyTool` | Agent | A single dynamic `IAgentTool` implementation instantiated per discovered MCP tool; its `Name`/`Description`/`ParametersSchema` come from the pushed catalog; `ExecuteAsync` calls `IMcpGateway`. |
| `McpToolGate` | Agent | Optional `IConversationToolGate` to hide a server's tools until relevant / when disabled (reuses the existing gate mechanism). |
| Web UI "MCP Servers" page | Bridge | CRUD + auth + enable toggles + per-tool allow-list + live status. |

---

## Agent-facing model: flatten with server-prefixed names

Every **enabled** MCP tool is registered in the agent's `ToolRegistry` as a **top-level, native tool named `mcp__<server>__<tool>`**, with the real JSON schema from `tools/list`. The agent cannot distinguish an MCP tool from a built-in one.

- The `mcp__<server>__` **prefix is the catalog** — the model perceives which server owns which tool natively, with no separate "list servers" step.
- This matches how mainstream harnesses expose MCP (highest tool-selection + argument accuracy; in-distribution for the model).
- **Rejected:** the gateway/meta-tool model (`mcp_list_servers`→`mcp_list_tools`→`mcp_call`). It adds a discovery round-trip and strips the enforced schema (free-form args → more errors). It only pays off at dozens-of-servers scale; the per-conversation `IConversationToolGate` is the escape hatch for that future case.

**Keeping the list lean** (so flattening scales for a curated set):
1. **Master MCP enable** toggle + **per-server enable** toggle (live, same pattern as the shipped subsystem toggles).
2. **Per-server tool allow-list** — expose only chosen tools from a chatty server.
3. (Optional, future) on-demand gating via `IConversationToolGate`.

### Namespacing & collisions
- Tool name = `mcp__<serverKey>__<toolName>`, lowercased server key (`[a-z0-9_-]`), validated unique at config time.
- The built-in `VoiceOnlyToolGate`/`MemoryDisabledToolGate` precedent shows `IConversationToolGate` composes cleanly; the MCP tools simply add to the registry's tool list.
- If two servers expose the same tool name, the server prefix disambiguates; if two *server keys* collide, config save is rejected.

### Tool-call path
1. LLM emits `mcp__github__create_issue({...})`.
2. `ToolRegistry.ExecuteAsync` dispatches to the matching `McpProxyTool`.
3. `McpProxyTool` → `IMcpGateway.InvokeAsync(server="github", tool="create_issue", argsJson)` → SignalR `InvokeMcpToolAsync` on the Bridge.
4. Bridge `McpHostService` finds the `McpServerConnection` for `github`, calls `tools/call` **with auth already attached on the host**.
5. MCP result (content blocks: text/json/image) → serialized back over SignalR → `McpProxyTool` returns it to the LLM as the tool result.
6. Errors (server down, auth-needed, tool error) map to a structured tool-error result the LLM can read (`{ "error": "...", "needsAuth": true }`), never an unhandled exception.

### Result mapping
- MCP `content` array → flattened to the agent tool-result string (text concatenated; JSON kept as JSON; images surfaced via the existing image-handling path if present, else described). Large outputs use the existing `ToolRegistry` truncation.

---

## Auth — host-side `McpAuthManager`

> **How do we know how to authenticate?** It depends entirely on transport, and the two cases are fundamentally different:
> - **HTTP servers are self-describing.** Auth is *discovered at connect time* via a spec-defined handshake: connect with no creds → server returns `401` + `WWW-Authenticate` pointing at `/.well-known/oauth-protected-resource` → that names the authorization server + endpoints + scopes. The server **tells us exactly how to authenticate**; we don't pre-know or guess.
> - **stdio servers carry no auth in the protocol.** A spawned process cannot announce "I need `GITHUB_TOKEN`." So stdio auth is **configured by the user when the server is added** (env/args per the server's docs, secrets → DPAPI) and **injected at spawn** — identical to how every MCP client (Claude Desktop/Code, Cursor) handles stdio servers. Where a stdio server runs its **own** interactive OAuth on first run (opens a browser, caches its own token in its home dir), the host simply *enables* it: we spawn with browser access + a persistent working dir, the user consents, the server self-manages its token (the Bridge does not). This case **cannot work in the container** at all — a core reason the MCP host is on the host.
>
> Net: **"Auto" discovery applies to HTTP only.** For stdio, a **"Test connect"** action spawns the server and surfaces its own stderr (which in practice reports the missing variable), turning the server's own error into configuration guidance.

Three modes, **configurable per server**, with **auto-discovery for HTTP**:

### Mode `none`
Public server. Nothing attached.

### Mode `apiKey` (static secret)
- User enters an API key / PAT / bearer once in the Web UI → stored in **DPAPI**, referenced from `cortex.yml` by a key id (never the value).
- **stdio**: injected as an **env var** into the spawned process (name configurable, e.g. `GITHUB_TOKEN`). Never logged.
- **http**: attached as `Authorization: Bearer <token>` (or a configurable custom header).

### Mode `oauth` (OAuth 2.1, auto-discovered for HTTP)
For remote HTTP servers implementing the MCP Authorization spec:
1. Bridge connects; server responds **`401` + `WWW-Authenticate`** → resource lives behind OAuth.
2. Bridge fetches **`/.well-known/oauth-protected-resource`** → authorization server(s).
3. Bridge fetches **authorization-server metadata** (`/.well-known/oauth-authorization-server` or OIDC `/.well-known/openid-configuration`) → endpoints, scopes, supported flows.
4. Bridge does **Dynamic Client Registration (RFC 7591)** if no client is pre-configured — *no manual client-id needed*.
5. Bridge runs **Authorization Code + PKCE** via the **system browser**; the user consents.
6. Bridge catches the redirect on a **loopback callback** it hosts (`http://127.0.0.1:5080/mcp/oauth/callback`), exchanges code → **access + refresh tokens**, stores them in **DPAPI**.
7. **Token refresh** rides the Bridge's existing token-refresh background service; on a `401` from the server mid-session, transparent refresh-and-retry.
- **Manual override**: if a server doesn't support DCR / discovery, the UI lets the user paste a client-id/secret and/or token endpoint; the same flow then runs.

### Discovery UX
The Web UI auth selector defaults to **"Auto"** for HTTP servers:
- public → connects immediately;
- static-bearer needed → UI prompts "paste token";
- OAuth needed → UI shows **"Connect"** → browser consent → `connected`.

stdio servers have **no discovery standard**, so their auth is always **manual env/args** (with DPAPI-backed secret entry).

### Security model
MCP servers are arbitrary host processes / endpoints — a deliberate, **opt-in** extension of trust beyond the container sandbox. Mitigations:
- Every server is **explicitly added + enabled** by the user; nothing runs implicitly.
- **Credentials never leave the host** (DPAPI at rest, in-memory in use, injected only at the host↔server boundary).
- **Master kill-switch** + per-server enable instantly remove all/one server's tools (live, no restart).
- Tool calls are **audited/logged** (server, tool, outcome — never secret values).
- stdio processes spawned with the **minimum env** needed (only the configured secrets + a clean base), not the Bridge's full environment.

---

## Configuration

### `cortex.yml` (non-secret), per tenant
```yaml
mcp:
  enabled: true          # master switch (default true)
mcpServers:
  - key: github          # unique; used in tool prefix mcp__github__*
    enabled: true
    transport: http       # http | stdio
    url: https://api.githubcopilot.com/mcp/   # http only
    auth: auto            # auto | none | apiKey | oauth
    apiKeyHeader: Authorization     # apiKey+http only (default Authorization: Bearer)
    secretRef: mcp/github/apikey    # DPAPI key id (value never in yaml)
    toolAllowList: [create_issue, list_prs, get_file]   # empty/absent = all
  - key: filesystem
    enabled: true
    transport: stdio
    command: npx
    args: ["-y", "@modelcontextprotocol/server-filesystem", "/app/shared"]
    auth: none
    env:                  # stdio env; secret values are secretRefs, not literals
      SOME_TOKEN: { secretRef: mcp/filesystem/token }
```
- **Secrets** (API keys, OAuth client secrets, access/refresh tokens) live only in **DPAPI**, addressed by `secretRef`. The YAML holds references, never values.
- All flags default to preserving current behavior: `mcp.enabled` default `true`, but with **zero servers configured** the agent's tool list is unchanged.

### Web UI — "MCP Servers" page (host, `:5080`, behind existing auth)
- **Server list** with status badge per server: `connected` · `error` · `needs login` · `disabled`, tool count, last error.
- **Add / Edit server**: key; transport (stdio: command/args/env; http: url + optional custom header); **auth = Auto/None/API-key/OAuth**; secret entry (→ DPAPI).
- **Enable toggle** (live) + **master MCP toggle** (live) — same instant kill-switch pattern as the shipped subsystem toggles.
- **"Connect" button** for OAuth (drives the host browser flow; shows `needs login` → `connected`).
- **Per-tool allow-list**: checkboxes over discovered tools.
- **Test/refresh** button: re-handshake + re-list tools.
- Saving writes config (yaml) + secrets (DPAPI), then **live-pushes the updated tool catalog to the agent over SignalR** — no restart; tools appear/disappear immediately.

---

## SignalR contract additions

On `IAgentHub` / `IAgentHubClient` (the existing typed hub contracts):
- **Bridge → Agent (client method):** `UpdateMcpToolCatalog(McpToolCatalog catalog)` — full namespaced catalog (per push, replace-all for simplicity); the agent rebuilds its MCP proxy tool set.
- **Agent → Bridge (hub method):** `Task<McpToolResult> InvokeMcpTool(McpToolInvocation call)` where `McpToolInvocation { string ServerKey; string ToolName; string ArgumentsJson; string ConversationId; }` and `McpToolResult { bool IsError; string ContentJson; bool NeedsAuth; string? Error; }`.
- DTOs in `Contracts/Hub/HubTypes.cs`: `McpToolCatalog`, `McpToolDefinition { ServerKey, ToolName, FullName, Description, ParametersSchemaJson }`, `McpToolInvocation`, `McpToolResult`.

Tenant routing reuses the existing `TenantRouter` — the catalog is per-tenant; invocations resolve to the calling tenant's connections.

---

## Lifecycle & resilience

- `McpHostService` **reconciles** connections against config + enable flags on startup and on any config change (start-if-should-run, stop-if-disabled) — same shape as `SttSidecarLifecycle`/`EmbeddingsSidecarLifecycle`.
- A crashed stdio process / dropped http connection is **auto-restarted with backoff**; while down, its tools are marked unavailable and `InvokeMcpTool` returns a structured error (not an exception).
- `needs login` (OAuth not yet completed / refresh failed) is a first-class status surfaced to the Web UI and returned as `NeedsAuth=true` on invocation so the agent can tell the user "that integration needs you to connect it."
- On agent reconnect, the Bridge re-pushes the full catalog (same as it re-pushes credentials today).

---

## Error handling

| Failure | Behavior |
|---|---|
| Server unreachable / crashed | Tools marked unavailable; `InvokeMcpTool` → `{IsError, Error}`; auto-reconnect with backoff; UI shows `error`. |
| Auth missing / refresh failed | `NeedsAuth=true`; UI shows `needs login` + Connect; agent gets a readable "needs auth" result. |
| Tool throws (MCP `isError`) | Mapped to a structured tool-error result the LLM reads; not a hub exception. |
| Bad/oversized args or result | Args validated against schema before send where possible; results truncated via existing `ToolRegistry` truncation. |
| Duplicate server key / tool collision | Rejected at config-save time with a clear UI error. |

---

## Testing strategy

- **Unit (Bridge):** `McpAuthManager` mode resolution (none/apiKey/oauth selection, env vs header injection, secretRef → DPAPI lookup); OAuth metadata-discovery parser (401/`WWW-Authenticate` → endpoints); catalog namespacing + allow-list filtering; config (de)serialization incl. secret redaction (no secret ever in yaml output).
- **Unit (Agent):** `McpProxyTool` builds correct `IAgentTool` from a catalog entry; `ToolRegistry` registers/removes MCP tools on catalog push; collision handling; `McpToolGate` hide/show.
- **Unit (contracts):** DTO round-trip; default-on flags.
- **Integration:** an in-process **fake MCP server** (stdio + http) exercising handshake → list → call → result, and a 401→OAuth-metadata discovery path with a stub authorization server (no real browser; the loopback exchange is driven directly).
- **Manual:** add a real stdio server (filesystem) and a real OAuth HTTP server via the Web UI; confirm browser consent, tool appears in agent, call round-trips, kill-switch removes it live.
- Pattern throughout: extract pure helpers (auth-mode resolver, namespacer, discovery parser, allow-list filter) and unit-test them — matching the existing codebase convention.

---

## Build phasing (for the implementation plan)

Single design; the plan sequences it so each phase is shippable:
1. **Contracts + agent proxy + flatten** — DTOs, `IMcpGateway`/`SignalRMcpGateway`, `McpProxyTool`, catalog push/registration, namespacing. (Drive with a fake catalog; no real servers yet.)
2. **Bridge MCP host (stdio + http) + `none`/`apiKey` auth** — `McpServerConnection`, `McpHostService`, `McpConfigStore`, DPAPI secrets, reconcile lifecycle, end-to-end tool call against a fake then real server.
3. **OAuth 2.1 auto-discovery** — `McpAuthManager` OAuth: metadata discovery, DCR, PKCE, loopback callback, DPAPI token storage, refresh integration.
4. **Web UI "MCP Servers" page** — CRUD, status, enable toggles, Connect button, per-tool allow-list, live push.
5. **Hardening** — kill-switch, audit logging, reconnect/backoff, collision validation, `IConversationToolGate` opt-in.

---

## Open questions (resolve during planning, none block the design)
- Exact `ModelContextProtocol` C# SDK surface for HTTP-OAuth (how much of discovery/DCR it does vs. we implement) — confirm against the SDK version pinned in the plan.
- Whether the loopback OAuth callback should be a dedicated port vs. piggyback on the existing `:5080` Kestrel (default: piggyback on `:5080` to avoid new firewall surface).
- Image/binary MCP content mapping to the agent's existing media/image path — confirm shape in Phase 1.
