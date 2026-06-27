# Voice speaker identification

A how-it-works explainer for the speaker-ID gate. For the full design rationale and decisions, see `docs/superpowers/specs/2026-05-11-voice-speaker-identification-design.md`.

## What problem does it solve?

When the agent is in a Discord voice channel (or listening through a microphone on the host), it hears whoever happens to be talking — the tenant owner, other people in the room, audio from a YouTube tab, anyone. Without speaker ID, every transcript reaches the agent and influences the conversation.

The gate's job is simple: **only let transcripts of the enrolled owner's voice through.** Everything else is silently dropped at the channel boundary, before it ever reaches the agent.

It is a **discriminator**, not a recognizer. It does not transcribe, it does not detect emotion, it does not identify *who* an unknown speaker is. It answers one yes/no question per utterance: "is this the same person we enrolled?"

## The model

### ERes2NetV2

The model used is **ERes2NetV2** from Alibaba's **3D-Speaker** toolkit, published on ModelScope as `iic/speech_eres2netv2_sv_zh-cn_16k-common`. Architecture in two sentences:

- **Res2Net** is a ResNet variant whose convolutional blocks split the channel dimension into groups and connect them in a hierarchical chain, so each block extracts features at multiple temporal scales simultaneously. "**Enhanced Res2Net**" (ERes2Net) adds local + global feature fusion between scales and attention-statistics pooling at the end. **V2** further refines the attention pooling and the loss formulation.
- The result is a single fixed-length vector (an **embedding**, typically 192 floats for the base export) that encodes the speaker's voice timbre — pitch range, formants, vocal tract characteristics — independent of the words they spoke.

We use the **ONNX** export (≈70 MB on disk) so it can be loaded by **Microsoft.ML.OnnxRuntime** from .NET on both Windows (Bridge) and Linux (Agent.Host container) without a Python dependency.

### Model contract

| Property | Value | Set by |
|---|---|---|
| Input name | `feats` (configurable via `SpeakerId:InputName`) | `OnnxSpeakerEmbedder` ctor |
| Input shape | `[1, time, 80]` float32 | log-mel fbank, 80 mel bins |
| Input rate | 16 kHz mono PCM | resampled by channel before fbank |
| Output name | `embed` (configurable via `SpeakerId:OutputName`) | `OnnxSpeakerEmbedder` ctor |
| Output shape | `[1, 192]` float32, L2-normalised | `SpeakerIdOptions` |
| File path | `eres2netv2-base.onnx` | `SpeakerId:ModelPath` (both processes) |

After inference, the wrapper L2-normalises the output even if the model already produces unit vectors. That makes cosine similarity well-defined for every embedding regardless of how the model was trained.

## How comparison works

Once you have a 192-dim unit vector per utterance, comparison is a single dot product:

```
score = e_utterance · e_voiceprint     // ∈ [-1, 1], typically [0.2, 0.95] in practice
accept if score ≥ threshold
```

The default threshold is **0.55** (cosine), tuned for ERes2NetV2 base. Per-tenant overrides exist in `VoiceprintRecord.ThresholdOverride` for users who want to tighten (fewer false accepts, more false rejects) or loosen (the inverse).

The enrolled voiceprint stored in the SQLite table is the **mean of the L2-normalised per-utterance embeddings** from the enrollment captures (3 by default), itself L2-normalised. So the stored voiceprint is just another 192-d unit vector — there is no special "speaker model" object.

## Language requirements

**The feature is language-independent.** Two layers below explain why and what the caveats are:

1. **Front-end features.** Log-mel fbank (80 mel bins, 25 ms window, 10 ms hop, 16 kHz) is purely an acoustic representation. It encodes spectral shape — vowel formants, fricative energy, pitch — and contains no phonetic or lexical information. The same enrolled person speaking English, Russian, or humming through their nose produces fbank frames that the embedder can map into the same region of the embedding space.

2. **Embedder training data.** ERes2NetV2 was trained on Mandarin Chinese speech (CN-Celeb dataset). In speaker-recognition literature this introduces a measured but not large cross-lingual penalty: the equal-error rate climbs by a few percent when evaluating on, say, English speakers, but the model still discriminates strongly. The embedding learned is "what makes voices sound different from each other," and that geometry transfers across languages — much more than, say, an ASR model would.

So in practice: enrollment and verification work for any language the speaker uses, and you can mix languages (enroll in Russian, verify in English) without re-enrollment. If FRR (false reject rate) on a non-CN speaker turns out unacceptable, options are:

- Raise the per-tenant threshold's *lower* bound by **lowering** `ThresholdOverride` (more accepts).
- Re-enroll with more diverse samples.
- Swap the model for a WeSpeaker / NeMo / pyannote export trained on multilingual data — the contract is the same (80-d fbank in, embedding out); only `SpeakerId:EmbeddingDim`/`InputName`/`OutputName` may change.

There is **no STT involved in speaker ID**. STT (Whisper) is a separate, parallel path. The two are joined only at the dispatch decision after both finish.

## End-to-end flow

```
                       ┌──────────────────────────────┐
                       │      Discord voice room      │
                       │      (or host microphone)    │
                       └──────────────┬───────────────┘
                                      │ Opus → PCM16
                                      │ 16 kHz mono
                                      ▼
                  ┌─────────────────────────────────────────┐
                  │       Bridge: voice channel             │
                  │  - VAD / silence trim                   │
                  │  - utterance commit (~1 s of voice)     │
                  └──────────────┬──────────────────────────┘
                                 │ pcm16 bytes
                                 ▼
                  ┌─────────────────────────────────────────┐
                  │  At each utterance, run in parallel:    │
                  │                                         │
                  │   ┌──────────────┐  ┌──────────────┐    │
                  │   │ STT (Whisper)│  │ Speaker gate │    │
                  │   │  → text      │  │  → Accept/   │    │
                  │   │              │  │    Reject/   │    │
                  │   │              │  │    Skipped   │    │
                  │   └──────┬───────┘  └──────┬───────┘    │
                  │          │                 │            │
                  │          └────────┬────────┘            │
                  │                   ▼                     │
                  │       gate.PassesTranscript?            │
                  │       false → drop, never dispatch      │
                  │       true  → forward to agent          │
                  └─────────────┬───────────────────────────┘
                                │ SignalR
                                ▼
                  ┌─────────────────────────────────────────┐
                  │           Agent.Host                    │
                  │  - LLM tool loop, conversation,         │
                  │    memory writes, scheduled tasks       │
                  └─────────────────────────────────────────┘
```

The gate runs **in parallel** with STT, not in series. STT and embedding both take ~100–300 ms on typical hardware; running them sequentially would double the floor latency for every utterance. The dispatch decision waits for both. A 1.5 s hard timeout on the verifier means if the model hangs the user is never silenced — `Skipped(Error)` is treated as a pass.

## Inside the gate

```
                pcm16, 16 kHz mono, ≥800 ms after silence trim
                              │
                              ▼
            ┌────────────────────────────────────┐
            │   FbankExtractor                   │
            │   25 ms window, 10 ms hop          │
            │   512-pt FFT, 80 mel bins,         │
            │   log + cepstral mean normalise    │
            │   → [numFrames, 80] float32        │
            └─────────────┬──────────────────────┘
                          │
                          ▼
            ┌────────────────────────────────────┐
            │   OnnxSpeakerEmbedder              │
            │   ERes2NetV2 ONNX                  │
            │   → [1, 192] float32, L2-norm      │
            │   (offloaded to thread pool)       │
            └─────────────┬──────────────────────┘
                          │
                          ▼
            ┌────────────────────────────────────┐
            │   SpeakerVerifier                  │
            │   score = e · stored_voiceprint    │
            │   threshold = override ?? 0.55     │
            │   → Accept / Reject                │
            └─────────────┬──────────────────────┘
                          │
                          ▼
                Accept | Reject | NotEnrolled | Skipped
```

`NotEnrolled` and `Skipped(*)` are both pass-through — the user is never blocked just because the feature is inactive or the audio is too short. Only `Reject` drops the transcript.

## Enrollment state machine

A tenant is in exactly one of six states. The orchestrator in **Agent.Host** is the single authority for transitions; the LLM only sees tools, the Bridge only sees a snapshot.

```
                    ┌─────────┐  user runs /voice-id enroll
                    │ Unknown │──────────────┐
                    └────┬────┘              │
                         │ user says "no"    ▼
                         ▼          ┌────────────┐
                    ┌──────────┐    │ Enrolling  │  ← capture K=3 utterances
                    │ Declined │    └─────┬──────┘
                    └──────────┘          │ K reached
                                          ▼
                                   ┌────────────┐
                                   │ Confirming │  ← M=2 matches, F=3 fails
                                   └─────┬──────┘
                                         │ M matches
                                         ▼
                                   ┌────────────┐
                                   │ Enrolled   │ ───┐
                                   └─────┬──────┘    │ user runs /voice-id forget
                                         │           ▼
                  user requests reenroll │      ┌──────────┐
                                         ▼      │ Declined │
                                  ┌──────────────┐
                                  │ PendingReenroll │
                                  └─────────┬──────┘
                                            │ confirm → Enrolling
                                            ▼
                                       ... loops back
```

Only **Enrolled** and **PendingReenroll** make the gate active; **Unknown**, **Declined**, **Enrolling**, **Confirming** all pass-through.

## Process boundaries and the same-model invariant

Two processes need to run the embedder:

1. **Bridge** (Windows host, runs the channel) — uses the embedder to **verify** every utterance against the stored voiceprint.
2. **Agent.Host** (Docker container, holds tenant data) — uses the embedder to **enroll**: to compute the voiceprint from the K capture samples that get pushed up from the Bridge after `/voice-id enroll` finishes.

The voiceprint store (SQLite) lives only in Agent.Host. The Bridge does not persist tenant state — it reads through a `SignalRVoiceprintCache` that pulls snapshots on demand and is invalidated by `OnVoiceprintInvalidated` pushes from Agent.Host.

**Critical invariant:** both processes must use the *byte-identical* ONNX file. Two different builds of "ERes2NetV2" — same architecture, different training run — produce embeddings in different coordinate systems, and a voiceprint enrolled by one is meaningless to the other. This is why the deployment story is "the same `eres2netv2-base.onnx` is mounted/copied to both sides," not "each side downloads its own."

```
            Bridge (Windows host)              Agent.Host (Docker container)
            %LOCALAPPDATA%\Cortex\             /app/models/speaker-id/
              models\speaker-id\                 eres2netv2-base.onnx
              eres2netv2-base.onnx                   ╱
                       ╲                            ╱
                        ╲      must be byte-       ╱
                         ╲────  identical    ────╱
```

If either file is missing, the corresponding embedder/verifier is **not registered** in DI, the gate is inert end-to-end, and every transcript passes through. That is intentional fail-open — losing the gate must never silence the user.

## Backends: in-process vs sidecar

Speaker-ID supports two embedder backends, selected by `SpeakerId:Backend`:

| Backend | Where the model loads | Use when |
|---|---|---|
| `Local` (default) | In-process — Bridge and Agent.Host each load their own ONNX | Single-tenant deployments, local development without Docker |
| `Remote` | One sidecar container holds the model; Bridge + Agent.Host call it over HTTP | Multi-tenant deployments (one model copy regardless of tenant count), production |

Remote backend topology:

```
            ┌────────────────────────────────┐
            │     voice-id sidecar (1)       │
            │  loads ONNX model ONCE         │
            │  POST /embed  (PCM in, vec out)│
            └──────────────┬─────────────────┘
                ▲          │
                │          ▼
   ┌────────────┴────────────────────────────┐
   │ Bridge (1)         Agent.Host (per-tenant) │
   │  HttpSpeakerEmbedder   HttpSpeakerEmbedder │
   └────────────────────────────────────────────┘
```

The sidecar lives at `lib/voice-id` (git submodule of [github.com/yury-opolev/voice-id](https://github.com/yury-opolev/voice-id)). Its README documents the wire protocol and the model-config knobs that let you swap models without touching Cortex.

### Switching backend

`SpeakerId:Backend = Remote` in the Agent.Host (and Bridge) config selects the HTTP path. Default is `Local` for backwards-compatible behaviour. The docker-compose setup ships `Remote` by default since the `voice-id` service is present.

### Swappable models

The sidecar's `Sidecar:ModelPath`, `ModelId`, `InputName`, `OutputName`, `EmbeddingDim`, and `ModelType` (Fbank | RawWaveform) can be changed independently of Cortex. Drop a new `.onnx` into the bind-mounted models directory, update the env vars in `docker-compose.yml`, and restart the sidecar. Cortex doesn't need to be rebuilt — but voiceprints enrolled under the old model become invalid (the stored `ModelId` won't match the new one), so the next utterance from any enrolled tenant will fall through the `Skipped(Error)` path and the user can re-enroll via `/voice-id enroll`.

## Entry points (opt-in only)

The feature is never offered by the agent. Three discovery paths exist:

| Path | Surface | Example |
|---|---|---|
| Tool call | LLM-driven, when the user asks in chat/voice | "set up voice ID for me" → LLM calls `voice_enrollment_start` |
| Slash command | Discord | `/voice-id status`, `/voice-id enroll`, `/voice-id forget`, `/voice-id enable`, `/voice-id disable` |
| Web UI | Bridge admin | tenant-settings page → "Voice identification" card |

There are no system-prompt injections that nudge the agent to offer enrollment based on utterance counts or thresholds. That pattern was explicitly removed (commit `f0745d8`).

## Observability

- **Logs**: both processes emit `[SpeakerId] Loaded ONNX model from ...` at startup when the model is found. The Discord/Voice channels emit one structured log line per rejected utterance: `voice-in: speaker-id rejected utt=... score=0.xx`.
- **Metrics**: `GET /api/voice-id/metrics` on the Bridge returns per-tenant Accept / Reject / Skipped(reason) / Timeout counters. Useful for empirical threshold tuning until the FAR/FRR eval fixtures land.
- **Web UI**: tenant settings page shows current state and the most recent reason if Skipped.

## Glossary

- **Voiceprint** — the mean L2-normalised 192-d embedding from the K enrollment samples. A single unit vector per tenant.
- **Embedding** — the model's output for a single utterance: a 192-d unit vector. Distance in this space approximates voice dissimilarity.
- **fbank / log-mel filterbank** — the 80-bin acoustic feature passed to the model. Computed from the PCM with FFT + mel-scale triangular filters + log + cepstral mean normalisation.
- **Cosine similarity** — dot product of two unit vectors; the comparison metric used by the gate.
- **Fail-open** — when the verifier can't decide (missing model, timeout, error), the transcript passes through. Never block the user on infrastructure failure.
- **FAR / FRR** — false accept rate (impostor's voice accepted) and false reject rate (owner's voice rejected). The threshold trades one for the other.
