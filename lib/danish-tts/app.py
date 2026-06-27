"""FastAPI server wrapping CoRal Røst-v3-Chatterbox-500m for Danish TTS.

Spike-level: single model loaded at startup, synchronous inference,
no batching or auth. See docs/superpowers/specs/2026-05-21-tts-spike-design.md.

Loading notes:
- The model is loaded via ChatterboxMultilingualTTS.from_local() against a
  snapshot of the CoRal repo. (from_pretrained() hardcodes the *base*
  ResembleAI/chatterbox repo, which is NOT the Danish fine-tune.)
- generate() has no speaker argument. The two predefined voices (Mic, Nic)
  are selected by passing one of the repo's bundled sample clips as
  audio_prompt_path — i.e. zero-shot voice cloning against the model's own
  reference audio.

Streaming-fork investigation status:
- /synthesize/stream uses state.model.generate_stream() if the installed
  library exposes it (the chatterbox-streaming fork yields incremental
  chunks); otherwise it falls back to whole-sentence generate() emitted as a
  single PCM chunk. The wire contract (raw LE int16 mono PCM) is identical
  either way. requirements.txt currently pins chatterbox-tts==0.1.7
  (whole-sentence only) — switching to the streaming fork is a server-only
  change (swap the pin, rebuild) requiring no client changes.
"""

from __future__ import annotations

import glob
import io
import logging
import os
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Literal

import numpy as np
import torch
import torchaudio
from fastapi import FastAPI, HTTPException
from fastapi.responses import JSONResponse, Response, StreamingResponse
from pydantic import BaseModel, Field

MODEL_REPO = "CoRal-project/roest-v3-chatterbox-500m"
LANGUAGE_ID = "da"

# Generation parameters matching the CoRal README's recommended samples.
# (exaggeration is intentionally left at its default — the model card states
# the Danish fine-tune does not support it.)
GEN_PARAMS = {
    "temperature": 0.8,
    "top_p": 0.95,
    "cfg_weight": 0.5,
    "min_p": 0.05,
    "repetition_penalty": 2.0,
}

logger = logging.getLogger("danish-voice")
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")


class ModelState:
    model = None
    device = "cpu"
    voice_refs: dict[str, str] = {}


state = ModelState()


def _build_voice_reference(model_dir: str, speaker: str) -> str | None:
    """Concatenate the repo's sample clips for a speaker into one reference WAV.

    A longer reference gives the voice-cloning path a more stable timbre to
    lock onto than a single short clip.
    """
    samples = sorted(glob.glob(os.path.join(model_dir, "audio_samples", f"*_{speaker}_*.wav")))
    if not samples:
        return None

    sample_rate: int | None = None
    waves = []
    for sample in samples:
        wav, file_sr = torchaudio.load(sample)
        if sample_rate is None:
            sample_rate = file_sr
        if file_sr != sample_rate:
            wav = torchaudio.functional.resample(wav, file_sr, sample_rate)
        if wav.shape[0] > 1:
            wav = wav.mean(dim=0, keepdim=True)
        waves.append(wav)

    combined = torch.cat(waves, dim=1)
    out_path = os.path.join(model_dir, f"_ref_{speaker}.wav")
    torchaudio.save(out_path, combined, sample_rate)
    return out_path


@asynccontextmanager
async def lifespan(_app: FastAPI):
    from chatterbox.mtl_tts import ChatterboxMultilingualTTS
    from huggingface_hub import snapshot_download

    state.device = "cuda" if torch.cuda.is_available() else "cpu"

    logger.info("Downloading %s (first run pulls ~1 GB)", MODEL_REPO)
    model_dir = snapshot_download(
        repo_id=MODEL_REPO,
        token=os.getenv("HF_TOKEN") or None,
        allow_patterns=["*.safetensors", "*.json", "*.pt", "*.wav"],
    )

    logger.info("Loading model on %s", state.device)
    state.model = ChatterboxMultilingualTTS.from_local(model_dir, device=state.device)

    state.voice_refs = {}
    for speaker in ("mic", "nic"):
        ref = _build_voice_reference(model_dir, speaker)
        if ref is not None:
            state.voice_refs[speaker] = ref

    logger.info(
        "Model loaded (sample_rate=%s, device=%s, voices=%s)",
        state.model.sr,
        state.device,
        sorted([*state.voice_refs.keys(), "default"]),
    )
    yield
    state.model = None


app = FastAPI(title="Danish Voice TTS", lifespan=lifespan)


class SynthesizeRequest(BaseModel):
    text: str = Field(min_length=1, max_length=2000)
    voice: Literal["mic", "nic", "default"] = "mic"


def _available_voices() -> list[str]:
    return sorted([*state.voice_refs.keys(), "default"])


def _pcm16_bytes(wav_tensor) -> bytes:
    """float32 torch waveform in [-1,1] -> little-endian int16 PCM bytes (mono)."""
    samples = wav_tensor.detach().cpu().numpy().reshape(-1)
    clipped = np.clip(samples, -1.0, 1.0)
    return (clipped * 32767.0).astype("<i2").tobytes()


@app.get("/health")
def health() -> JSONResponse:
    loaded = state.model is not None
    body = {
        "model_loaded": loaded,
        "device": state.device,
        "model": MODEL_REPO,
        "voices": _available_voices() if loaded else [],
        "sample_rate": state.model.sr if loaded else None,
    }
    return JSONResponse(body, status_code=200 if loaded else 503)


@app.get("/voices")
def voices() -> list[str]:
    return _available_voices()


@app.post("/synthesize")
def synthesize(req: SynthesizeRequest) -> Response:
    if state.model is None:
        raise HTTPException(status_code=503, detail="model not yet loaded")

    audio_prompt_path: str | None = None
    if req.voice != "default":
        audio_prompt_path = state.voice_refs.get(req.voice)
        if audio_prompt_path is None:
            raise HTTPException(
                status_code=400,
                detail=f"voice '{req.voice}' unavailable; have {_available_voices()}",
            )

    try:
        wav = state.model.generate(
            req.text,
            language_id=LANGUAGE_ID,
            audio_prompt_path=audio_prompt_path,
            **GEN_PARAMS,
        )
    except Exception as ex:  # noqa: BLE001 — boundary, surface message to client
        logger.exception("synthesis failed")
        raise HTTPException(status_code=500, detail=f"synthesis failed: {ex}") from ex

    buf = io.BytesIO()
    torchaudio.save(buf, wav.cpu(), state.model.sr, format="wav")
    return Response(content=buf.getvalue(), media_type="audio/wav")


@app.post("/synthesize/stream")
def synthesize_stream(req: SynthesizeRequest):
    if state.model is None:
        raise HTTPException(status_code=503, detail="model not yet loaded")

    audio_prompt_path = None
    if req.voice != "default":
        audio_prompt_path = state.voice_refs.get(req.voice)
        if audio_prompt_path is None:
            raise HTTPException(status_code=400, detail=f"voice '{req.voice}' unavailable")

    def generate_chunks():
        # Prefer model-level streaming if the installed library exposes it
        # (chatterbox-streaming fork: generate_stream yields chunks). Fall back
        # to whole-sentence generation emitted as a single chunk — identical
        # wire contract either way, so the .NET client needs no branching.
        stream_fn = getattr(state.model, "generate_stream", None)
        if callable(stream_fn):
            for chunk in stream_fn(
                req.text, language_id=LANGUAGE_ID,
                audio_prompt_path=audio_prompt_path, **GEN_PARAMS):
                yield _pcm16_bytes(chunk)
        else:
            wav = state.model.generate(
                req.text, language_id=LANGUAGE_ID,
                audio_prompt_path=audio_prompt_path, **GEN_PARAMS)
            yield _pcm16_bytes(wav)

    return StreamingResponse(generate_chunks(), media_type="audio/pcm")


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(
        "app:app",
        host="0.0.0.0",  # noqa: S104 — intended: container exposes the port
        port=int(os.environ.get("PORT", "8000")),
        log_level="info",
    )
