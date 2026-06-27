# Turn-Detection Fixture Recordings

Real-voice WAVs consumed by `TurnDetectionFixtureTests`. Real prosody matters
because the LiveKit turn detector was trained on human speech, not TTS — TTS
prosody gives unrepresentative pEou values.

## Audacity settings (one-time)

1. Bottom-left **Project Rate**: `16000` Hz.
2. **Edit → Preferences → Devices → Recording → Channels**: `1 (Mono)`.

## Recording rules

- **Tail silence is critical.** Hit Stop ~4 seconds AFTER finishing speech.
  The harness needs that silence to observe the end-of-turn decision unfold;
  if the WAV cuts off right after speech, only the tail-flush is exercised,
  which defeats the test.
- Small head silence (~300 ms) is fine — matches what Discord sees.
- Speak naturally. Natural breath pauses between sentences are the whole point.

## Export

**File → Export → Export Audio**, format **WAV (Microsoft)**, encoding
**Signed 16-bit PCM**. Save to this directory.

## The six phrases

| File                              | Phrase                                                                                                  | Purpose                                                  |
|-----------------------------------|---------------------------------------------------------------------------------------------------------|----------------------------------------------------------|
| `single-sentence.wav`             | "I went to the gym today."                                                                              | Baseline: single sentence with clean falling intonation. |
| `multi-sentence-3.wav`            | "I went to the gym today. It was packed. So I just lifted at home instead."                              | **Gym-bug regression guard.** 3 sentences, breath pauses.|
| `multi-sentence-4.wav`            | "I went to the gym today. It was packed. So I just lifted at home instead. The workout was actually pretty good." | 4 sentences, same natural pauses.                        |
| `sentence-with-afterthought.wav`  | "I'll meet you at six... actually, make it six-thirty."                                                  | Sentence + ~600 ms beat + clarifying afterthought.       |
| `mid-thought.wav`                 | "I think... um... let me see..."                                                                        | Trailing-off, no committing intonation.                  |
| `short-yes.wav`                   | "Yes."                                                                                                  | Very short, one word.                                    |
