# Configurable GitHub Host + OAuth Client ID for Copilot Setup — Design Spec

*Created: 2026-06-29*
*Status: Design proposal (spec only — no implementation yet)*
*Author: brainstormed with Claude*

## 1. Summary

Let the first-run setup wizard **specify the GitHub host and the OAuth App client ID**
for the GitHub Copilot provider, both **defaulting to the current public values** when left
blank. Plus: **surface GitHub's real error** instead of a bare "400", and **retry only
genuinely transient failures** (not 400s).

**Motivation:** the owner has a *personal* `github.com` Copilot subscription **and** a work
**GitHub Enterprise (GHE)** host. Today every GitHub host is hardcoded to public
`github.com` / `api.githubcopilot.com`, so the wizard can only ever target public GitHub —
it cannot authenticate against the work GHE. Making host + client ID configurable lets one
build target either, chosen at setup time.

This consolidates the prior proposal's items **#1 (surface errors)**, **#3 (custom OAuth
client ID)**, and **#4 (GHE host)** into one coherent feature, plus a corrected **#2 (retry)**
and a **selectable OAuth flow** (device code | authorization code + PKCE) — because EMU/SSO
enterprises often disable device flow.

## 2. Decisions (locked during brainstorming)

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | **Configurable GitHub auth host + OAuth client ID, set at setup; blank → current public defaults** | Owner needs to target either public `github.com` or a work GHE from the same build. Backward-compatible: existing setups behave identically. |
| D2 | **Auth host and inference host are separate concerns; the wizard configures only the AUTH host** | Verified in code: the configurable GitHub host governs auth only (device/auth-code + the OAuth→Copilot-bearer exchange, done Bridge-side). Inference goes to the Copilot API host, which already exists as `Credential.BaseUrl` (default `https://api.githubcopilot.com`, `OpenAiCompatibleApiClient.cs:394-400`). For personal Copilot the API host is always the public default; for enterprise it can be a proxy and is best read from the token-exchange response's `endpoints.api` — NOT typed by the user. So we do NOT add a separate "Copilot API host" wizard field. |
| D6 | **Selectable OAuth flow: device code (default) or authorization-code + PKCE** | EMU/SSO enterprises frequently DISABLE device flow (`device_flow_disabled`), making auth-code the only working path against a GHE. Cortex already implements auth-code+PKCE for Anthropic (`/api/setup/anthropic-auth`, `GenerateAnthropicPkce`, `BuildAnthropicAuthUrl`) — generalize that pattern for GitHub/GHE. |
| D3 | **400 (and other definitive 4xx) = terminal; retry only 5xx + transient network/timeout** | Owner's explicit call. A 400 means a real client/config error; surface it (D4) rather than hammering it. The observed transient 400 is rare and better handled by a clear error + manual retry than auto-retrying a "bad request". |
| D4 | **Surface GitHub's actual error body** on failure | Today `EnsureSuccessStatusCode()` discards the JSON body, so the wizard shows a causeless "400 Bad Request". The body carries the actionable `error` / `error_description`. |
| D5 | **Host config must also reach the agent runtime + token-refresh path** | The wizard alone is insufficient — the Agent Host LLM client and the Copilot token-refresh both hardcode `api.githubcopilot.com`. A setup that the runtime ignores would only half-work. |

## 3. Goals / Non-Goals

### Goals
- Wizard exposes an **OAuth flow selector** (device code | authorization code + PKCE) plus
  optional **GitHub Host** and **OAuth Client ID** fields (under an "Advanced" disclosure);
  blank uses today's public defaults. (No "Copilot API host" field — the inference host is the
  existing `BaseUrl`, auto-set from the token exchange.)
- The device-code, token-exchange, models-list, **runtime LLM calls**, and **token refresh**
  all honor the configured hosts/client ID.
- Failed device-flow init/poll shows GitHub's real `error_description` in the wizard.
- Transient (5xx/network) device-code failures retry briefly; 400 reports immediately.
- Existing public-`github.com` setups are byte-for-byte unaffected when the fields are blank.

### Non-Goals
- Auto-detecting the GHE host or EMU identity (user supplies it).
- Pre-flight "test connection" (prior #5) — optional, deferred (Section 8).
- Any provider other than `github-copilot-api`.
- Solving enterprise *entitlement* (whether the owner's GHE grants a Copilot license to their
  EMU identity is an org-side prerequisite, not a code change — see Open Questions).

## 4. Current State — every hardcoded GitHub host (verified 2026-06-29)

| Site | File:line | Host |
|------|-----------|------|
| Device code POST | `Bridge/SetupHelpers.cs:155` | `github.com/login/device/code` |
| Verification URI fallback | `Bridge/SetupHelpers.cs:173` | `github.com/login/device` |
| Token exchange POST | `Bridge/SetupHelpers.cs:196` | `github.com/login/oauth/access_token` |
| Provider base URL (setup) | `Bridge/SetupHelpers.cs:423` | `api.githubcopilot.com` |
| Models list GET | `Bridge/SetupHelpers.cs:504` | `api.githubcopilot.com/models` |
| **Runtime LLM base URL** | `Agent.Host/Llm/Providers/OpenAi/OpenAiCompatibleApiClient.cs:397` | `api.githubcopilot.com` |
| **Token-refresh / 401 path** | `Contracts/Hub/HubTypes.cs:580` (doc) + refresh impl | `api.githubcopilot.com` |

Relevant existing facts:
- **Inference host is already overridable:** the runtime uses `Credential.BaseUrl ??
  "https://api.githubcopilot.com"` (`OpenAiCompatibleApiClient.cs:394-400`) — so the API host
  is config-driven today, just not wizard-exposed.
- **The container does NOT exchange tokens.** It uses an OAuth token / Bridge-minted Copilot
  bearer directly as `Authorization: Bearer` (`OpenAiCompatibleApiClient.cs:408`); the
  OAuth→Copilot-bearer exchange + refresh happen **Bridge-side** (401 → `tokenManager.ForceRefreshAsync`, L73-79).
- `DefaultCopilotOAuthClientId = "Ov23li8tweQw6odWQebz"` (`SetupHelpers.cs:135`), shared default app.
- Endpoints already accept a client ID: `CopilotAuthRequest.ClientId` / `CopilotPollRequest.ClientId`
  threaded at `SetupEndpoints.cs:73,87`. The wizard sends `selectedTemplate?.clientId || null`
  (`setup.js:492`), and templates never set one, so it currently resolves to `null` → default.
- Config persists `clientId` only when it differs from the default (`SetupHelpers.cs:278-282`,
  `CortexConfigMutator.cs:87-93`) — the pattern to mirror for the new host fields.
- `PollCopilotTokenAsync` (`SetupHelpers.cs:208-238`) already parses the 200-body error codes
  correctly; its only gap is returning a bare `"failed"` on a non-2xx without the body.

## 5. Design

### 5.1 Config model (`BridgeConfig`, `github-copilot-api` provider)
Add ONE new optional field alongside the existing `clientId`:
- `githubBaseUrl` — **auth host** for the device/auth-code flow and the OAuth→Copilot-bearer
  exchange. Default `https://github.com` (exchange on `https://api.github.com`).

The **inference host is NOT a new field** — it is the existing `Credential.BaseUrl`
(default `https://api.githubcopilot.com`, `OpenAiCompatibleApiClient.cs:394-400`). For
enterprise, the Bridge's token-exchange should set `BaseUrl` from the exchange response's
`endpoints.api` so inference automatically targets the right (possibly proxied) host without
the user typing it. `BaseUrl` stays available as a manual override.

### 5.1a OAuth flow selection (D6)
- Wizard offers a **flow choice**: *Device code* (default) or *Authorization code + PKCE*.
- Device code: today's path (`/api/setup/copilot-auth` + `/copilot-poll`).
- Auth-code + PKCE: new GitHub endpoints modeled on the existing Anthropic ones
  (`GenerateAnthropicPkce` / `BuildAnthropicAuthUrl` → a `BuildGitHubAuthUrl` against
  `{githubBaseUrl}/login/oauth/authorize` + code exchange at `{githubBaseUrl}/login/oauth/access_token`).
- If a device-flow attempt returns `device_flow_disabled` (now surfaced by §5.4), the wizard
  should suggest switching to auth-code.

### 5.2 Setup wizard (`setup.html`, `setup.js`)
- Add an **Advanced** disclosure on the Copilot panel with three optional inputs:
  *OAuth App Client ID*, *GitHub Host*, *Copilot API Host*. Placeholders show the defaults.
- Thread the values into the existing `/api/setup/copilot-auth` and `/api/setup/copilot-poll`
  request bodies (extend `CopilotAuthRequest`/`CopilotPollRequest` with the host fields;
  `clientId` already present).

### 5.3 SetupHelpers (`SetupHelpers.cs`)
- `InitiateCopilotDeviceFlowAsync` / `PollCopilotTokenAsync` take a `githubBaseUrl`
  (default `https://github.com`) and build `"{githubBaseUrl}/login/device/code"` /
  `"/login/oauth/access_token"`.
- The provider base / models fetch (`L423`, `L504`) take the **inference host** — the
  configured/ discovered `BaseUrl` (default `https://api.githubcopilot.com`), not a new field.

### 5.4 Error surfacing (D4)
- `InitiateCopilotDeviceFlowAsync`: on non-success, **read the body** and throw/return an
  error carrying GitHub's `error` + `error_description`; `SetupEndpoints.cs:76-79` propagates
  it into the 502/wizard text.
- `PollCopilotTokenAsync`: on a non-2xx (L209-212), include the body text instead of a bare
  `"failed"`.

### 5.5 Retry (D3)
- Wrap only the device-code POST: retry on `5xx` + `HttpRequestException`/timeout with short
  backoff (2-3 attempts). **Do not retry 400/4xx** — return immediately so D4 reports the cause.

### 5.6 Agent runtime + refresh (D5)
- The runtime **already** honors `Credential.BaseUrl` for inference
  (`OpenAiCompatibleApiClient.cs:394-400`) — no change needed there beyond ensuring the
  Bridge **populates `BaseUrl`** from the token-exchange `endpoints.api` for enterprise.
- The Bridge-side OAuth→Copilot-bearer **exchange + refresh** must use the configured
  `githubBaseUrl` (auth/exchange host), not the hardcoded public one.

### 5.7 Persistence
- `GenerateYaml` / `CortexConfigMutator` emit `githubBaseUrl` (and `oauthFlow` when not the
  device default) **only when non-default**, mirroring the existing `clientId`-only-when-
  non-default behavior, so a public-GitHub setup's `cortex.yml` is unchanged. `BaseUrl`
  persists via its existing path (set by the Bridge from the exchange `endpoints.api`, or by
  a manual override).

## 6. Data Flow (unchanged shape, parameterized auth host)
Browser wizard (flow = device | auth-code) → `/api/setup/copilot-*  {clientId?, githubBaseUrl?}`
→ device/auth-code against `{githubBaseUrl}` → GitHub OAuth token → **Bridge** exchanges it
for a short-lived Copilot bearer (`{githubBaseUrl} api host`/`copilot_internal/v2/token`),
reading `endpoints.api` from the response → stores bearer + sets `BaseUrl` = that API host →
runtime calls inference at `BaseUrl` (default `api.githubcopilot.com`). Blank fields ⇒ public
defaults ⇒ today's behavior.

## 7. Open Questions
1. **GHE auth host** for the owner's enterprise: the device/auth-code + token-exchange host
   (likely the GHE web host, e.g. `https://<org>.ghe.com`, exchanging on its API host). The
   **inference host is discovered** from the exchange response's `endpoints.api`, so it does
   not need to be known up front — but confirm the exchange response actually carries it for
   this enterprise.
2. **Is device flow enabled on the GHE?** Many EMU/SSO orgs disable it → auth-code+PKCE
   becomes mandatory (D6). Determines whether the auth-code flow is "nice to have" or the
   primary path for the owner's work account.
3. **Auth model on the GHE:** native GitHub OAuth, or **Entra-fronted SSO**? Entra-fronted SSO
   may need an extra hop / different grant — confirm before committing either flow for GHE.
4. **Copilot entitlement:** does the work enterprise grant a Copilot license to the owner's
   EMU identity? (Org prerequisite; the code can target the host but cannot create entitlement.)
5. **Token-exchange host derivation:** confirm whether the exchange lives at the GHE's
   `api.<host>` vs a path on the web host, so the Bridge builds the right exchange URL.

## 8. Out of Scope / Deferred
- Prior **#5 pre-flight "test connection"** — nice-to-have; revisit after this lands.
- Auto-detecting host/identity.
- Non-Copilot providers.

## 9. Rough Effort
- #1 error surfacing + #2 retry-policy: ~1h together.
- Configurable auth host + client ID across wizard, SetupHelpers, endpoints, persistence: ~2-3h.
- Bridge-side exchange/refresh honoring `githubBaseUrl` + setting `BaseUrl` from
  `endpoints.api`: ~1-2h (runtime already honors `BaseUrl`).
- Authorization-code + PKCE GitHub flow (generalize the Anthropic pattern): ~2-3h.
- Confirming GHE host/auth model + device-flow availability (Open Questions 1-3): blocking
  research, not coding.

---

*Next step (when ready): confirm the GHE hostnames + auth model (§7), then turn this into an
implementation plan via the writing-plans skill. No code until then.*
