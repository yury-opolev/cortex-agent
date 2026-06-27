# Local Voice (Host Mic/Speakers)

The **Voice** channel lets you talk to Cortex out loud using your Windows host's
**microphone and speakers** — no Discord involved. Enable it under **Global Settings
→ Channels → Voice**.

This is distinct from Discord voice: this channel captures and plays audio directly
on the host. It requires Windows and the speech engines (Whisper STT + a TTS engine —
see [Speech](help:speech-settings)).

## Activation Mode

How listening is triggered:

- **Wake word** — Cortex listens for a phrase (default `hey cortex`,
  case-insensitive) and starts capturing when it hears it. Configure the phrase in
  the **Wake Word** field.
- **Push-to-talk** — a global hotkey starts and stops listening. Set the **Push-to-Talk
  Hotkey** (e.g. `Ctrl+Space`, `Alt+Shift+V`, `F5`). Press once to start, again to
  stop.

## Audio devices

By default the channel uses your system's **default input and output** devices. If
you need to pin specific devices, the underlying options support an input and output
device index (default `-1` = system default).

## The desktop overlay

When the Voice channel is active, Cortex shows a small status overlay so you can see
its state — idle/waiting, listening, processing, speaking, or paused — and click to
start/stop listening or pause playback.

## Turning it off (releasing the mic/speakers)

To stop Cortex from using the host microphone and speakers, set the Voice channel
toggle to **off** under **Global Settings → Channels**. Because channel changes need
a restart, the UI will prompt to restart the Bridge; once it respawns, the Voice
channel doesn't start and the host audio devices are no longer in use.

You can confirm it's off in the Bridge log: a running Voice channel logs
`Channel registered: Voice (voice-default)` at startup. If that line is absent after a
restart, the host voice channel is disabled.

> If the Voice channel is enabled but the Whisper (STT) or TTS model isn't available,
> the channel won't start and the Bridge logs a warning telling you to install the
> model under **Settings → Speech**.
