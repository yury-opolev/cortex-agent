# Design: Voice-transport watchdog (auto-recover silent audio death)

**Date:** 2026-06-28
**Status:** Approved (implementing)
**Branch:** `feat/discord-image-attachments` (carries this fix too)

## Problem

In a Discord voice session on 2026-06-28, Emma went silent for ~14 minutes
(14:22–14:36). Root cause, confirmed from `bridge-20260628.log`:

- At 14:22:16 the Discord **audio transport** died (`Discord.Net [Audio #2]: A task was canceled`).
- Discord's **gateway** stayed alive the whole time (heartbeats continued; the bot still
  showed as present in the voice channel — presence is gateway-managed, separate from the
  audio/UDP transport).
- The audio death raised **no** `IAudioClient.Disconnected` event — `OnAudioClientDisconnected`
  never logged `voice-in: connection lost`. `ConnectionState` was likely left stale at
  `Connected`.
- Recovery in `DiscordVoiceHandler` is **entirely event-driven**: `audioClient.Disconnected`
  (`OnAudioClientDisconnected:662`), gateway-reconnect (`OnGatewayReconnectedAsync:696`), and
  user-join (`:566`). None fired. `IsConnected` (`:330`) is only ever read reactively —
  **nothing polls it**.
- Result: dead audio, no inbound STT, no outbound TTS, until the user manually left/rejoined
  the channel (14:36) which fired `user-joined` → reconnect → instant recovery.

This is the same class as the 2026-05-15 outage (cited in a code comment), but via a death
mode the existing handlers don't catch: a silent audio-task cancellation with no gateway event
and a stale `Connected` state.

## Goal

Auto-recover a silently-dead voice audio transport while the linked user is still present,
without waiting for a manual rejoin — and without risking a reconnect storm.

## Approach (decided in brainstorming)

Two detection paths plus a guarded forced-reconnect primitive.

### 1. Periodic watchdog (per `DiscordVoiceHandler`)
A `PeriodicTimer` loop (~10 s). On each tick, if the linked user is present and the transport
is not alive (`!IsConnected`), call the existing `EnsureConnectedAsync("watchdog")`. Catches
the case where `ConnectionState` flipped but nothing polled it. The (REST) user-presence check
runs only when the transport already looks dead, so the steady-state cost is one cheap
`ConnectionState` read per tick.

### 2. Log-signal trigger
`DiscordChannel.OnDiscordLog` already parses audio logs (for DAVE stats). Add a classifier for
the audio-death line; on a match, fan out `NotifyAudioTransportSuspectDead()` to the voice
handler(s). This forces recovery even when `ConnectionState` lies (stale `Connected`).

### 3. `ForceReconnectAsync(trigger)` (new, guarded)
Unconditional teardown + rejoin under `connectionGate` (bypasses the `IsConnected` early-return
in `EnsureConnectedAsync`). Guarded by a **cooldown** (min ~25 s between forced reconnects per
handler) so a misfire can never cause a reconnect storm — the one outcome worse than silence.
Only runs when the linked user is present.

## New isolated, testable units

Mirrors the existing `VoiceConnectionState` / `VoiceStateRouter` / `DaveEventStats.Classify`
pure-predicate pattern, so the logic is unit-testable without Discord dependencies.

### `AudioDeathLogClassifier`
```
static bool IsAudioTransportDeath(string source, string message)
```
Matches `source == "Audio"` (or `"Voice"`) with a known death message
(`"A task was canceled"`, and other known transport-death strings). Pure.

### `VoiceWatchdogDecision`
```
enum WatchdogAction { None, Reconnect, ForceReconnect }
static WatchdogAction Decide(bool userPresent, bool isConnected, bool suspectDead,
                             long lastForcedReconnectTicks, long nowTicks, long cooldownTicks)
```
- not present → `None`
- present && !isConnected → `Reconnect`
- present && isConnected && suspectDead && cooldown elapsed → `ForceReconnect`
- present && isConnected && suspectDead && within cooldown → `None`
- present && isConnected && !suspectDead → `None`
Pure; fully unit-tested.

## Changes to existing files

- `DiscordVoiceHandler`
  - watchdog loop (`PeriodicTimer`), started when a connection is established, stopped on dispose
  - `ForceReconnectAsync(trigger)` with cooldown tracking (`lastForcedReconnectTicks`)
  - `NotifyAudioTransportSuspectDead()` — sets a `suspectDead` flag consumed by the next tick
  - watchdog tick consults `VoiceWatchdogDecision.Decide(...)` then runs `EnsureConnectedAsync`
    / `ForceReconnectAsync` accordingly; clears `suspectDead` after acting
- `DiscordChannel.OnDiscordLog` — classify via `AudioDeathLogClassifier`; on match, call
  `NotifyAudioTransportSuspectDead()` on the voice handler(s)

## Multi-tenant note

The audio log line (`Audio #N`) does not identify a tenant. For the single-voice-handler case
(current setup) this is a non-issue. For multi-tenant, the `user-present` guard + cooldown bound
the blast radius to at most one brief reconnect of a healthy handler. Tighter per-connection
routing is a future refinement, not built now.

## Testing

- `AudioDeathLogClassifierTests` — matches `"A task was canceled"` on `Audio`/`Voice` source;
  ignores benign/unrelated lines and other sources.
- `VoiceWatchdogDecisionTests` — every branch above, incl. cooldown allows after elapse and
  blocks within.
- `DiscordVoiceHandler` / `DiscordChannel` wiring relies on those pure units + manual
  post-deploy verify (consistent with how the existing voice paths are tested — they require a
  live Discord client).

## Manual verification (post-deploy)

Simulate/await an audio-transport death (or force one) with the user present and confirm the
watchdog re-establishes voice within ~10 s without a manual rejoin, and that repeated triggers
do not cause a reconnect storm (cooldown holds).
