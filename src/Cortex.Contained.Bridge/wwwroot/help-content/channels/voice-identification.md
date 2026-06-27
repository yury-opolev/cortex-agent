# Voice Identification

Voice identification (speaker verification) makes Cortex act **only** on voice it
recognizes as the enrolled owner. It's managed per tenant under **Tenant Settings →
Voice identification**.

## What it does

When enabled, every spoken utterance is checked against an enrolled **voiceprint** in
parallel with transcription. Utterances whose voiceprint doesn't match the owner are
dropped — so other voices in the room don't drive the assistant. When the feature is
off, all transcripts pass through (the default behavior).

## Enrollment

Enrollment normally happens **in voice**: after you've spoken a few utterances, the
agent offers to enroll you and guides you through capturing samples. The Tenant
Settings panel provides the admin side:

- **Feature enabled** — master toggle for the gate on this tenant.
- **Status / Embedding dim / Model** — read-only state of the current voiceprint and
  the embedding model in use.
- **Start enrollment** — arm enrollment (then speak / join voice to capture samples).
- **Forget voiceprint** — delete the enrolled voiceprint and reset.

## Advanced: cosine threshold

Under **Show advanced** you can override the match threshold (cosine similarity,
`0`–`1`). Leave it blank to use the default (`0.55`). Raising it makes verification
stricter (fewer false accepts, more false rejects); lowering it is more lenient.

This works for both the local [Voice channel](help:voice-host) and Discord voice.
