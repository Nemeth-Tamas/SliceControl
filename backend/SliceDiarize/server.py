import asyncio
import os
import tempfile
from pathlib import Path

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
    version="0.1.0",
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
    }


@app.post("/diarize")
async def diarize(
    file: UploadFile = File(...),
):
    suffix = (
        Path(file.filename or "recording.wav")
        .suffix
        or ".wav"
    )

    temp_path = None

    try:
        data = await file.read()

        if not data:
            raise HTTPException(
                status_code=400,
                detail="Uploaded audio file is empty.",
            )

        with tempfile.NamedTemporaryFile(
            suffix=suffix,
            delete=False,
        ) as temp:
            temp.write(data)
            temp_path = temp.name

        async with pipeline_lock:
            output = await asyncio.to_thread(
                pipeline,
                temp_path,
            )

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

    finally:
        if temp_path:
            try:
                os.remove(temp_path)
            except OSError:
                pass
