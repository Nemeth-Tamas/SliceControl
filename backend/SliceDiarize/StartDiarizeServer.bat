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
    echo Missing .venv. Create it with:
    echo.
    echo   py -3.12 -m venv .venv
    echo.
    pause
    exit /b 1
)

".venv\Scripts\python.exe" -c "import uvicorn, fastapi, pyannote.audio" >nul 2>&1
if errorlevel 1 (
    echo.
    echo Python dependencies are missing. Installing requirements...
    echo.
    ".venv\Scripts\python.exe" -m pip install --upgrade pip
    if errorlevel 1 goto :install_failed

    ".venv\Scripts\python.exe" -m pip install -r requirements.txt
    if errorlevel 1 goto :install_failed
)

echo.
echo Checking CUDA...
".venv\Scripts\python.exe" -c "import torch; print('PyTorch:', torch.__version__); print('CUDA available:', torch.cuda.is_available()); print('GPU:', torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'NONE')"
if errorlevel 1 (
    echo.
    echo CUDA/PyTorch check failed.
    echo.
    pause
    exit /b 1
)

echo.
echo Starting SliceDiarize on port 8766...
echo.
".venv\Scripts\python.exe" -m uvicorn server:app --host 0.0.0.0 --port 8766

pause
exit /b 0

:install_failed
echo.
echo Dependency installation failed. Scroll up for the pip error.
echo.
pause
exit /b 1
