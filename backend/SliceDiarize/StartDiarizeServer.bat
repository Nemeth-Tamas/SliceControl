@echo off
cd /d "%~dp0"
title SliceDiarize - pyannote / RTX 3090

if not defined HF_TOKEN (
    echo.
    echo HF_TOKEN is not set.
    echo Accept the pyannote Community-1 model terms on Hugging Face,
    echo create a read token, then set it with:
    echo.
    echo   setx HF_TOKEN "hf_your_token_here"
    echo.
    echo Open a NEW terminal after setx.
    echo.
    pause
    exit /b 1
)

if not exist ".venv\Scripts\python.exe" (
    echo.
    echo Missing .venv. Create it and install requirements first.
    echo.
    pause
    exit /b 1
)

call ".venv\Scripts\activate.bat"

python -m uvicorn server:app --host 0.0.0.0 --port 8766

pause
