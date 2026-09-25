import asyncio
import io
import os
import wave

import numpy as np
import torch
from fastapi import FastAPI, File, HTTPException, UploadFile
from pyannote.audio import Pipeline

MODEL_NAME = os.environ.get(
    "PYANNOTE_MODEL",
    "pyannote/speaker-diarization-community-1",
)

HF_TOKEN = os.environ.get("HF_TOKEN")

if not HF_TOKEN:
    raise RuntimeError(
        "HF_TOKEN is required. Accept the Community-1 model terms on "
        "Hugging Face, create a read token, and set HF_TOKEN before starting."
    )

device = torch.device(
    "cuda"
    if torch.cuda.is_available()
    else "cpu"
)

pipeline = Pipeline.from_pretrained(
    MODEL_NAME,
    token=HF_TOKEN,
)

pipeline.to(device)

pipeline_lock = asyncio.Lock()

app = FastAPI(
    title="SliceDiarize",
    version="0.2.0",
)


@app.get("/health")
async def health():
    gpu_name = None

    if torch.cuda.is_available():
        gpu_name = torch.cuda.get_device_name(0)

    return {
        "ok": True,
        "model": MODEL_NAME,
        "device": str(device),
        "gpu": gpu_name,
        "audio_loader": "pcm-wave-in-memory",
    }


def decode_pcm_wave(data: bytes) -> dict:
    try:
        with wave.open(
            io.BytesIO(data),
            "rb",
        ) as wav:
            channels = wav.getnchannels()
            sample_width = wav.getsampwidth()
            sample_rate = wav.getframerate()
            frame_count = wav.getnframes()
            pcm = wav.readframes(frame_count)

    except wave.Error as exc:
        raise HTTPException(
            status_code=400,
            detail=f"Expected a PCM WAV file: {exc}",
        ) from exc

    if sample_width != 2:
        raise HTTPException(
            status_code=400,
            detail=(
                "SliceDiarize expects 16-bit PCM WAV input; "
                f"received sample width {sample_width * 8} bits."
            ),
        )

    samples = np.frombuffer(
        pcm,
        dtype="<i2",
    ).astype(
        np.float32,
    )

    if channels > 1:
        samples = (
            samples
            .reshape(-1, channels)
            .mean(axis=1)
        )

    samples /= 32768.0

    waveform = (
        torch.from_numpy(
            samples.copy(),
        )
        .unsqueeze(0)
    )

    return {
        "waveform": waveform,
        "sample_rate": sample_rate,
    }


@app.post("/diarize")
async def diarize(
    file: UploadFile = File(...),
):
    data = await file.read()

    if not data:
        raise HTTPException(
            status_code=400,
            detail="Uploaded audio file is empty.",
        )

    audio_input = decode_pcm_wave(
        data,
    )

    try:
        async with pipeline_lock:
            output = await asyncio.to_thread(
                pipeline,
                audio_input,
            )

    except Exception as exc:
        raise HTTPException(
            status_code=500,
            detail=(
                f"pyannote pipeline failed: "
                f"{type(exc).__name__}: {exc}"
            ),
        ) from exc

    annotation = getattr(
        output,
        "exclusive_speaker_diarization",
        None,
    )

    if annotation is None:
        annotation = output.speaker_diarization

    segments = []

    for turn, speaker in annotation:
        segments.append(
            {
                "start": float(turn.start),
                "end": float(turn.end),
                "speaker": str(speaker),
            }
        )

    speakers = sorted(
        {
            segment["speaker"]
            for segment in segments
        }
    )

    return {
        "segments": segments,
        "speakers": speakers,
        "speaker_count": len(speakers),
    }
