# Discord voice — DAVE decrypt-desync recovery & instrumentation

**Date:** 2026-07-10
**Status:** Approved design
**Branch:** `feature/dave-decrypt-recovery`

## Problem

During a Discord voice conversation the agent went **deaf** — it stopped
responding to the user for ~6.7 minutes — until the user manually left and
rejoined the voice channel, after which it worked normally for the rest of the
session. This is a **fourth, distinct** Discord-voice silent-outage mode, seen
on the deployed build `v0.2.310`.

### Evidence (bridge-20260708.log, evening session)

| Time | Event |
|------|-------|
| 19:03:04 | User joins voice (`trigger=user-joined`) |
| 19:03:06 | **Bad join**: `MLS Failure: ... Unexpected user ID in add proposal`, `has data?: False`, `mlsFail=1` |
| 19:03:13 | Our v0.2.292 auto-rejoin fires (`trigger=dave-mls-failure`) — bot leaves+rejoins, **user stays** |
| 19:03:14 | Rejoin *looks* clean: `has data?: True`, `Commit result succeeded` |
| 19:03:15 → 19:04:34 | Conversation **works** (5 clean responses) |
| **19:04:37 →** | `decryptFail` **bursts** begin: 19, 227, (zeros ~90s), 42, 22, (zeros), 108, 10, (zeros), 94 |
| 19:04:34 → 19:11:16 | **Zero speech-onset, zero agent audio** — bot deaf ~6m42s |
| 19:11:16 / 19:11:27 | User **leaves + rejoins** (full membership teardown) |
| 19:11:28 | Rejoin clean (`has data?: True`, commit succeeded); healthy for the next **26 min** |

### Root cause

`decryptFail` means **the bot cannot decrypt the user's inbound audio** (Discord
DAVE end-to-end encryption). No decrypted PCM → no VAD onset → the agent is never
prompted → total silence. Key characteristics established from the logs:

1. **Intermittent, not continuous** — `decryptFail` comes in short bursts
   (each ≈ one 5 s stats window) separated by long stretches of `decryptFail=0`,
   correlated with the user *resuming* speech after a silence gap. This is a
   **per-sender media-key ratchet desync** within a single MLS epoch (no epoch
   transition is logged around the bursts), not an MLS membership failure.
2. **Seeded by the join-race** — the session was born from a join-race MLS
   validation failure (`"Unexpected user ID in add proposal"`). A clean session
   (user leave+rejoin) did not develop the wedge.
3. **A bot-only rejoin looked clean but still wedged** (19:03:14); only the
   **user's** full leave+rejoin reliably cleared it (19:11:27). Whether a
   bot-only rejoin *triggered after the wedge is established* clears it is
   **unproven** — the logs contain no such data point.

### Why it never self-heals

`DiscordChannel.OnDiscordLog` reacts to exactly two signals:
`AudioDeathLogClassifier` (`"A task was canceled"` → `NotifyAudioTransportSuspectDead`)
and `DaveEventKind.MlsFailure` within the join-race window
(→ `NotifyDaveSessionSuspect`). `DaveEventKind.DecryptFailure` is **only counted
and logged** — it sets no suspect flag. So `WatchdogTickAsync` sees
`isConnected=true, suspect=false` and early-returns every tick. A sustained
decrypt-failure flood is invisible to recovery.

### Constraints on the fix (what we can and cannot control)

- **We cannot disable DAVE by default.** Discord enforces DAVE E2EE for all
  non-stage voice as of **March 2026**; a non-DAVE client is rejected with voice
  **close code 4017** (`EndToEndEncryptionDAVEProtocolRequired`). *(Whether this
  guild actually enforces it is empirically testable — see Component 1.)*
- **We cannot patch the desync's origin.** The per-sender ratchet and MLS
  validation live in the compiled native `libdave` (Rust) that
  `Discord.Net.Dave` wraps.
- **We can patch the managed layer.** `Discord.Net` is vendored as our own fork
  (`yury-opolev/Discord.Net`, submodule at `lib/Discord.Net`). The managed
  `Discord.Net.Dave` owns DAVE *session management* (epoch transitions and
  `SendMLSInvalidCommitWelcomeAsync`, DAVE's built-in "re-add me" resync).

## Approach — staged

The true in-place desync fix requires **reproducing the wedge with
instrumentation** (epoch/ratchet state at each decrypt failure). This spec
covers **Stage 1**: (a) empirically test whether DAVE can simply be turned off
for this guild — which would eliminate the whole class; (b) add the
instrumentation needed to diagnose the next occurrence; (c) ship a safety-net
recovery so the deaf state self-heals in ~30–60 s. **Stage 2** (the real
in-place resync fix) is deferred until the instrumentation yields a reproduction
and is out of scope here.

## Component 1 — Internal DAVE toggle + 4017 handling

Make DAVE on/off a **runtime-config decision** so we can test elimination
reversibly, without a rebuild.

- **Config:** `channels.discord.settings.enableVoiceDaveEncryption`, a `bool`
  defaulting to **`true`** (today's behavior — feature inert until flipped).
  Internal/advanced: **`cortex.yml` only, no web UI.**
- **Wiring:** flows through the existing Discord voice-settings path into
  `DiscordChannel` and replaces the hardcoded `EnableVoiceDaveEncryption = true`
  at `DiscordChannel.cs:164`. Read once at Bridge startup (restart-required, like
  the other Discord voice toggles — consistent with existing behavior).
- **4017 safety:** detect voice close code `4017`
  (`EndToEndEncryptionDAVEProtocolRequired`) and emit a single loud, unambiguous
  log: *"Voice rejected 4017: this channel requires DAVE — set
  `enableVoiceDaveEncryption: true` and restart."* So a rejected experiment is
  obvious and trivially reverted. **No auto-fallback** (YAGNI — the setting is a
  deliberate, easily-reverted experiment toggle).

**Test protocol:** set `enableVoiceDaveEncryption: false`, restart the Bridge,
join voice, speak. Either it connects clean (→ eliminate DAVE permanently) or it
4017s (→ revert; rely on Components 2–3 and drive Stage 2).

## Component 2 — DAVE decrypt-flood instrumentation (at cortex's layer)

**Constraint discovered in planning:** the build consumes `Discord.Net` /
`Discord.Net.Dave` from **NuGet 3.20.1** (pinned in `Directory.Packages.props`;
the `lib/Discord.Net` submodule is vestigial — the fork was deliberately dropped
on 2026-06-29). Modifying the vendored `DaveDecryptStream` would therefore have
**no effect**, and re-vendoring the fork just to add logging is not worth
reversing that decision. So instrumentation lives in **cortex's own code**.

cortex already classifies every decrypt-failure log line in
`DiscordChannel.OnDiscordLog` (`DaveEventStats.Classify` +
`DaveEventStats.TryParseUserId`), giving us **user id + `DecryptorResultCode`**
(RTP sequence/timestamp of the *failed* packet is dropped inside the NuGet
`DaveDecryptStream` before it reaches us — not recoverable without re-vendoring).

Track per-**burst** decrypt-failure state (consecutive run of failures for a
user, uninterrupted by a success/commit) and emit a concise summary log when a
burst ends: `userId, failureCount, durationMs, dominant resultCode`. This
characterizes burst size/cadence for Stage 2 and — crucially — **is the same
accumulator Component 3 trips its recovery on** (the two components share one
mechanism). Richer RTP-level instrumentation is explicitly out of scope (would
require re-vendoring the fork).

## Component 3 — Burst-aware decrypt-flood recovery watchdog

Add a third recovery signal, symmetric with the existing audio-death and
MLS-failure signals, that reacts to the decrypt-flood.

### Detection policy (pure, unit-tested)

New pure decision unit `DaveDecryptFloodPolicy` (mirrors `DaveMlsRecoveryPolicy`
/ `VoiceWatchdogDecision`), evaluated each watchdog tick (~10 s):

Trip **only** when **all** hold over a rolling evaluation window (≈ 30–60 s):

1. **User present** in the target channel, **and**
2. `decryptFail` has been **actively accumulating** across the window (proves
   packets are arriving = the user is transmitting — this is what distinguishes
   a wedge from ordinary silence), **and**
3. **zero successful speech commits** occurred in that window (nothing is getting
   through).

This inherently:
- **never fires during silence** — silence produces no `decryptFail`, so
  condition 2 fails;
- **never fires during a healthy conversation** — successful commits fail
  condition 3;
- **accumulates across bursts** — it counts failures over the window rather than
  requiring one continuous failing stretch (the observed pattern was bursty).

### Recovery action

On trip, set a new `decryptFloodSuspect` flag consumed by `WatchdogTickAsync`,
producing a `ForceReconnect` with a new trigger label **`dave-decrypt-flood`**,
reusing the existing `ForceReconnectAsync` machinery (25 s cooldown +
user-present gate already apply). No proactive re-speak (this is inbound — there
is nothing to replay).

**Known limitation (documented, not blocking):** a bot-only rejoin's efficacy on
this exact wedge is unproven (see Root cause §3). If Component 2's instrumentation
later shows the bot rejoin does not clear it, the recovery action becomes a
Stage-2 decision (in-place resync, or prompting the user to rejoin). Shipping the
watchdog now is still net-positive: at worst it is a no-op on this wedge; at best
it heals it in ~30–60 s.

### State tracking

The handler samples `daveStats` decrypt-failure deltas per tick and tracks the
last successful speech-commit time. `DaveDecryptFloodPolicy.Decide(...)` takes
these as plain inputs (delta counts, ticks-since-last-commit, user-present,
window/threshold constants) and returns a bool — no live Discord client needed
for tests.

## Files touched

- `src/Cortex.Contained.Channels.Discord/DiscordChannel.cs` — DAVE toggle from
  config; 4017 close-code detection/log.
- `src/Cortex.Contained.Contracts/Config/*` + Discord options/`VoiceHandlerConfig`
  — plumb `enableVoiceDaveEncryption` (default true).
- `src/Cortex.Contained.Channels.Discord/DaveDecryptFloodPolicy.cs` — **new**
  pure policy.
- `src/Cortex.Contained.Channels.Discord/DiscordVoiceHandler.cs` — decrypt-flood
  sampling, `decryptFloodSuspect` flag, watchdog wiring, `dave-decrypt-flood`
  trigger.
- `src/Cortex.Contained.Channels.Discord/DaveDecryptBurstTracker.cs` — **new**
  per-user decrypt-failure burst accumulator (instrumentation summary log +
  the signal Component 3 consumes).
- `src/Cortex.Contained.Channels.Discord/DaveEventStats.cs` — expose any snapshot
  helpers the tracker/policy needs (if not already present).
- Tests under `tests/Cortex.Contained.Channels.Discord.Tests/` — new policy +
  burst-tracker unit tests + config default test (red/green TDD).

*(Not touched: `lib/Discord.Net` — the build uses NuGet Discord.Net 3.20.1, so
the submodule has no effect on the shipped binary.)*

## Testing

- **Unit (red/green TDD):** `DaveDecryptFloodPolicy` truth table — silence
  (no trip), healthy conversation with commits (no trip), sustained flood with
  no commits + user present (trip), cooldown/edge cases. Config default = `true`
  (byte-inert). 4017 classifier.
- **Manual/live (post-deploy):** the DAVE-disabled test protocol (Component 1);
  and — if a wedge recurs with DAVE on — confirm the watchdog trips
  `dave-decrypt-flood` and whether the rejoin clears it (Component 2 telemetry).

## Rollout

Ships via `Build-All.ps1` (cert `F578A5879BE57511D40288B6DA3A0F383BD74EEE`) +
`Self-Update.ps1 -Schedule`. `main` is protected → PR required
(`gh pr create` + `gh pr merge --merge --delete-branch`, 0 required approvals).
Version bump is its own concern of the build.

## Out of scope (Stage 2)

The real in-place desync fix (e.g. triggering `SendMLSInvalidCommitWelcome` /
forced re-add on detected flood, validated against a reproduction) — deferred
until Component 2 instrumentation yields a diagnosable repro.
