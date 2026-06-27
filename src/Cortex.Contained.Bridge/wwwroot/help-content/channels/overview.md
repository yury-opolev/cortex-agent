# How Channels Work

A **channel** is a way to reach the assistant. Cortex ships with three:

- **Web Chat** — the chat in this UI. Always available; it's the default channel and
  can't be disabled. See [Web Chat](help:webchat).
- **Discord** — a Discord bot for text and voice. See [Discord](help:discord).
- **Local Voice** — talk through the host's microphone and speakers. See
  [Local Voice](help:voice-host).

## Enabling and configuring channels

Channels are managed under **Global Settings → Channels**. Each channel is a card
with an on/off toggle (the default Web Chat channel has no toggle). Enabling a
channel reveals its settings and a **Check Prerequisites** button that verifies what
the channel needs (for example, the Whisper model and the Windows platform for
voice).

> **Channel changes require a Bridge restart.** When you toggle a channel or save its
> settings, the UI offers to restart Cortex; confirming respawns the Bridge
> automatically (about 10–15 seconds). This is exactly why a channel you just turned
> off stops using its resources — for example, disabling Local Voice releases the
> host microphone after the restart.

## Per-tenant vs. global

Some channel configuration is **global** (the Discord bot token, local voice device
and activation settings) and some is **per-tenant** (which Discord user is linked,
which Discord voice channel a tenant uses, a tenant's API key). Per-tenant settings
live under **Tenant Settings**.

## How a message flows

Whatever the channel, the Bridge receives your message, routes it to the right
tenant, and hands it to the Agent over the local hub. The Agent thinks, optionally
runs tools, and streams a reply back through the same channel. Voice channels add
speech-to-text on the way in and text-to-speech on the way out.
