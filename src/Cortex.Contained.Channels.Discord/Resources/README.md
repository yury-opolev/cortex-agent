# Voice-out static assets

## `tts-notice-trouble.pcm`

Last-resort spoken notice played by `VoiceOutPipeline` when **all** TTS synthesis
for a response fails (the resolved engine yielded no audio AND
`CompositeTtsEngine`'s default-voice retry also yielded nothing). It guarantees
the user hears something instead of dead air, with zero TTS dependency at play
time — the bytes are embedded in the assembly.

- **Spoken text:** "Sorry, having trouble speaking right now."
- **Voice:** kokoro `af_heart` (the default English female voice).
- **Format:** 48 kHz mono signed 16-bit little-endian PCM (the canonical
  voice-out source format; `VoiceOutPipeline` upmixes to stereo + applies gain +
  frames it exactly like a synthesized sentence).

### Regenerating

With the uni-voices sidecar running on `127.0.0.1:8000`:

```powershell
$text = "Sorry, having trouble speaking right now."
$body = @{ engine="kokoro"; voice="af_heart"; text=$text; sampleRate=48000 } | ConvertTo-Json -Compress
$r = Invoke-WebRequest -Uri "http://127.0.0.1:8000/v1/synthesize/stream" -Method Post `
    -ContentType "application/json; charset=utf-8" `
    -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -UseBasicParsing
[System.IO.File]::WriteAllBytes(
    "src/Cortex.Contained.Channels.Discord/Resources/tts-notice-trouble.pcm", $r.Content)
```

The asset is embedded via `<EmbeddedResource>` in the project file, so it ships
inside the MSIX with no file-copy or path resolution.
