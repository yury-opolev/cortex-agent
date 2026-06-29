# Cloud Web/Voice Frontend for Cortex — Design Spec

*Created: 2026-06-29*
*Status: Design proposal (spec only — no implementation yet)*
*Author: brainstormed with Claude*

## 1. Summary

Extract the local web chat into a **separate, cloud-deployable frontend** so the user
can talk to their own Cortex agent (Emma/Aden) from anywhere — phone or laptop, over a
public URL — instead of having to be at the PC or in Discord.

The agent itself (LLM loop, memory, tools, GPU TTS/STT) **stays on the home machine**.
The cloud hosts only a thin frontend + a relay; the home Bridge connects **outbound** to
that relay, exactly the way the Discord bot connects outbound to Discord today. No inbound
ports are opened on the home machine, and no agent data lives in the cloud at rest.

**v1 scope = text chat.** Browser **voice is a documented Phase 2** (Section 9) because it
is net-new work (mic capture + streaming STT/TTS over the web), not an extraction.

## 2. Decisions (locked during brainstorming)

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | **Topology: cloud frontend → home agent** | Lowest lift; keeps GPU, memory, and data on the home box; no recurring cloud GPU cost. Mirrors the existing Discord pattern. |
| D2 | **Audience: single user (the owner), single agent** | Private remote front door. Simplest auth, no tenant routing, reuses the existing single agent. The multi-tenant machinery (docs/multi-tenant-architecture.md) is explicitly out of scope. |
| D3 | **Home → cloud link is OUTBOUND from home** | The home Bridge dials the cloud relay and holds a persistent connection. No inbound firewall holes; the home machine is never directly exposed. |
| D4 | **v1 = text; voice = Phase 2** | Voice is substantial new work; text chat extraction delivers a usable remote interface fast. |
| D5 | **Relay is a dumb, stateless forwarder** | It bridges browser ↔ home and authenticates both ends. It never stores conversation content, memory, or credentials. |

## 3. Goals / Non-Goals

### Goals (v1)
- A public HTTPS URL that serves the chat UI (extracted from `Bridge/wwwroot`).
- Authenticated single-user access (only the owner can use it).
- Real-time, streaming text chat with the home agent (same UX as the local web UI:
  streaming chunks, typing indicator, abort/stop, history).
- Home machine never accepts inbound connections; it only dials out to the relay.
- The extracted frontend lives in its **own project/repo**, deployable independently of
  the Windows MSIX.

### Non-Goals (v1)
- Browser voice (Phase 2).
- Multiple users / multi-tenant routing / billing.
- Moving the agent, memory, or speech stack to the cloud.
- Replacing Discord (this runs *alongside* it as another front door).
- Mobile native apps (the web UI is responsive; PWA install is a nice-to-have).

## 4. Current Architecture (what we're extracting from)

The "web chat" today is three fused layers inside the Windows-host **Bridge**:

1. **UI** — `src/Cortex.Contained.Bridge/wwwroot/` — static HTML + vanilla JS + Alpine +
   Bootstrap, using the SignalR JS client (`lib/signalr/signalr.min.js`). Already portable.
2. **Chat backend** — `Cortex.Contained.Channels.WebChat`:
   - `WebChatHub : Hub` mapped at `/hub/webchat` (Program.cs:1054), `.RequireAuthorization()`.
     Browser calls `SendMessage(conversationId, text)`, `GetStatus()`, `AbortGeneration(id)`;
     receives streaming chunks / finalized messages / `OnError`.
   - `WebChatChannel : IChannelWithStreaming` — an `IChannel` adapter with no external
     service. Inbound: `ReceiveFromBrowserAsync(InboundMessage)` raises `MessageReceived`.
     Outbound: events `OutboundMessageReady`, `StreamingUpdateReady`, `StreamingFinalizeReady`,
     `TypingIndicatorReady`.
   - REST: history via `GET /api/messages/webchat-default`; plus memory/settings endpoints.
   - Auth: cookie auth, single admin password from `cortex.yml`; Kestrel bound to
     `WebUi.BindAddress` (`127.0.0.1:5080` today).
3. **The brain** — the Bridge routes inbound messages over its Agent Hub SignalR connection
   to the **Agent Docker container** (`AgentRuntime`: LLM loop, memory, tools), which uses the
   local GPU sidecars (uni-voices TTS, Whisper STT, Ollama embeddings).

**Key reuse seam:** `WebChatChannel` is already a clean boundary — inbound via one method,
outbound via four events. The cloud path plugs into exactly this seam; the agent, routing,
history, and memory are untouched.

## 5. Proposed Architecture (v1)

```
  Phone / Laptop (browser, anywhere)
        │  HTTPS + WebSocket (chat protocol)
        ▼
  ┌──────────────────────────────────────────────┐
  │ CLOUD                                          │
  │  ┌────────────────┐     ┌────────────────────┐ │
  │  │ Static frontend│     │  Relay             │ │
  │  │ (extracted UI) │◄───►│  - browser WS in   │ │
  │  │  Cloudflare    │     │  - home WS in      │ │
  │  │  Pages         │     │  - forwards frames │ │
  │  └────────────────┘     │  - authenticates   │ │
  │   Auth: CF Access        │    both ends       │ │
  │   (single identity)      └─────────▲──────────┘ │
  └────────────────────────────────────┼───────────┘
                                        │ persistent OUTBOUND WS
                                        │ (home dials cloud; strong shared secret/mTLS)
  ┌─────────────────────────────────────┼───────────┐
  │ HOME (Windows host, existing)        │           │
  │  ┌──────────────────────────────┐    │           │
  │  │ Bridge                        │   │           │
  │  │  + NEW: WebRelayConnector ────┘   │           │
  │  │     ▲ dials relay, bridges to     │           │
  │  │     │ WebChatChannel seam         │           │
  │  │  WebChatChannel ──► Agent Hub ──► Agent (Docker)│
  │  └──────────────────────────────┘                │
  └──────────────────────────────────────────────────┘
```

### Data flow (one text turn)
1. Browser authenticates (Cloudflare Access) → opens WS to the relay.
2. Browser sends `{type:"message", conversationId, text}`.
3. Relay validates the session, forwards the frame over the home WS.
4. Home `WebRelayConnector` receives it and calls
   `WebChatChannel.ReceiveFromBrowserAsync(InboundMessage)` — identical to a local browser.
5. Agent streams the reply; `WebChatChannel` raises `StreamingUpdateReady` /
   `StreamingFinalizeReady`; `WebRelayConnector` serializes each event over the home WS.
6. Relay forwards frames to the browser WS; the UI renders streaming text.
7. `AbortGeneration` / `GetStatus` / history travel the same path (history may also be a
   relayed REST-over-WS request, or a separate relayed REST endpoint — see Open Questions).

## 6. Components (each with one clear purpose)

### C1 — Frontend (`cortex-web/frontend`, new project)
- **What:** The extracted static chat UI (HTML/JS/CSS lifted from `Bridge/wwwroot`,
  trimmed to the chat surface for v1; admin/settings pages can come later).
- **Depends on:** the relay's browser-facing WS protocol; Cloudflare Access for auth.
- **Boundary:** pure client; no secrets; talks only to the relay endpoint (configurable
  per environment).
- **Hosting:** Cloudflare Pages (user already runs CF Pages for spacekubegames).

### C2 — Relay (`cortex-web/relay`, new project)
- **What:** A stateless forwarder that pairs one authenticated browser session with the
  home connection and shuttles frames both ways. Mirrors the `WebChatHub` message contract
  to the browser.
- **Depends on:** nothing persistent. Holds in-memory session pairing only.
- **Boundary:** authenticates the home link (shared secret / mTLS) and the browser
  (Cloudflare Access JWT); never inspects or stores message *content*.
- **Hosting candidates:** Cloudflare Worker + Durable Object (hibernatable WebSockets,
  cheap, fits CF stack) **[recommended]**, or a tiny always-on container (Fly.io / Azure
  Container Apps / cheap VPS) running a small .NET or Node relay.

### C3 — Home connector (`WebRelayConnector`, new, in `cortex-agent` Bridge)
- **What:** A long-lived outbound client in the Bridge that dials the relay, authenticates,
  and bridges relay frames to/from the **existing `WebChatChannel` seam**. Reconnects with
  backoff (reuse the resilience pattern already used for the Discord/agent connections).
- **Depends on:** `WebChatChannel` (inbound method + 4 outbound events), the relay URL +
  secret (stored via existing DPAPI secret store / `cortex.yml`).
- **Boundary:** the *only* new code in the home repo; agent, memory, history, routing
  unchanged. Gated by a config flag (off by default).

### C4 — Shared message contract (`cortex-web/contracts`)
- **What:** The small JSON frame schema for browser↔relay↔home (message, stream-chunk,
  finalize, typing, error, abort, status, history-request/response). One source of truth
  shared by frontend, relay, and the home connector.

## 7. Authentication & Security

- **Browser → frontend/relay:** **Cloudflare Access** in front of both, scoped to the
  owner's identity (Google / email OTP for yuri.opolev@gmail.com). Zero passwords to manage;
  the relay trusts the validated Access JWT. (Alternative: an app-level passkey/password if
  CF Access is undesirable.)
- **Home → relay:** strong shared secret or mTLS client cert; the relay rejects any home
  connection that doesn't present it. Secret stored in the existing DPAPI secret store.
- **Transport:** TLS everywhere (CF terminates browser TLS; home↔relay is WSS).
- **Data at rest in cloud:** none. The relay is a forwarder; conversation content, memory,
  and credentials never persist in the cloud.
- **Home exposure:** zero inbound. The home Bridge only dials out. Killing the connector
  flag fully detaches the cloud surface.
- **Abuse bound:** single-identity Access gate means no open attack surface; add a basic
  per-session rate limit at the relay as defense-in-depth.

## 8. Phasing

### Phase 1 — Text chat (this spec's v1)
1. Extract the chat UI into `cortex-web/frontend`; parametrize the backend endpoint.
2. Build the relay (C2) with the browser protocol + home protocol + both auth checks.
3. Build `WebRelayConnector` (C3) in the Bridge against the `WebChatChannel` seam.
4. Wire history (relayed read of the existing `GET /api/messages/...`).
5. Deploy frontend (CF Pages) + relay (CF Worker/DO or small container); put CF Access
   in front; configure the home secret + relay URL.
6. End-to-end: send/stream/abort/history from a phone over the public URL.

### Phase 2 — Browser voice (separate spec later)
See Section 9.

## 9. Phase 2 sketch — Browser voice (NOT in v1)

Goal: talk to the agent by voice in the browser, reusing the home GPU speech stack.

- **Capture:** browser mic → `MediaRecorder`/`AudioWorklet` → Opus/PCM frames over the same
  WS (a new `audio` frame type), with VAD/endpointing client- or home-side.
- **Uplink path:** frames → relay → home; home runs **Whisper STT** (existing
  `WhisperSpeechToText`) → transcript → agent (same `WebChatChannel` seam).
- **Downlink path:** agent text → home **uni-voices TTS** (existing) → audio frames → relay
  → browser playback (Web Audio). Sentence-by-sentence streaming like the Discord
  `VoiceOutPipeline`, with barge-in (stop playback when the user speaks).
- **Reuse:** STT/TTS/barge-in concepts already exist for Discord; the new surface is the
  browser audio I/O + transporting audio frames over the relay instead of Discord's UDP.
- **Why separate:** real-time audio, endpointing, barge-in, and playback are their own
  body of work and risk; phasing keeps v1 shippable.

## 10. Alternatives Considered

| Option | Why not (for v1) |
|--------|------------------|
| **Tunnel the existing Bridge directly** (Cloudflare Tunnel / Tailscale Funnel exposing `127.0.0.1:5080`) | Least new code, but exposes the whole Bridge to the public edge, couples the public surface to the home machine's security/availability, and doesn't give the "separate, independently deployable project" the user asked for. Good *fallback* if the relay proves over-engineered for one user. |
| **Full agent in the cloud** | Rejected in brainstorming (D1): recurring GPU cost, data leaves the machine, GPU speech sidecars need a cloud GPU host. |
| **Browser connects directly to home over the tunnel** (no relay) | Still exposes the home endpoint and ties browser availability to home reachability; the relay cleanly decouples and centralizes auth. |
| **Reuse the multi-tenant Bridge router as-is** | Overkill for one user; that machinery is a design proposal, not built. D2 keeps v1 single-user. |

## 11. Open Questions (to resolve before/within implementation planning)

1. **Relay host:** Cloudflare Worker + Durable Object vs. a small always-on .NET/Node
   container. DO is cheapest and fits CF; a .NET container maximizes code sharing with
   `cortex-agent` contracts. (Lean: Durable Object unless we want to share .NET DTOs.)
2. **History/REST over the relay:** relay REST endpoints, or tunnel REST-over-WS, or have
   the frontend hit a separate relayed read API? (Lean: small relayed REST passthrough for
   `GET /api/messages/...`.)
3. **Repo layout:** new `cortex-web` repo (frontend + relay + contracts) vs. a folder in
   `cortex-agent`. The home `WebRelayConnector` lives in `cortex-agent` regardless.
4. **Protocol:** reuse SignalR end-to-end (browser↔relay↔home) to maximize reuse of the
   existing JS client + hub semantics, or a plain JSON-over-WS protocol that's easier to
   forward through a Worker/DO? (Lean: SignalR browser↔relay if relay is .NET; plain WS if
   relay is a Worker.)
5. **Offline/home-down UX:** what the UI shows when the home connector is disconnected
   (queue, error, "agent offline").
6. **PWA install** for a native-app feel on the phone — nice-to-have, not v1-blocking.

## 12. Out of Scope (explicit)
- Multi-tenant routing, per-user agents, billing, sign-up.
- Cloud-hosted agent/memory/speech.
- Native mobile apps.
- Replacing or modifying the Discord channel.

---

*Next step (when ready): review this spec, resolve the Section 11 open questions, then turn
Phase 1 into an implementation plan via the writing-plans skill. No code until then.*
