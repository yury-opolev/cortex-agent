# Speech (STT / TTS)

Found under **Global Settings → Speech**. These engines are shared by every
voice-capable channel — the local [Voice channel](help:voice-host) and Discord
voice. You only need them if you use voice.

## Speech-to-text: Whisper

Cortex transcribes speech with a local **Whisper** model. The Speech tab shows
whether the model is **Ready** or **Not installed**, with a one-click download
(~142 MB) if it's missing. Whisper auto-detects the spoken language.

## Text-to-speech

Cortex can synthesize speech with several engines. Available TTS engines include:

| Engine | Notes |
|--------|-------|
| **Kokoro** | Default, cross-platform, natural-sounding. |
| **Windows SAPI** | Uses the voices installed in Windows. |
| **Silero** | Compact neural voices. |
| **Silero v5 Russian / CIS** | Russian and CIS-language voices. |
| **Røst (Danish)** | Native Danish synthesis. |
| **Auto (Multi-language)** | Routes to a per-language voice automatically. |

## Language Voice Configuration

Cortex **auto-detects** the language of each message. In this panel you declare:

- **Supported languages** — add or remove the languages you want handled.
- **Male / Female voice per language** — which voice to use for each language,
  picked from the engines above.
- **Fallback language** — used when detection is uncertain or a language isn't in
  your supported list.

This is what lets Cortex reply in the right voice when you switch languages
mid-conversation.

## Models

The **Speech Models** panel manages downloading and status for the STT model and the
TTS provider models. Some neural voices download on first use; others can be placed
manually.

> DM voice transcription on Discord and any voice channel require the Whisper model
> to be installed; voice replies require a working TTS engine.
