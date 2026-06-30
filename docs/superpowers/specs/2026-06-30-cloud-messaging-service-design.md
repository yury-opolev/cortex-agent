# AI Messenger — Cortex Cloud Messaging Service — Design Spec

*Created: 2026-06-30 · Branch: `feature/webchat-cloud-service` · Status: Design proposal (no implementation until approved)*
*Supersedes the single-user `2026-06-29-cloud-web-voice-frontend-design.md` for the multi-user case.*

## 1. Summary

A cloud-hosted, **multi-user messaging service** where human users **and** home Cortex Bridges
both "call in," so a user can chat with *their tenant's* AI agent from anywhere — phone or
laptop — over a public, authenticated URL. It is essentially **a messaging app for humans and
AI**, designed to serve both well.

- **Separate repo** (`ai-messenger`); `cortex-agent` gains **one new outbound channel** that
  dials the service — exactly like the Discord channel dials Discord. The agent, memory,
  history, GPU speech, and routing on the home box are untouched.
- **Hybrid statefulness:** a stateless live relay **plus** a small, short-lived, encrypted
  **outbox** that holds only *undelivered* messages while a recipient (bridge or user) is
  offline, then deletes on delivery. **No long-term conversation history in the cloud.**
- **Auth: Microsoft Entra External ID** — the hosted sign-in page offers **Microsoft · Google ·
  Apple · Email**; the user picks one per sign-in. Identity is unified by **verified email**,
  access is **invite/allowlist** only (the "cheap, only real users" constraint).
- **Hosting: Azure serverless, scale-to-zero** — ≈ **$0 when idle**.
- **Extensible by design:** one typed message envelope. **Text is the MVP; voice and images
  slot in later** over the same transport with no protocol redesign.

## 2. Locked decisions (from brainstorming, 2026-06-30)

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | **Separate `ai-messenger` repo + a new `cortex-agent` outbound channel** (`CloudMessagingChannel`) | Independent Azure deploy/scale/cost lifecycle; clean public-surface security boundary; minimal home footprint (one `IChannel`, config-gated, off by default); reusable across bridges. Mirrors the proven Discord-channel pattern. |
| D2 | **Hybrid: stateless relay + small TTL'd encrypted outbox; no long-term cloud history** | Offline resilience (queue while home is off) without becoming a cloud datastore. Keeps Cortex's "data stays home" ethos, low cost, small attack surface. |
| D3 | **Auth = Entra External ID**, IdPs = Microsoft/Google/Apple/Email, user picks at sign-in, identity unified by **verified email**, **invite/allowlist** | Managed CIAM in the user's existing Microsoft stack; standards OIDC tokens the service validates; no password handling by us; free tier at low MAU. Email-keying lets a person use any provider interchangeably as one account. |
| D4 | **Hosting = Azure serverless, scale-to-zero** | Static Web Apps (frontend), Azure Web PubSub (managed WebSockets), Container Apps (.NET service, scale-to-zero), Table Storage / Cosmos serverless (registry + outbox). ≈ $0 idle; pay ~only when chatting. |
| D5 | **Bridges authenticate via per-bridge Managed Identity** (no shared secrets) | No long-lived bridge secret to leak; Azure-native trust. |
| D6 | **One typed, versioned message envelope; text MVP; voice/images later** | The "extendable" guarantee: new media are new `type`s over the same envelope/transport. |
| D7 | **Multi-bridge-capable from day one; one bridge in the MVP** | Routing keyed by tenant→bridge from the start; we just run one bridge first. |
| D8 | **Home is outbound-only (zero inbound); agent/memory/GPU stay home** | Same security posture as the Discord bot; no firewall holes. |
| D9 | **Service backend = .NET (Container Apps)** | Persistent-connection handling + share a `contracts` library with `cortex-agent`. |

## 3. Goals / Non-Goals

### Goals (MVP = Phase 1)
- Public, authenticated HTTPS/PWA chat UI; users sign in via Entra External ID (provider choice).
- Real-time streaming **text** chat between an invited user and *their tenant's* home agent.
- **Tenant isolation:** a user can only see and reach their own tenant's agent/traffic.
- **Offline outbox:** messages queue (encrypted, TTL'd) when the recipient is offline; deliver on reconnect; then delete.
- Home Bridge connects **outbound only**; no inbound exposure.
- Lives in its own repo, deployable independently of the Windows MSIX; ≈ $0 idle cost.

### Non-Goals (now)
- Browser/real-time **voice** (Phase 3 — envelope is ready for it).
- Long-term conversation **history in the cloud** (history stays home; cloud only holds the offline outbox).
- Open public sign-up / billing (invite-only).
- Moving the agent/memory/GPU speech to the cloud.
- Replacing Discord (runs alongside as another channel).

## 4. Architecture

```
  Humans (browser / installable PWA, anywhere)        Home Bridge(s)  (Windows host, OUTBOUND-only)
     │  sign in: Entra External ID                         │  per-bridge Managed Identity
     │  (Microsoft / Google / Apple / Email)               │  NEW: CloudMessagingChannel : IChannel
     ▼  WSS (typed envelope)                               ▼  WSS (typed envelope)
  ┌──────────────────────── AZURE — repo: ai-messenger ──────────────────────────┐
  │  Static Web Apps (free)         → frontend UI (PWA), MSAL sign-in              │
  │  Azure Web PubSub               → holds ALL WebSockets (users + bridges),      │
  │                                   group-per-tenant fan-out                     │
  │  Container Apps (.NET, →0)       → the service: negotiate/authZ · routing ·    │
  │                                   outbox mgmt · presence · Web PubSub handlers │
  │  Table Storage / Cosmos (svrls)  → registry (users↔tenants↔bridges, allowlist) │
  │                                   + outbox (per-recipient, encrypted, TTL'd)   │
  └───────────────────────────────────────────────────────────────────────────────┘
                                   (agent, memory, history, GPU STT/TTS stay HOME)
```

## 5. Components (each one clear purpose, well-bounded)

1. **Frontend** (`ai-messenger/frontend`, Azure Static Web Apps) — the chat UI (seeded from the
   extracted `Bridge/wwwroot` chat surface, made auth-aware + multi-user; reuses C1 thinking-lane
   & C2 queued-messages work). MSAL for sign-in; talks only to the service's negotiate endpoint
   and Web PubSub. No secrets. PWA-installable.
2. **Web PubSub** (Azure managed) — the transport. Holds every WebSocket (users + bridges).
   Group-per-tenant; access tokens are group-scoped so the transport itself enforces isolation.
3. **Service** (`ai-messenger/service`, .NET on Container Apps, scale-to-zero) — the brain of the
   cloud side: `/negotiate` (validate Entra token → allowlist → resolve tenant → issue a
   group-scoped Web PubSub token), Web PubSub **event handlers** (connect/message/disconnect),
   **routing** (user↔tenant-group↔bridge), **outbox** read/write, and **presence**. Stateless
   except for the registry/outbox stores.
4. **Stores** (Table Storage or Cosmos serverless) — (a) **registry**: users↔tenants, bridges↔the
   tenants they serve, the invite allowlist (keyed by verified email); (b) **outbox**:
   per-recipient queue of undelivered envelopes, encrypted, TTL (e.g. 48 h), deleted on delivery.
5. **Contracts** (`ai-messenger/contracts`) — the versioned wire envelope schema (Section 8),
   shared as a small .NET library by the service and `cortex-agent`'s channel; the frontend uses
   the JSON form.
6. **`CloudMessagingChannel`** (`cortex-agent`, new `IChannel`) — long-lived outbound connector:
   authenticates with the bridge's Managed Identity, connects to Web PubSub, joins its tenants'
   groups, and bridges envelopes ↔ the existing `WebChatChannel` seam (inbound method + 4
   outbound streaming events). Config-gated, off by default; reconnect-with-backoff like the
   Discord/agent connections.
7. **Infra** (`ai-messenger/infra`) — Bicep IaC for all of the above + the Entra External ID
   config; per-environment (staging/prod).

## 6. Data flow

**User connect / authZ:** sign in (Entra External ID, provider of choice) → frontend calls
`POST /negotiate` with the OIDC token → service validates the token, checks the **allowlist by
verified email**, resolves the user's **tenant**, returns a **group-scoped** Web PubSub client
token → frontend connects to Web PubSub and joins its tenant group.

**Bridge connect:** the channel authenticates with the bridge's **Managed Identity** → service
authorizes it and returns a Web PubSub token scoped to **the groups for the tenants this bridge
serves** → bridge connects and joins them; service marks the bridge **present** for those tenants.

**One text turn:** user sends `text` envelope → arrives at the tenant group → the bridge serving
that tenant receives it → `CloudMessagingChannel` → `WebChatChannel.ReceiveFromBrowserAsync` →
agent → reply streams back as `stream-chunk` … `finalize` envelopes the same path to the user's
connection (thinking-lane / typing / abort all ride the envelope).

**Offline:** if the target (bridge or user) is **not present**, the envelope is written to that
recipient's **outbox** (encrypted, TTL'd). On reconnect, the service drains the outbox in order,
delivers, and deletes. Outbox entries past TTL are auto-expired (store TTL).

## 7. Authentication & Security (the priority)

- **Users:** Entra External ID OIDC, validated server-side on every `/negotiate`; **invite/allowlist
  by verified email**; provider choice (MS/Google/Apple/Email) is cosmetic — one normalized token.
- **Bridges:** per-bridge **Managed Identity** — no long-lived shared secret to leak or rotate.
- **Tenant isolation enforced at the transport:** Web PubSub access tokens are **group-scoped**;
  a user/bridge can only publish/subscribe to its own tenant group(s).
- **Transport:** TLS/WSS everywhere; Web PubSub + SWA terminate TLS.
- **Data at rest:** only the outbox + registry; **encrypted** (Azure SSE; consider app-layer
  envelope encryption for outbox payloads), **short TTL**, deleted on delivery. No long-term
  history, no agent memory, no model credentials ever in the cloud.
- **Home posture:** outbound-only; killing the channel's config flag fully detaches the cloud surface.
- **Abuse controls:** per-connection + per-identity rate limiting; allowlist caps the population;
  audit log of connect/auth events (no message content).

## 8. Message envelope (extensibility contract)

One versioned JSON envelope for every frame, browser ↔ Web PubSub ↔ bridge:

```jsonc
{
  "v": 1,
  "type": "text",                 // see below
  "tenantId": "…",
  "conversationId": "…",
  "from": "user" | "agent" | "system",
  "id": "…",                      // message/frame id
  "ts": 1730000000000,
  "payload": { /* type-specific */ }
}
```

- **MVP `type`s:** `text`, `stream-chunk`, `finalize` (carries `isThinking` — reuses the C1 lane),
  `typing`, `control` (`abort` / `status` / `presence`), `error`.
- **Later (no redesign):** `image` (attachment ref/blob), `audio-frame` (Opus/PCM for voice
  calls/messages), `audio-control` (start/stop/barge-in). Voice reuses the home Whisper STT /
  uni-voices TTS exactly as the Discord voice path does; the only new surface is browser mic/
  playback + carrying audio frames over this same transport.

## 9. Cost

≈ **$0 idle.** Static Web Apps free tier; Web PubSub **free tier** (~20 concurrent connections /
20k messages/day — ample for a handful of tenant users); Container Apps **scale-to-zero** (pay per
request/second only while active); Table Storage / Cosmos serverless = pennies for the
registry + tiny outbox. Cost scales with *actual* chatting, matching the constraint.

## 10. Repos & integration

- **New repo `ai-messenger`** (private): `/frontend` (SWA), `/service` (.NET Container App),
  `/contracts` (wire schema), `/infra` (Bicep). Its own CI/CD + Azure deploy.
- **`cortex-agent`**: adds `CloudMessagingChannel` (the connector) + references the `contracts`
  schema. Off by default; verified via the existing channel/`IChannel` plumbing.
- The two communicate **only** via the versioned envelope over Web PubSub — no other coupling.

## 11. Phasing

- **Phase 1 — MVP (build first):** text chat; Entra External ID sign-in (provider choice, allowlist);
  one bridge; tenant routing + group isolation; offline outbox; the `CloudMessagingChannel`;
  end-to-end from a phone. Staging + prod Azure environments via Bicep.
- **Phase 2:** multi-bridge polish, richer presence/typing, **push notifications** (Web Push /
  APNs/FCM) for offline messages, image attachments.
- **Phase 3 — Voice:** `audio-*` envelope types; browser mic capture + playback; reuse home
  Whisper STT + uni-voices TTS + barge-in (mirrors the Discord voice pipeline). Voice messages
  first, then live voice calls.

## 12. Open questions (resolve during planning)

1. **Store choice:** Table Storage (cheapest, simplest) vs. Cosmos serverless (richer queries,
   TTL native, slightly pricier) for registry + outbox. (Lean: Table Storage for the outbox; the
   registry is tiny either way.)
2. **Web PubSub event model:** event handlers as Container Apps HTTP webhooks vs. the server SDK
   holding a control connection. (Lean: SDK in the Container App for lower latency + simpler state.)
3. **Outbox TTL** exact value (24 h? 48 h? 7 d?) and max queue depth per recipient.
4. **Tenant ↔ user provisioning UX:** how invites are issued/managed (admin UI in the frontend vs.
   a CLI/Bridge command vs. config) — MVP can be allowlist-by-config; an admin UI is Phase 2.
5. **Frontend reuse vs. rebuild:** lift `Bridge/wwwroot` chat as-is (fastest) vs. a fresh SPA
   (cleaner multi-user/auth). (Lean: lift + adapt for MVP; it already has C1/C2.)
6. **Custom domain / naming** for the public app (e.g. on an existing Azure DNS zone).

## 13. Out of scope
- Cloud-hosted agent/memory/speech; long-term cloud history; open sign-up/billing; native mobile
  apps (PWA covers it); changes to the Discord channel.

---

*Next step (when approved): resolve §12 with a quick pass, then turn Phase 1 into an implementation
plan via the writing-plans skill, and scaffold the `ai-messenger` repo. No project code until then.*
