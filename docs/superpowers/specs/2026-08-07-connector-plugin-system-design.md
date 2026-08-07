# Connector Plugin System — Design Spec

**Status:** implemented (phases 1–7)

## Goal

Let third-party developers build Cortex channels (connectors) out-of-tree, in any
language, without modifying the cortex-agent repository. Today every channel
(WebChat, Discord, Voice, CloudMessaging) is an in-tree C# project wired into
`Program.cs`, and `ChannelType` is a closed enum — so a new messaging surface is
impossible without a fork.

After this change, a connector is an external process that dials in over a
documented WebSocket + JSON protocol, gets approved once through a pairing flow,
and from then on behaves like any first-party channel: it appears in
`ChannelManager`, routes through `HubMessageDispatcher`, supports streaming, and
is a valid target for `transfer_session`.

## Core principle — the Bridge is the connector host and the credential boundary

This mirrors the MCP plugin system (see `docs/mcp-plugin-system.md`):

- The **agent never sees a connector credential**, and never talks to a connector.
  It sends and receives `InboundMessage` / `OutboundMessage` over the existing
  `/hub/agent` SignalR connection exactly as it does today.
- The **Bridge owns everything the container cannot safely do**: accepting
  connector sockets, pairing approval, DPAPI token storage, revocation, and rate
  limiting.

Connectors are never reached from inside the container. Only message content
crosses the container boundary — never a connector token.

### Direction differs from MCP — connectors dial in

MCP servers are spawned by the Bridge (stdio) or called out to (HTTP), because
tools are invoked on demand. Connectors are the inverse: they are long-lived
message *sources* that push inbound traffic, and some (a terminal CLI) are
launched by the user, not by the Bridge. The Bridge therefore never spawns a
connector; connectors dial in and authenticate.

## Non-goals (v1)

- **No remote connectors.** The endpoint binds loopback only.
- **No in-process plugin assemblies.** Loading third-party DLLs into the Bridge
  would run untrusted code with Bridge privileges and full access to DPAPI
  secrets. Rejected on security grounds.
- **No connector-provided tools.** A connector is a messaging surface only.
- **No marketplace/distribution.** Authors ship their own binaries.
- **No media/attachments in v1.** The capability flag is negotiated but the
  Bridge rejects attachments.

## Architecture

### The agent requires zero changes

Each connected connector is projected into a `PluginChannel` instance
implementing the existing `IChannel` (plus `IChannelWithStreaming` when
streaming is negotiated) and registered with `ChannelManager`. From
`HubMessageDispatcher`'s perspective it is indistinguishable from
`DiscordChannel` or `WebChatChannel`.

All new code is Bridge-side. The shared-Contracts changes are deliberately minimal and all
backward compatible, which matters because `Cortex.Contained.Contracts` is compiled into the
container image:

- `ChannelType.Plugin = 7` — a new enum member, so existing values are unchanged.
- `OutboundMessage.Timestamp` — a new nullable, non-required property carrying the timestamp the
  agent recorded, so a channel exposing a replay cursor quotes the agent's clock rather than its
  own. A container built without it simply yields `null`, which the Bridge falls back from.
- `PluginChannelId` — the channel-id format and validation rules, shared because both the Bridge
  and the Agent Host need them and duplicating the validation would be a security risk.
- `ChannelCapabilities.MaxMessageLength` — documentation only, clarifying that the unit is UTF-16
  code units rather than bytes.

### Identity model

```csharp
public enum ChannelType
{
    WebChat = 0, Teams = 2, Telegram = 3, Voice = 4,
    Discord = 5, CloudMessaging = 6,
    Plugin = 7,   // real identity carried by ChannelId
}
```

Concrete identity lives in the channel id:

```
plugin:<key>:<instance>        e.g.  plugin:terminal:default
```

- `key` — connector-declared, stable, unique per connector type.
- `instance` — allows one connector type to serve multiple logical channels.

## Wire protocol

**Transport:** WebSocket at `ws://127.0.0.1:5080/connector`, loopback only.
Chosen over SignalR deliberately: a raw WebSocket + JSON is implementable in Go,
Rust, Python, or Node in a few dozen lines with no SDK.

**Framing:** one JSON object per WebSocket text message. Every frame is
`{"type": …, "payload": {…}}`.

### Frame type summary

| Direction | Type | Purpose |
|---|---|---|
| C→B | `hello` | attach, declare identity + capabilities, present token, request replay |
| C→B | `inbound` | a user message |
| C→B | `abort` | cancel in-flight generation |
| C→B | `pong` | liveness response |
| B→C | `pairing_required` / `paired` / `pairing_denied` | pairing handshake |
| B→C | `ready` | attach accepted |
| B→C | `typing` / `stream` / `outbound` | agent response lifecycle |
| B→C | `error` | protocol or policy rejection |
| B→C | `ping` | liveness probe |

See `docs/connector-plugin-system.md` for the full author-facing payload
reference.

## Offline connectors and replay

A paired connector that is not running is the normal case. Such a channel sits
at `ChannelStatus.Disconnected`. Outbound traffic can still be generated for it
(a scheduled task firing, or `transfer_session`), so:

- Outbound messages are persisted to the existing message history store.
- On attach the connector supplies `sinceCursor` in `hello`, and the Bridge
  replays everything newer before entering steady state, reporting `replayCount`
  in `ready`.
- A connector that omits `sinceCursor` gets no replay.

Replay is bounded by a configurable cap (default 100 messages / 24h).

## Configuration

Paired connectors live in a DPAPI-backed store, not in YAML. `cortex.yml`
carries only policy, mirroring `mcp.enabled`:

```yaml
connectors:
  enabled: true            # master kill-switch; drops all plugin channels live
  requireApproval: true    # false only for unattended/dev setups
  maxConnectors: 16
  replay:
    maxMessages: 100
    maxAge: 24:00:00
  limits:
    maxFrameBytes: 1048576
    maxMessagesPerMinute: 120
```

## Security model

- **Loopback only.** The endpoint refuses non-loopback peers.
- **Pairing code must match in both places** — shown by the connector *and* in
  the Web UI. A rogue local process cannot pair silently.
- **Pairing requests are rate-limited and single-flight**, and codes expire
  (5 min default).
- **Tokens are DPAPI-encrypted**, scoped to the Windows user, individually
  revocable, and compared in constant time.
- **The master switch and per-connector toggle drop channels live.**
- **Input is bounded**: `maxFrameBytes`, `maxMessageLength` (negotiated, capped
  by the Bridge), and per-connector message rate limits.
- **The agent never receives connector credentials**; tokens are never logged or
  returned by the REST API.
- **`displayName` is untrusted** and is escaped wherever the Web UI renders it.

## Resolved design questions

1. **One socket per instance.** A `hello` declares exactly one
   `key`+`instanceId` pair. Multiple instances = multiple sockets.
2. **`transfer_session` may target any paired connector**, attached or not.
   Replay covers the offline case, so a detached target is not an error.
3. **Logging is sufficient for v1** — no live protocol trace view in the Web UI.
   Connector frames are logged at Debug with payload text redacted.
4. **`IsGroup` / `ThreadId` are reserved for first-party channels in v1.** The
   Bridge ignores them if a connector sets them.

## Deviations from the original proposal

Discovered during implementation; the design intent is unchanged.

- **`GET /api/messages/{channelId}` does not exist.** The Bridge is a stateless
  router with no message store — all persistence lives in the Agent Host and is
  reached over SignalR via `IHistoryHub.GetMessages(conversationId, limit,
  offset)`. Replay is implemented against that interface.
- **Messages carry no cursor field.** The replay cursor is the message
  timestamp serialised as a round-trip ISO-8601 string (`o` format); replay
  returns messages strictly newer than `sinceCursor`.
- **`ChannelManager` had no `UnregisterChannel`.** One was added so plugin
  channels can be dropped live by the kill-switch and by revocation.

## Build phasing

1. `ChannelType.Plugin` + `PluginChannel` + `ConnectorHost` skeleton;
   hello/ready/inbound/outbound only, pairing stubbed to auto-approve.
2. Pairing service, DPAPI token store, revocation.
3. Streaming, typing, abort.
4. Replay + history integration.
5. Web UI Connectors page.
6. Limits, rate limiting, hardening.
7. Author documentation + a minimal example connector.
