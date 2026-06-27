# Discord Voice Calls for AI Agent

Research document covering what it takes to implement real-time voice conversations
between a user and the AI agent in Discord, plus voice interface design principles.

> **Historical note (2026-04):** this document was written before Piper TTS and
> the `sherpa-onnx` dependency were removed from the project. Any recommendation
> below that relies on Piper or sherpa-onnx is no longer current. Supported TTS
> engines are now **Kokoro**, **Silero**, and **Windows SAPI**. Streaming STT is
> tracked separately in [research-voice-improvements.md](./research-voice-improvements.md).

## Table of Contents

- [Current State](#current-state)
- [Discord Voice API: How It Works](#discord-voice-api-how-it-works)
- [What We Already Have](#what-we-already-have)
- [Open Questions and Design Decisions](#open-questions-and-design-decisions)
- [Open-Source Reference Projects](#open-source-reference-projects)
- [Voice AI Frameworks](#voice-ai-frameworks)
- [Real-Time Voice AI APIs](#real-time-voice-ai-apis)
- [Voice vs Text: How the Agent Must Behave Differently](#voice-vs-text-how-the-agent-must-behave-differently)
- [System Prompt Modifications for Voice Mode](#system-prompt-modifications-for-voice-mode)
- [Multi-Modal: Voice + Text Together](#multi-modal-voice--text-together)
- [Latency Budget](#latency-budget)
- [Common Mistakes and Lessons Learned](#common-mistakes-and-lessons-learned)
- [Implementation Plan](#implementation-plan)
- [References](#references)

---

## Current State

We already have a working Discord voice implementation in `DiscordVoiceHandler.cs` that:

- Auto-joins/leaves a configured guild voice channel when users join/leave
- Receives per-user audio via `IAudioClient.StreamCreated` (48kHz mono PCM)
- Performs VAD with RMS energy threshold (0.01) and silence timeout (1500ms)
- Resamples 48kHz to 16kHz and transcribes via Whisper
- Sends TTS audio back through Discord's audio stream
- Tracks per-user audio state

This document focuses on what needs to change to make the experience good, and
what additional capabilities are needed (greeting, proactive calls, turn-taking,
voice-appropriate LLM responses).

---

## Discord Voice API: How It Works

### Connection Flow

```
1. Bot sends Gateway Opcode 4 (Voice State Update) with guild_id + channel_id
2. Gateway responds with Voice State Update (session_id) + Voice Server Update (token, endpoint)
3. Bot opens separate WebSocket to voice endpoint (wss://{endpoint}?v=8)
4. Bot sends Opcode 0 Identify on voice WebSocket
5. Bot receives Opcode 2 Ready (SSRC, UDP IP/port, encryption modes)
6. Bot opens UDP connection to voice server
7. IP Discovery (UDP packet to find external IP/port)
8. Bot sends Opcode 1 Select Protocol with encryption mode
9. Bot receives Opcode 4 Session Description with secret key
10. Bot sends Opcode 5 Speaking before transmitting audio
11. Audio flows over UDP as encrypted Opus in RTP packets
```

### Audio Format

- **Codec**: Opus
- **Channels**: 2 (stereo)
- **Sample rate**: 48kHz
- **Frame size**: 20ms (960 samples per channel)
- Audio is **per-user** (each user has a unique SSRC), not mixed

### Encryption

All deprecated XSalsa20 modes were discontinued November 2024. Current modes:

| Mode | Status |
|------|--------|
| `aead_aes256_gcm_rtpsize` | Preferred |
| `aead_xchacha20_poly1305_rtpsize` | Required fallback |

### DAVE Protocol (End-to-End Encryption)

**Mandatory since March 1, 2026** for all Discord voice/video:

- Uses MLS (Messaging Layer Security) for group key exchange
- Each sender has a ratcheted per-sender key
- Opus frames encrypted with AES128-GCM at the frame level (on top of transport encryption)
- Protocol whitepaper: https://daveprotocol.com
- Open source library: https://github.com/discord/libdave
- Discord.Net has DAVE support (PR #3241 merged Feb 2026)

### DM Voice Calls: Not Possible for Bots

Bots **cannot** join DM voice calls. The Voice State Update (Opcode 4) requires
a `guild_id` field -- there is no mechanism for bots to join DM calls.

**Workaround**: Create a private server voice channel restricted to just the bot
and the target user.

### VAD Signals from Discord

Discord provides **Opcode 5 Speaking** events (start/stop speaking per user via
SSRC mapping). However, this is coarse -- the bot needs its own silence detection
for accurate turn-taking. Discord clients perform their own VAD before sending
audio, so the bot only receives packets during active speech.

### Discord.Net Voice Support (Our Current Library)

- Sending: `IAudioClient.CreatePCMStream()` -- feed PCM S16LE 48kHz stereo, auto-encodes Opus
- Receiving: `IAudioClient.StreamCreated` -- per-user audio streams (recently fixed for DAVE)
- Native dependencies: libsodium + opus
- DAVE E2EE: Supported (PR #3241 merged Feb 2026)
- Limitation: one voice connection per guild

---

## What We Already Have

### Channel Architecture

```
Discord Voice Channel
    |
    v (Opus packets)
DiscordVoiceHandler (48kHz mono PCM via IAudioClient.StreamCreated)
    |-- Per-user VAD (RMS threshold 0.01, silence timeout 1500ms)
    |-- Resample 48kHz -> 16kHz
    |-- ISpeechToText (Whisper) -> transcript
    |-- Fires _onTranscription callback
    |
    v
DiscordChannel.MessageReceived -> ChannelManager -> HubMessageDispatcher -> Agent
    |
    v (response text)
DiscordVoiceHandler.SendVoiceAsync
    |-- ITextToSpeech -> PCM
    |-- Resample to 48kHz
    |-- Write to Discord audio stream (960-sample Opus frames)
```

### Speech Engines (Cortex.Contained.Speech)

| Engine | Type | Details |
|--------|------|---------|
| Whisper.net | STT | Local GGML model, 16kHz mono input |
| Kokoro 82M | TTS | ONNX, 24kHz output, streaming support, cross-platform |
| Piper | TTS | VITS/ONNX, multiple languages, streaming support |
| Windows SAPI | TTS | Windows-only, 16kHz output, no streaming |

### Streaming STT: sherpa-onnx (Already in Dependencies)

We already have `org.k2fsa.sherpa.onnx` in our NuGet dependencies (used for
Piper TTS). It also provides **true streaming speech recognition** via
`OnlineRecognizer`:

```csharp
// Already installed: org.k2fsa.sherpa.onnx
var config = new OnlineRecognizerConfig();
config.ModelConfig.Transducer.Encoder = "encoder.onnx";
config.ModelConfig.Transducer.Decoder = "decoder.onnx";
config.ModelConfig.Transducer.Joiner = "joiner.onnx";
config.ModelConfig.Tokens = "tokens.txt";

var recognizer = new OnlineRecognizer(config);
var stream = recognizer.CreateStream();

// Feed audio frame-by-frame as it arrives from Discord
stream.AcceptWaveform(sampleRate, audioSamples);
recognizer.Decode(stream);

// Get partial result -- available within ~200ms of first audio
var result = recognizer.GetResult(stream);
// result.Text = partial transcription so far
```

Available streaming models:
- **Zipformer transducer**: English, Chinese+English, Korean, French, etc.
- **Streaming Paraformer**: Chinese+English
- **Streaming CTC models**: various languages
- Models as small as 14MB, runs on CPU with 50-200MB RAM

Comparison with Whisper:

| | sherpa-onnx streaming | Whisper.net (batch) |
|-|----------------------|---------------------|
| First words | <200ms | After full utterance |
| Quality | ~85-90% of Whisper | Best |
| Streaming | True frame-by-frame | No |
| Use case | Barge-in, real-time feedback | Final transcription |

**Two-pass architecture**: Use sherpa-onnx streaming for instant partial results
(barge-in detection, real-time feedback), then Whisper for the final
high-quality transcription after the user finishes speaking.

Also notable: **EchoSharp** (by the same author as Whisper.net) is a C# library
that orchestrates VAD + STT with sherpa-onnx and Whisper.net backends in a
clean API. NuGet: `EchoSharp`, `EchoSharp.Onnx.Sherpa`, `EchoSharp.Whisper.net`.
GitHub: https://github.com/sandrohanea/echosharp

### Audio Utilities (AudioConverter)

- `Resample()` -- linear interpolation between any sample rates
- `DecodeOggOpus()` / `EncodeOggOpus()` -- Opus codec via Concentus
- `ComputeRms()` -- VAD energy detection
- `Pcm16ToFloat()` / `FloatToPcm16()` -- format conversion

---

## Design Decisions

These decisions are finalized based on our requirements (1-on-1 voice chat in
Discord, not multi-user).

### 1. When Does the Agent Join?

**Decision**: Auto-join when a user joins the configured voice channel (current
behavior). This is a 1-on-1 setup -- the user joins the designated channel to
talk to the agent.

### 2. Turn Detection

**Decision**: Always-on listening. Since this is 1-on-1, there's no need for
name-based addressing or wake words. The agent listens to everything the user
says.

- When audio packets arrive from the user, start buffering (STT accumulation)
- When **1.5 seconds of silence** pass with no audio packets, the turn is complete
- Transcribe the buffered audio via Whisper and send to the agent
- 1.5s allows natural mid-sentence pauses without cutting the user off

### 3. Greeting

**Decision**: Short configurable greeting phrase, spoken via TTS when the bot
joins and a user is present. Configurable as `VoiceGreeting` in
`DiscordChannelOptions`.

Default: "Hey! I'm here if you need anything."

Don't use the LLM for the greeting -- it adds latency and the user is waiting
for the channel to be ready.

### 4. Acknowledgment Before Processing

**Decision**: Before the agent processes the user's message through the LLM,
speak a brief acknowledgment phrase via TTS ("Got it", "Let me think about
that", "Sure, one moment"). This fills the silence during STT + LLM processing
and signals the agent heard the user.

The acknowledgment should be a quick fixed phrase, not LLM-generated (that
would defeat the purpose of reducing perceived latency).

### 5. Barge-In (Interruption)

**Decision**: When the user starts speaking while the agent is playing TTS audio,
use **early transcription** to decide quickly whether it's an interruption.

The flow:

1. **Pause** TTS playback immediately when user audio packets are detected
2. Start buffering user audio and begin STT **immediately** -- don't wait for
   silence. Transcribe in short increments (~0.5-1s of audio) to get the first
   few words as fast as possible.
3. Once we have a partial transcript (even just a few words), make a **quick
   LLM classification** (memory model): "Is this an interruption or a
   backchannel reaction? Answer: interrupt or continue"
4. If **continue** -- resume TTS playback from where it was paused
5. If **interrupt** -- discard remaining TTS audio. Continue buffering until
   1.5s silence, then process the complete transcript as a new message.

**Why early transcription matters**: Instead of waiting for 1.5s of silence
before we even start STT, we get "wait, actually..." or "uh huh" within
~0.5-1s. This makes the barge-in decision feel responsive rather than sluggish.

**Streaming STT for early detection**: Whisper is fundamentally a batch model
(architectural limitation -- the encoder processes the entire audio at once).
However, **we already have sherpa-onnx** in our dependencies (used for Piper
TTS), and it includes a true **streaming ASR API** (`OnlineRecognizer`) that
produces partial transcription results in under 200ms, frame-by-frame.

The recommended approach for barge-in is a **two-pass architecture**:

1. **sherpa-onnx streaming** runs continuously during TTS playback, giving us
   the user's first words within ~200ms. This is what we use for the barge-in
   classification.
2. **Whisper** (batch) runs after the user finishes speaking (1.5s silence) to
   produce the final high-quality transcription of the complete utterance.

Streaming models are ~85-90% of Whisper quality, which is fine for detecting
"yeah" vs "wait, actually..." but not ideal for the final transcript that gets
sent to the LLM. The two-pass approach gives us the best of both worlds.

Examples:
- Backchannel: "uh huh", "yeah", "right", "ok" -- resume playback
- Interruption: "wait, actually...", "stop", "no I meant...", a new question

### 6. Response Length

**Decision**: No artificial word limit. The agent should respond naturally.
If the user asks for a story, the agent tells a story. The voice mode prompt
should instruct conversational tone and progressive disclosure, but not impose
a hard word count.

### 7. Complex Content (Code, URLs, Lists)

**Decision**: The agent should not attempt to read code, URLs, file paths, or
long technical output aloud. Instead:

- The voice mode prompt instructs the agent to send complex content to
  the text channel (default: discord-dm)
- The agent should confirm with the user: "I have some code for you. Should
  I send it to the chat?" (defaulting to discord-dm if no preference stated)
- The agent uses the existing `send_message` tool to post to the text channel

### 8. Proactive Voice Messages

**Decision**: If the user is already in the voice channel with the bot, speak
proactive messages (scheduled tasks, notifications). If the user is not in the
voice channel, send text via DM. Never auto-join a voice channel to deliver a
message.

---

## Open-Source Reference Projects

### Best Architecture: abishop1990/discord-ai-voice (Python)

- **Stack**: whisper.cpp + LiteLLM + kokoro-onnx
- **DAVE E2EE**: Yes (built for March 2026)
- **Pipeline**:
  ```
  Discord Voice -> PCM buffer until 0.7s silence
      -> ffmpeg (in-memory) -> 16kHz mono WAV
      -> whisper.cpp HTTP server -> transcript
      -> name-based addressing gate (30s conversation window)
      -> streaming LLM -> sentence boundaries -> kokoro-onnx TTS
      -> FFmpegPCMAudio -> Discord playback
  ```
- **Latency**: ~500ms total (120ms STT + 300ms LLM first token + 80ms TTS)
- **Sentence-streaming TTS**: TTS for sentence N+1 runs while sentence N plays
- https://github.com/abishop1990/discord-ai-voice

### OpenAI Realtime Integration: KNQuoc/clod-voice (TypeScript)

- Uses **OpenAI Realtime API** for voice-to-voice (no separate STT/TTS)
- Discord Opus -> PCM 24kHz mono -> Base64 -> OpenAI Realtime WebSocket
- Server-side VAD handles turn detection
- ~1 second latency
- Complex requests routed to Claude via function calling
- https://github.com/KNQuoc/clod-voice

### Fully Local: SprtnDio/Complete-Local-Discord-AI-Voice-Chat-Bot

- **Stack**: Vosk ASR + Ollama/Llama 3 + Piper TTS
- Wake word triggered
- Everything runs offline
- https://github.com/SprtnDio/Complete-Local-Discord-AI-Voice-Chat-Bot

### Feature-Rich: CobCob047/discord-voice-ai-bot

- faster-whisper + Ollama + gTTS
- Wake word ("Hey Jarvis"), emotion detection, per-user analytics
- Greeting ceremony (toggleable), 200+ voice commands
- https://github.com/CobCob047/discord-voice-ai-bot

---

## Voice AI Frameworks

These are not Discord-specific but contain the best engineering patterns:

### LiveKit Agents (9.7k stars)

- Framework for realtime voice AI over WebRTC
- **Semantic turn detection**: transformer model (not just silence) to detect when user finished
- Pluggable STT/LLM/TTS, built-in Silero VAD
- Multi-agent handoff, push-to-talk, telephony/SIP
- https://github.com/livekit/agents

### Pipecat (10.7k stars)

- Composable pipeline framework for real-time conversational AI
- 50+ STT/LLM/TTS service integrations
- Speech-to-speech: OpenAI Realtime, Gemini Live, AWS Nova Sonic
- Silero VAD, Krisp noise filtering
- https://github.com/pipecat-ai/pipecat

---

## Real-Time Voice AI APIs

### Option A: STT + LLM + TTS Pipeline (Current Approach)

```
Discord Audio -> Whisper (STT) -> LLM -> Kokoro/Piper (TTS) -> Discord Audio
```

**Pros**: Full control, mix any engines, can run locally, cheapest at scale.
**Cons**: Higher latency, must implement turn-taking ourselves.

### Option B: OpenAI Realtime API (Speech-to-Speech)

```
Discord Audio -> PCM 24kHz -> OpenAI Realtime WebSocket -> Audio response -> Discord
```

**Pros**: Sub-second latency, built-in turn detection and barge-in, no STT/TTS
needed. **Cons**: Cloud-only, expensive (audio tokens), limited to OpenAI models,
less control.

### Comparison

| Factor | STT+LLM+TTS | OpenAI Realtime |
|--------|-------------|-----------------|
| Latency | ~500ms-1.5s (streaming) | ~300ms-1s |
| Turn-taking | Must implement | Built-in |
| Barge-in | Must implement | Built-in |
| Cost | Lower (text tokens) | Higher (audio tokens) |
| Model flexibility | Any STT/LLM/TTS | OpenAI only |
| Local/self-hosted | Yes | No |
| Voice quality | Wide selection | Limited voices |
| Control | Full | Limited |

**Recommendation**: Stay with STT+LLM+TTS pipeline. We already have the
infrastructure. The latency is acceptable with streaming. Consider OpenAI
Realtime as an optional alternative for users who want lowest latency and
don't mind the cost.

---

## Voice vs Text: How the Agent Must Behave Differently

This is critical. Sending a text response through TTS produces a terrible
voice experience. The agent needs a fundamentally different output style
when communicating via voice.

### Response Length

| Mode | Target | Example |
|------|--------|---------|
| Text | 50-500 words | Full explanation with code examples, links, lists |
| Voice | **15-40 words** (1-3 sentences) | Brief answer, offer to elaborate |

Amazon Alexa guideline: "Write responses that can be read out by a human in one
or two breaths." (~6-15 seconds of speech)

### Structure

**Never use in voice output**:
- Markdown formatting (bold, italic, headers)
- Bullet points or numbered lists (max 3 items if absolutely needed)
- Code blocks or inline code
- URLs, email addresses, file paths
- Long numbers (say "about twelve hundred" not "1,247")
- Tables

### Tone

| Text mode | Voice mode |
|-----------|-----------|
| Can be formal, technical | Must be conversational |
| Passive voice OK | Active voice always |
| Full words ("cannot", "will not") | Contractions ("can't", "won't") |
| Complete sentences with context | Short, direct statements |
| Can include caveats upfront | Lead with the answer |

### Progressive Disclosure

In text, you dump all the information. In voice, you give the headline and ask:

- **Text**: "Prague is a great city to visit. You should ideally spend 3-4 days.
  Here are the top attractions: 1. Charles Bridge 2. Prague Castle 3. Old Town
  Square 4. Jewish Quarter 5. Petrin Hill..."
- **Voice**: "You'd probably want 3 to 4 days in Prague. Want me to go through
  the top things to see?"

### Acknowledgment

Voice responses should start with brief acknowledgment when the user gives an
instruction or asks a question:

- "Got it, I'll set that up."
- "Sure, let me check."
- "OK, here's what I found."

This is critical for voice (fills the gap, signals understanding) but unnecessary
and annoying in text.

### Error Recovery (Misheard Speech)

Google's 3-level error model:

1. **1st error**: Brief rephrase. "Sorry, what was that?"
2. **2nd error**: Add examples. "I can help with things like weather, reminders,
   or questions. What would you like?"
3. **3rd error**: Graceful exit. "I'm having trouble hearing you. You can also
   type your question in the chat."

### Turn-Taking Signals

Voice responses should end with a clear signal:
- A question ("Would you like more detail?")
- A pause that indicates completion
- An explicit hand-off ("What else can I help with?")

Don't end with trailing thoughts that leave the user unsure whether to speak.

### Context Without Visual Reference

In text, users can scroll back. In voice, there's no history. The agent must:
- Track pronouns and references ("What about the second one?")
- Remind the user of context when needed ("Going back to the Prague trip...")
- Not assume the user remembers details from earlier in the conversation

### Key Research Sources

- **Amazon Alexa Design Guide**: https://developer.amazon.com/en-US/alexa/alexa-haus
- **Google Conversation Design**: https://developers.google.com/assistant/conversation-design/welcome
- **OpenAI Realtime Prompting Guide**: https://platform.openai.com/docs/guides/realtime-models-prompting
- **NN/g Study on Voice Assistants**: https://www.nngroup.com/articles/intelligent-assistant-usability/
- **Grice's Cooperative Principle (1975)**: Foundation of Google's design -- Quality, Quantity, Relevance, Manner

---

## System Prompt Modifications for Voice Mode

When the agent detects it's responding to a voice channel, the system prompt
should include additional voice-specific instructions. This should be injected
dynamically based on the channel type in the `InboundMessage`.

### Voice Mode Prompt

```
You are currently speaking to the user via VOICE. Your responses will be
read aloud through text-to-speech. Adjust your communication:

1. CONVERSATIONAL TONE: Use contractions, active voice, plain language.
   Speak as you would to a friend. Avoid unnecessary jargon.

2. NO FORMATTING: Never use markdown, bullet points, numbered lists, code
   blocks, URLs, file paths, or email addresses. Everything you say will
   be spoken aloud -- write in natural flowing speech.

3. PROGRESSIVE DISCLOSURE: When a topic has a lot of detail, give the key
   point first, then offer to go deeper. Don't front-load with caveats.

4. NUMBERS: Say "about twelve hundred" not "1,247". Round when possible.
   Spell out abbreviations.

5. COMPLEX CONTENT: You cannot speak code, URLs, tables, or long technical
   output. Instead, ask the user if you should send it to the chat (use
   the send_message tool targeting discord-dm). Say something like "I have
   some code for that. Want me to send it to the chat?"

6. NATURAL LENGTH: Respond naturally -- short answers for simple questions,
   longer responses when the user asks for stories, explanations, or detail.
   Don't artificially truncate, but don't pad either.
```

### How to Inject This

The voice mode prompt should be injected in `AgentRuntime` when building the
system prompt, based on the `ChannelType` from the `InboundMessage`. This is
similar to how memory context is injected -- it's a virtual addition to the
system prompt, not stored in conversation history.

```csharp
if (message.ChannelType == ChannelType.Discord && isVoiceConversation)
{
    systemPrompt += "\n\n" + VoiceModePrompt;
}
```

The channel type and voice flag need to flow through `HubInboundMessage` so the
agent knows whether to apply voice mode.

---

## Multi-Modal: Voice + Text Together

When a user is in a Discord voice channel, they also have access to text channels.
This opens up a powerful multi-modal pattern.

### The Pattern

- **Voice gives the summary**: "I found 3 functions that match. I've posted the
  code in the chat."
- **Text provides the detail**: Code blocks, file contents, URLs, tables

### Implementation

When the agent is in voice mode and needs to output complex content:

1. Speak a brief summary via voice
2. Send the detailed content (code, lists, URLs) to the guild text channel
3. Reference it: "Check the chat for the full list."

This requires the agent to know it can send to both channels simultaneously.
The `SendMessageTool` already supports targeting specific channels -- this
pattern can be achieved by having the agent call `send_message` to the text
channel while its voice response goes to the voice channel.

### Amazon's Guidance

"Avoid reading out lengthy data that can be sent to the customer's app."
When a screen is available, "shorten the VUI response because the customer
can see the additional detail on screen."

### Google's Framework

| Device context | Strategy |
|---------------|----------|
| Audio-only (smart speaker) | Voice carries everything |
| Audio + screen (phone, Discord) | Voice = summary, screen = detail |

---

## Latency Budget

### Human Expectations

Research on conversational turn-taking (Ed Yong, The Atlantic, 2016):

- Average gap between human conversation turns: **200 milliseconds**
- People start planning their reply before the other finishes speaking
- This 200ms is near-universal across languages and cultures

### Acceptable AI Response Latency

| Range | Perception |
|-------|-----------|
| < 500ms | Feels instantaneous, like human conversation |
| 500ms - 1s | Acceptable, natural |
| 1-2s | Noticeable delay, still tolerable |
| 2-4s | Uncomfortable, needs filler ("Let me check...") |
| > 4s | Unacceptable without explicit thinking signal |

### Our Pipeline Latency (Estimated)

| Stage | Estimate | Notes |
|-------|----------|-------|
| Silence detection | 700-1000ms | Configurable threshold |
| Resample + Whisper STT | 100-300ms | Depends on audio length |
| LLM first token | 300-800ms | Depends on provider |
| TTS first audio | 50-200ms | Kokoro is fast |
| **Total to first audio** | **~1.2-2.3s** | After silence threshold |

### Reducing Perceived Latency

1. **Filler phrases**: Before processing, immediately speak "Let me check" or
   "One moment" while the STT/LLM pipeline runs. OpenAI explicitly recommends
   this for tool calls.
2. **Sentence-level streaming TTS**: Start playing TTS for the first sentence
   while the LLM is still generating the rest. The abishop1990 project does
   this effectively.
3. **Earcons**: Brief audio cue (ding, chime) when the bot starts listening or
   starts processing. Google calls these "earcons."

---

## Common Mistakes and Lessons Learned

### From NN/g User Study (2018)

The NN/g study tested Alexa, Google Assistant, and Siri with real users. Key
findings relevant to us:

1. **"It doesn't shut up"** -- Users' #1 complaint. They cannot interrupt the
   assistant mid-response. **We must implement barge-in.**

2. **"I have to think like a robot"** -- Users feel they must carefully
   pre-formulate queries. Natural pauses mid-sentence cause the assistant to
   jump in prematurely. **Our silence threshold must be tuned carefully (0.7-1s,
   not too aggressive).**

3. **"It gave me links, not answers"** -- Users want a spoken answer, not a
   redirect. **Voice responses must be self-contained.**

4. **"Too chatty"** -- Extra information, verbose confirmations, unsolicited
   detail. **The voice mode prompt must enforce brevity.**

5. **"I don't trust the answer"** -- No supporting evidence for why the
   assistant chose a particular result. **When possible, briefly cite source
   ("According to the docs..." or "Based on your earlier preference...").**

### From Amazon Alexa Design Guide

1. **Don't teach commands** -- "Don't tell the customer explicitly what to say."
   Speaking is intuitive. Avoid IVR-style "Say 'yes' or 'no'."

2. **Don't monopolize** -- Ask one question at a time. Don't stack options and
   questions. End your turn and let the user speak.

3. **Don't blame the user** -- "That's not valid" is hostile. "I didn't catch
   that, could you say it again?" is better.

4. **Don't over-confirm** -- For repetitive low-stakes actions, minimal
   confirmation. Don't say "I've added milk to your shopping list" every single
   time in a rapid sequence.

5. **Test by listening** -- "Listen to the prompts in Alexa's voice to ensure
   they don't sound unintentionally awkward." Many developers only read their
   dialog, never hear it.

### From Google Conversation Design

1. **Grice's Maxims** (foundation of Google's design): Quality (be truthful),
   Quantity (right amount of info), Relevance (stay on topic), Manner (be clear).

2. **Error escalation** -- Never repeat the same error prompt. Escalate:
   brief rephrase -> add examples/options -> graceful exit to text.

3. **Handle cooperative users** -- Users often volunteer extra information.
   Systems that ignore it and re-ask the same question are infuriating.

4. **Discourse markers** -- "Sure," "OK," "Got it," "Alright" are essential in
   voice. They signal the agent heard the user and is transitioning. Skip them
   in text.

### From OpenAI Realtime API Guide

1. **Filler before tool calls** -- "Before any tool call, say one short line
   like 'I'm checking that now.' Then call the tool immediately."

2. **Personality in system prompt** -- "Tone: Warm, concise, confident, never
   fawning. 2-3 sentences per turn."

3. **Pacing instructions work** -- "Deliver your audio response fast, but do not
   sound rushed."

---

## Detailed Technical Design

### Full-Duplex Architecture

Discord.Net's `IAudioClient` is inherently full-duplex:

- **Output**: `_audioOutStream` (from `CreatePCMStream`) -- write TTS audio
- **Input**: `StreamCreated` event → per-user `AudioInStream` -- read user audio

These are independent I/O paths over the same UDP voice connection. The
`ReceiveUserAudioAsync` loop runs on a background `Task.Run` continuously,
independent of `SendVoiceAsync` writing to the output stream.

**The current implementation is logically half-duplex** -- `ReceiveUserAudioAsync`
has no awareness that the bot is speaking, and `SendVoiceAsync` is a blocking
write loop with no interruption mechanism. To support barge-in and simultaneous
listen+speak, we need:

1. A shared `_isSpeaking` flag set by the TTS output loop
2. `SendVoiceAsync` must be frame-interruptible (check a `_bargeInRequested`
   flag between each 20ms frame write)
3. `ReceiveUserAudioAsync` must detect user speech during `_isSpeaking` and
   trigger the barge-in flow

### Always-On Full-Duplex STT via EchoSharp

The voice handler is always in full-duplex mode:

- **Always listening**: STT runs continuously on all incoming user audio
- **Always ready to speak**: audio output is available at any time
- No modes, no state transitions for "start listening" / "stop listening"

STT uses **EchoSharp** (by the same author as Whisper.net), which orchestrates:

1. **Silero VAD** -- detects speech segments in the audio stream
2. **sherpa-onnx streaming** (`OnlineRecognizer`) -- produces partial
   transcription in real-time as audio arrives (<200ms for first words)
3. **Whisper refinement** (optional) -- after silence is detected, runs a
   batch Whisper pass on the completed utterance for higher-quality final
   transcript

```
User audio (48kHz mono PCM, 20ms frames from Discord)
    ↓ resample to 16kHz on the fly
    ↓
EchoSharp pipeline (always running)
    ├── Silero VAD: detects speech start/end
    ├── sherpa-onnx streaming: partial transcript in real-time
    │       → available immediately for barge-in classification
    │       → available for any real-time feedback
    └── Whisper refinement (on speech end): high-quality final transcript
            → ~100-300ms after silence detection
            → this is what gets sent to the LLM
```

**Why this works for full-duplex**: The STT runs whether the agent is speaking
or silent. During agent speech, if the user talks, we have partial words within
~200ms -- immediately available for barge-in classification. No special "barge-in
mode" needed; we just check the streaming partial results whenever user audio
is detected during TTS playback.

**EchoSharp NuGet packages**:
- `EchoSharp` -- core orchestration
- `EchoSharp.Onnx.Sherpa` -- sherpa-onnx streaming STT backend
- `EchoSharp.Whisper.net` -- Whisper batch STT backend (refinement)
- `EchoSharp.Onnx.SileroVad` -- Silero VAD

GitHub: https://github.com/sandrohanea/echosharp

### New Interface: IStreamingSpeechToText

```csharp
// Cortex.Contained.Speech/IStreamingSpeechToText.cs
public interface IStreamingSpeechToText : IDisposable
{
    /// <summary>Feed a chunk of 16kHz mono 16-bit PCM audio.</summary>
    void AcceptAudio(ReadOnlySpan<byte> pcm16kMono);

    /// <summary>Current partial transcription (updates as more audio is fed).</summary>
    string GetPartialResult();

    /// <summary>Finalize current utterance, return best result, reset for next.</summary>
    string GetFinalResult();

    /// <summary>Discard accumulated audio and reset recognizer state.</summary>
    void Reset();

    bool IsReady { get; }
}
```

The implementation wraps EchoSharp's pipeline. `GetPartialResult()` returns
the sherpa-onnx streaming result. `GetFinalResult()` triggers the Whisper
refinement pass and returns the high-quality transcript.

### DiscordVoiceHandler Architecture

The handler has **two independent substates** that run in parallel:

```csharp
enum ListeningState { Idle, Hearing, Processing }
enum SpeakingState  { Silent, Playing }
```

These are **orthogonal** -- any combination is valid at any time:

| Listening | Speaking | Scenario |
|-----------|----------|----------|
| Idle | Silent | Nobody talking, waiting |
| Hearing | Silent | User speaking, agent quiet |
| Idle | Playing | Agent speaking, user silent (normal) |
| Processing | Silent | User finished, running STT + sending to agent |
| **Hearing** | **Playing** | **Barge-in: user speaks during agent playback** |
| Processing | Playing | Agent responding to previous while new message processes |

Each substate is managed by its own loop. They only interact at one point:
**barge-in** (when `Hearing` + `Playing` coincide).

```
┌─────────────────────────────────────────────────────────────┐
│                   DiscordVoiceHandler                        │
│                                                             │
│  LISTENING SIDE                   SPEAKING SIDE             │
│  (always running)                 (when there's something   │
│                                    to say)                  │
│                                                             │
│  ┌──────────────────────┐    ┌────────────────────────────┐ │
│  │   RECEIVE LOOP       │    │    TTS PRODUCER            │ │
│  │                      │    │                            │ │
│  │ Discord AudioIn      │    │ Channel<string> sentences  │ │
│  │   ↓                  │    │   ↓                        │ │
│  │ Resample 48k→16k     │    │ Synthesize sentence → PCM  │ │
│  │   ↓                  │    │   ↓                        │ │
│  │ EchoSharp pipeline   │    │ Resample → 48kHz           │ │
│  │ (VAD+streaming STT)  │    │   ↓                        │ │
│  │   ↓                  │    │ Enqueue to playback        │ │
│  │ Partial results      │    └──────────────┬─────────────┘ │
│  │   ↓                  │                   │               │
│  │ 1.5s silence:        │    ┌──────────────v─────────────┐ │
│  │  → final transcript  │    │    PLAYBACK LOOP           │ │
│  │  → ack + send agent  │    │                            │ │
│  │                      │    │ Channel<byte[]> audio      │ │
│  │ Speech during Playing│    │   ↓                        │ │
│  │  → barge-in check ───│────│→ Check _bargeIn each frame │ │
│  │                      │    │   ↓                        │ │
│  └──────────────────────┘    │ Write 20ms frame to        │ │
│                              │ Discord AudioOut           │ │
│                              └────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

**Three concurrent loops**:

1. **Receive loop** (manages `ListeningState`): reads 20ms frames from Discord,
   resamples to 16kHz, feeds to EchoSharp. Always running.
   - `Idle → Hearing`: EchoSharp VAD detects speech
   - `Hearing → Processing`: 1.5s silence after speech
   - `Processing → Idle`: transcript dispatched to agent

2. **TTS producer** (part of `SpeakingState`): reads complete sentences from
   `Channel<string>`, synthesizes via TTS, resamples to 48kHz, enqueues audio
   chunks to the playback queue.

3. **Playback loop** (manages `SpeakingState`): reads audio chunks from
   `Channel<byte[]>`, writes 20ms frames to Discord AudioOut.
   - `Silent → Playing`: audio chunk dequeued
   - `Playing → Silent`: all queued audio played (or discarded on interrupt)
   - Checks `_bargeInRequested` between every frame write

### How Each Scenario Works

**User speaks (agent silent)** — `Hearing + Silent`:

```
Listening: Idle → Hearing → Processing → Idle
Speaking:  Silent (throughout)

1. Audio arrives → EchoSharp VAD detects speech → Hearing
2. sherpa-onnx streaming produces partial transcript in real-time
3. User pauses for 1.5s → Processing
4. EchoSharp runs Whisper refinement → high-quality final transcript
5. Speak acknowledgment ("Got it") → enqueue to TTS producer
6. Build InboundMessage with Properties["voice"]="true"
7. Fire _onTranscription → agent pipeline → Idle
8. Agent response streams in via IChannelWithStreaming
9. Sentences enqueued to TTS producer → playback loop plays them
```

**Agent speaking, user silent** — `Idle + Playing`:

```
Listening: Idle (throughout)
Speaking:  Silent → Playing → Silent

1. LLM generates tokens → DiscordChannel.SendStreamingUpdateAsync
2. Voice handler accumulates text in StringBuilder
3. Sentence boundary detected (. ! ?)
4. Complete sentence enqueued to TTS producer → Playing
5. TTS producer synthesizes → audio queue → playback loop
6. Meanwhile LLM continues → next sentence → concurrent synthesis
7. On FinalizeStreamingAsync: flush remaining partial sentence
8. All audio played → Silent
```

**Barge-in** — `Hearing + Playing`:

```
Listening: Idle → Hearing
Speaking:  Playing → (paused) → Playing or Silent

1. Playback loop is writing frames (Playing)
2. Audio arrives → EchoSharp VAD detects speech → Hearing
3. sherpa-onnx streaming produces partial words within ~200ms
4. Receive loop sets _bargeInRequested = true
5. Playback loop sees flag on next frame → pauses
6. Once partial text available from streaming STT:
   LLM classification (memory model): "interrupt or continue?"

7a. "continue":
    _bargeInRequested = false → playback resumes (Playing)
    Listening: Hearing → Idle (user stopped talking)

7b. "interrupt":
    Drain TTS producer + playback queues (discard all pending audio) → Silent
    Listening: Hearing continues → 1.5s silence → Processing → dispatch
```

### Streaming TTS Pipeline (LLM → Sentence → TTS → Audio)

Currently `SendVoiceAsync` receives the **complete** agent response text, calls
`_tts.SynthesizeAsync` on the whole thing (batch), waits for all audio, then
plays it. This means the user waits for: full LLM generation + full TTS
synthesis + playback start. For a 200-word response, that's several seconds
of dead silence.

**The problem is at two levels**:

1. **LLM → Channel**: `DiscordChannel` does not implement `IChannelWithStreaming`.
   The `HubMessageDispatcher` accumulates all LLM chunks and only sends the
   complete text via `SendMessageAsync` after the LLM finishes. The voice
   handler doesn't see any text until generation is complete.

2. **TTS → Audio**: Even within `SendVoiceAsync`, it batch-synthesizes the
   entire text before playing any audio. Both Kokoro and Piper already support
   `SynthesizeStreamingAsync` (yields audio per sentence), but we don't use it.

**Target pipeline**:

```
LLM streams tokens
    ↓
Accumulate in sentence buffer
    ↓ (sentence boundary: . ! ? detected)
TTS synthesize sentence N → play audio
    ↓ (concurrent)
LLM continues → accumulate sentence N+1
    ↓ (sentence N audio finishes)
Play sentence N+1 audio (already synthesized or nearly ready)
```

**Changes needed**:

a. **`DiscordChannel` implements `IChannelWithStreaming`**. On `SendStreamingUpdateAsync`,
   feed text chunks to a sentence accumulator on the voice handler.

b. **New method: `DiscordVoiceHandler.AcceptTextChunk(string chunk)`**. Appends to
   a `StringBuilder`. After each append, scans for sentence boundaries. When a
   complete sentence is found, enqueues it into a `Channel<string>` for the TTS
   producer.

c. **TTS producer loop** (background task): reads sentences from the channel,
   synthesizes each one via `_tts.SynthesizeAsync(sentence)` (or uses
   `SynthesizeStreamingAsync` for sub-sentence streaming), resamples to 48kHz,
   and enqueues audio chunks into a `Channel<byte[]>` for the playback loop.

d. **Audio playback loop** (background task): reads audio chunks from the channel
   and writes to `_audioOutStream` frame-by-frame (with barge-in checking
   between frames).

e. **`FinalizeStreamingAsync`** signals end-of-response to flush any remaining
   partial sentence.

```
                    ┌─────────────────┐
LLM chunks ──────→ │ Sentence Buffer │
                    │ (StringBuilder) │
                    └───────┬─────────┘
                            │ complete sentences
                            v
                    ┌─────────────────┐
                    │  TTS Producer   │ Channel<string>
                    │  (synthesize)   │
                    └───────┬─────────┘
                            │ PCM audio chunks
                            v
                    ┌─────────────────┐
                    │ Audio Playback  │ Channel<byte[]>
                    │ (write frames)  │
                    │ (barge-in check)│
                    └─────────────────┘
```

**Latency reduction**: Instead of waiting for the full LLM response (2-5s) +
full TTS (1-3s) = 3-8 seconds, the user hears the first sentence after:
first sentence LLM generation (~0.5-1s) + TTS for that sentence (~0.1-0.3s)
= **~0.6-1.3s** after the LLM starts generating.

### Shared State and Queues

```csharp
// ── Substates ──
private volatile ListeningState _listeningState = ListeningState.Idle;  // managed by receive loop
private volatile SpeakingState _speakingState = SpeakingState.Silent;    // managed by playback loop

enum ListeningState { Idle, Hearing, Processing }
enum SpeakingState  { Silent, Playing }

// ── Concurrent queues (thread-safe producer/consumer) ──
private readonly Channel<string> _sentenceQueue;     // TTS producer reads from this
private readonly Channel<byte[]> _audioQueue;         // Playback loop reads from this

// ── Cross-thread signaling (receive loop → playback loop) ──
private volatile bool _bargeInRequested;   // set by receive loop when Hearing + Playing

// ── STT (always running, fed by receive loop) ──
private readonly IStreamingSpeechToText _streamingStt;  // EchoSharp: VAD + sherpa + Whisper
```

The two substates are orthogonal. Each loop reads/writes only its own substate.
The only cross-loop interaction is `_bargeInRequested`, which the receive loop
sets when it detects `_listeningState == Hearing && _speakingState == Playing`,
and the playback loop checks between frame writes.

### Greeting

In `JoinVoiceChannelAsync`, after audio client is connected:

```csharp
if (!string.IsNullOrEmpty(_options.VoiceGreeting))
{
    await SendVoiceAsync(_options.VoiceGreeting, CancellationToken.None);
}
```

New option:
```csharp
// DiscordChannelOptions
public string? VoiceGreeting { get; init; } = "Hey! I'm here if you need anything.";
```

### Acknowledgment

After Whisper transcription completes, before dispatching to agent:

```csharp
private static readonly string[] Acknowledgments =
    ["Got it.", "Sure.", "Let me think.", "One moment.", "OK."];

// In ProcessUserAudioAsync, after transcription:
var ack = Acknowledgments[Random.Shared.Next(Acknowledgments.Length)];
await SendVoiceAsync(ack, CancellationToken.None);  // blocks until spoken
// Then dispatch to agent
await _onTranscription(inbound);
```

The acknowledgment is spoken synchronously before the agent starts processing.
This fills the 1-3 second gap while the LLM generates a response.

### Voice Mode System Prompt

**Data flow through the stack**:

```
DiscordVoiceHandler.ProcessUserAudioAsync
    InboundMessage.Properties["voice"] = "true"
        ↓
DiscordChannel.MessageReceived
        ↓
HubMessageDispatcher.OnChannelMessageReceived
    Maps Properties["voice"] → HubInboundMessage.IsVoice = true
        ↓
AgentHub.SendMessage → AgentRuntime.HandleMessageAsync
    AgentMessage.IsVoice = true
        ↓
AgentRuntime.BuildPrompt
    if (isVoice) systemPrompt += VoiceModeInstructions
```

Changes needed per file:

| File | Change |
|------|--------|
| `HubTypes.cs` | Add `bool IsVoice` to `HubInboundMessage` |
| `HubMessageDispatcher.cs` | Map `Properties["voice"]` → `IsVoice` |
| `AgentRuntime.cs` (AgentMessage) | Add `bool IsVoice` |
| `AgentRuntime.cs` (HandleMessageAsync) | Pass `IsVoice` through |
| `AgentRuntime.cs` (BuildPrompt) | Inject voice mode prompt when `IsVoice` |

### Complex Content → DM

Handled entirely by the voice mode system prompt. The agent already has the
`send_message` tool which can target `discord-dm`. The prompt instructs:

> "For code, URLs, or long technical output, say 'I'll send that to the chat'
> and use send_message to post it to discord-dm."

No code changes needed beyond the prompt injection.

### Files to Change

| File | Change |
|------|--------|
| **New**: `Cortex.Contained.Speech/IStreamingSpeechToText.cs` | Streaming STT interface |
| **New**: `Cortex.Contained.Speech/Stt/SherpaStreamingSpeechToText.cs` | sherpa-onnx OnlineRecognizer impl |
| `Cortex.Contained.Speech/SpeechOptions.cs` | Streaming STT model path config |
| `Cortex.Contained.Contracts/Hub/HubTypes.cs` | `bool IsVoice` on `HubInboundMessage` |
| `Cortex.Contained.Bridge/Channels/HubMessageDispatcher.cs` | Map `Properties["voice"]` → `IsVoice` |
| `Cortex.Contained.Channels.Discord/DiscordChannelOptions.cs` | `VoiceGreeting` property |
| `Cortex.Contained.Channels.Discord/DiscordVoiceHandler.cs` | Major rewrite: state machine, full-duplex, streaming TTS pipeline, streaming STT, barge-in, greeting, ack |
| `Cortex.Contained.Channels.Discord/DiscordChannel.cs` | Implement `IChannelWithStreaming`, pass streaming STT to voice handler, route LLM chunks to voice handler |
| `Cortex.Contained.Agent.Host/Agent/AgentRuntime.cs` | Voice prompt in BuildPrompt, IsVoice on AgentMessage |

### Dependencies

| Dependency | Status | Notes |
|------------|--------|-------|
| `EchoSharp` | **New NuGet** | Core orchestration library |
| `EchoSharp.Onnx.Sherpa` | **New NuGet** | sherpa-onnx streaming STT backend |
| `EchoSharp.Whisper.net` | **New NuGet** | Whisper refinement backend |
| `EchoSharp.Onnx.SileroVad` | **New NuGet** | Silero VAD |
| `org.k2fsa.sherpa.onnx` | Already in deps | Used by EchoSharp.Onnx.Sherpa |
| `Whisper.net` | Already in deps | Used by EchoSharp.Whisper.net |
| Zipformer streaming model | **Download needed** | ~70MB (encoder.onnx, decoder.onnx, joiner.onnx, tokens.txt) |
| Silero VAD model | **Download needed** | ~2MB (silero_vad.onnx) |

---

## Implementation Phases

### Phase 1: Full-Duplex Voice Handler

The big rewrite. Replaces the current half-duplex `DiscordVoiceHandler` with
the always-on full-duplex architecture:

**a. Always-on STT** (EchoSharp):
- Add `IStreamingSpeechToText` interface + EchoSharp implementation
- Receive loop feeds all audio to EchoSharp continuously
- VAD + sherpa-onnx streaming + Whisper refinement
- On 1.5s silence: final transcript → dispatch to agent

**b. Streaming TTS pipeline**:
- `DiscordChannel` implements `IChannelWithStreaming`
- LLM chunks arrive in real-time via `SendStreamingUpdateAsync`
- Voice handler accumulates text, detects sentence boundaries
- Sentence → TTS producer (`Channel<string>`) → synthesize → audio
  queue (`Channel<byte[]>`) → playback loop
- Frame-interruptible playback (checks `_bargeInRequested` between frames)

**c. Voice mode system prompt**:
- Wire `IsVoice` flag through the stack
- Inject voice mode instructions in `BuildPrompt`

**d. Greeting + acknowledgment**:
- `VoiceGreeting` option, speak on join
- Random acknowledgment phrase before dispatching to agent

### Phase 2: Barge-In

- Receive loop detects speech during `_isSpeaking`
- Streaming STT already running → partial words available immediately
- Pause playback, classify via LLM (memory model)
- Resume or discard based on classification

### Phase 3: Polish

- Verify complex content routing to DM via `send_message` tool
- Test and tune silence threshold, VAD sensitivity, barge-in timing
- Test sentence boundary detection edge cases
- Handle edge cases: agent starts responding while user starts new utterance
- Consider earcon (audio cue) when bot starts listening

---

## References

### Official Design Guidelines

| Source | URL |
|--------|-----|
| Amazon Alexa Design Guide | https://developer.amazon.com/en-US/alexa/alexa-haus |
| Amazon "Be Brief" | https://developer.amazon.com/en-US/alexa/alexa-haus/design-principles/be-brief |
| Amazon "Be Natural" | https://developer.amazon.com/en-US/alexa/alexa-haus/design-principles/be-natural |
| Amazon "Be Multimodal" | https://developer.amazon.com/en-US/alexa/alexa-haus/design-principles/be-multimodal |
| Amazon Lists Pattern | https://developer.amazon.com/en-US/alexa/alexa-haus/patterns-and-components/patterns-lists |
| Amazon Error Handling | https://developer.amazon.com/en-US/alexa/alexa-haus/patterns-and-components/patterns-errors |
| Google Conversation Design | https://developers.google.com/assistant/conversation-design/welcome |
| Google "Learn About Conversation" | https://developers.google.com/assistant/conversation-design/learn-about-conversation |
| Google Error Handling | https://developers.google.com/assistant/conversation-design/errors |
| Google Multi-Device Design | https://developers.google.com/assistant/conversation-design/scale-your-design |
| Apple Siri HIG | https://developer.apple.com/design/human-interface-guidelines/siri |
| OpenAI Realtime API | https://platform.openai.com/docs/guides/realtime |
| OpenAI Realtime Prompting | https://platform.openai.com/docs/guides/realtime-models-prompting |

### Research and Articles

| Source | URL |
|--------|-----|
| NN/g "Intelligent Assistants Have Poor Usability" | https://www.nngroup.com/articles/intelligent-assistant-usability/ |
| Ed Yong, "The Incredible Thing We Do During Conversations" | https://www.theatlantic.com/science/archive/2016/01/the-incredible-thing-we-do-during-conversations/422439/ |
| Julie Beck, "The Secret Life of 'Um'" | https://www.theatlantic.com/science/archive/2017/12/the-secret-life-of-um/547961/ |
| Grice's Cooperative Principle (1975) | http://www.ucl.ac.uk/ls/studypacks/Grice-Logic.pdf |
| James Giangola, "Conversation Design: Speaking the Same Language" | https://design.google/library/conversation-design-speaking-same-language |
| DAVE Protocol Whitepaper | https://daveprotocol.com |

### Discord Technical References

| Source | URL |
|--------|-----|
| Discord Voice Connections | https://discord.com/developers/docs/topics/voice-connections |
| Discord libdave (E2EE) | https://github.com/discord/libdave |
| Discord.Net | https://github.com/discord-net/Discord.Net |

### Open-Source Voice Bot Projects

| Project | Language | URL |
|---------|----------|-----|
| abishop1990/discord-ai-voice | Python | https://github.com/abishop1990/discord-ai-voice |
| KNQuoc/clod-voice | TypeScript | https://github.com/KNQuoc/clod-voice |
| CobCob047/discord-voice-ai-bot | Python | https://github.com/CobCob047/discord-voice-ai-bot |
| SprtnDio/Complete-Local-Discord-AI-Voice-Chat-Bot | JavaScript | https://github.com/SprtnDio/Complete-Local-Discord-AI-Voice-Chat-Bot |
| KickerMix/Discord-Local-LLM-VoiceChat-Bot | Python | https://github.com/KickerMix/Discord-Local-LLM-VoiceChat-Bot |

### Voice AI Frameworks

| Framework | Stars | URL |
|-----------|-------|-----|
| LiveKit Agents | 9.7k | https://github.com/livekit/agents |
| Pipecat | 10.7k | https://github.com/pipecat-ai/pipecat |
