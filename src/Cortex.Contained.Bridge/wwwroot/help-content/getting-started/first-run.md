# First-Run Setup

The first time Cortex starts, it runs a **Setup Wizard** in the web UI. You can
re-run it any time from **Global Settings → General → Re-run Setup Wizard**.

## 1. Pick a language model provider

Cortex needs at least one LLM provider to think. Supported provider types:

- **OpenAI** — paste an API key.
- **Anthropic** — paste an API key.
- **GitHub Copilot** — sign in with GitHub's device flow (no key needed); gives you
  access to all models in your Copilot subscription, including Claude and GPT.
- **Ollama** — point at a local Ollama server for fully local models.

Keys you enter are stored **encrypted on the host** via Windows DPAPI — they are not
written to disk in plaintext and are never persisted inside the Agent container.

See [LLM Providers & Routing](help:llm-providers) for the full settings reference.

## 2. Choose your default model

Each provider exposes one or more models. You pick a **default model** (used for
normal conversation) and optionally a separate, cheaper **memory model** for
background extraction and compaction work.

## 3. (Optional) Enable channels

Out of the box you can talk to Cortex right here in **Web Chat**. You can also enable:

- **Discord** — chat with the assistant from Discord, including DMs and voice.
- **Local Voice** — talk to it using your host's microphone and speakers.

Channels are covered in detail in the **Channels** section.

## 4. (Optional) Set up speech

If you want voice, download the **Whisper** speech-to-text model and pick a
**text-to-speech** voice under **Global Settings → Speech**. See
[Speech (STT / TTS)](help:speech-settings).

## Where your data lives

On the host, Cortex keeps its config and data under your local app data folder
(`%LOCALAPPDATA%\Cortex`): the `cortex.yml` config, encrypted `secrets`, message
history, downloaded `models`, and `logs`. Editing settings in this web UI writes to
that `cortex.yml` for you — you rarely need to touch the file directly.
