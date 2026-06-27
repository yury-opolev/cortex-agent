# Discord

The Discord channel lets you talk to Cortex from Discord — direct messages, server
text, and voice. Enable it under **Global Settings → Channels → Discord**.

## Bot setup (one time)

The Channels tab includes a collapsible **Setup Guide**. In short:

1. Create an application at
   [discord.com/developers/applications](https://discord.com/developers/applications).
2. Under **Installation**, enable **Guild Install** with the `bot` scope.
3. Under **Bot**, **Reset Token** and copy it.
4. Enable **Public Bot** and the **Message Content Intent** (needed to read messages).
5. Paste the token into the **Bot Token** field and save.

The token is stored encrypted (DPAPI), not in plaintext config. After saving, the bot
connects automatically.

## Global Discord settings

- **Bot Token** — the bot credential from the Developer Portal.
- **DM Voice Transcription** — transcribe voice-message attachments sent in DMs using
  Whisper STT. Requires the Whisper model and a Bridge restart.
- **DM Voice Reply Mode** — reply to DM voice messages as **text** (default) or as a
  synthesized **voice** attachment.
- Voice tuning (for live voice conversations): **Silence Timeout**, **Smart Turn
  Detection**, and **Barge-In** options (see below).

## Per-tenant linking and voice

Linking a Discord user and choosing a voice channel is done **per tenant** under
**Tenant Settings → Discord**:

- **Pairing** — generate a setup code and send the instructions to the user; they
  link their Discord account to that tenant.
- **Voice channel** — once linked, paste the **Guild (Server) ID** and **Voice
  Channel ID** (both are in the channel's Discord URL:
  `discord.com/channels/GUILD_ID/CHANNEL_ID`). Optionally set a spoken **Voice
  Greeting**. When configured, the bot auto-joins when you enter that voice channel.

Discord voice requires working STT and TTS engines — see [Speech](help:speech-settings).

## Live voice tuning

- **Silence Timeout (ms)** — soft commit point after you stop speaking. With Smart
  Turn Detection on, completed sentences commit much faster; a hard ceiling prevents
  stranded waits.
- **Smart Turn Detection** — a small ONNX model predicts when you've finished a
  thought and commits early, typically cutting latency by ~1 second on complete
  sentences.
- **Barge-In Detection** — lets your speech interrupt the bot's playback. The
  **Onset Guard** filters brief noises (coughs, claps) before treating speech as a
  real interruption, and the **Classifier** decides whether an interruption is you
  taking the floor vs. a backchannel ("mhm").
