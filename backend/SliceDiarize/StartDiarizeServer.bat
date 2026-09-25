@echo off
cd /d "%~dp0"
title SliceDiarize - pyannote / RTX 3090

if not defined HF_TOKEN (
    for /f "usebackq delims=" %%T in (`powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable('HF_TOKEN','User')"`) do set "HF_TOKEN=%%T"
)

if not defined HF_TOKEN (
    echo.
    echo HF_TOKEN is not set in this process or the user environment.
    echo Accept the pyannote Community-1 model terms on Hugging Face,
    echo create a read token, then set it with:
    echo.
    echo   setx HF_TOKEN "hf_your_token_here"
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

".venv\Scripts\python.exe" -c "import uvicorn, fastapi" >nul 2>&1
if errorlevel 1 (
    echo.
    echo Base Python dependencies are missing. Installing requirements...
    echo.
    ".venv\Scripts\python.exe" -m pip install --upgrade pip
    if errorlevel 1 goto :install_failed

    ".venv\Scripts\python.exe" -m pip install -r requirements.txt
    if errorlevel 1 goto :install_failed
)

echo.
echo Checking PyTorch / CUDA / TorchVision...
".venv\Scripts\python.exe" -c "import torch, torchvision; assert torch.cuda.is_available(); assert torch.version.cuda is not None; print('PyTorch:', torch.__version__); print('TorchVision:', torchvision.__version__); print('CUDA runtime:', torch.version.cuda); print('GPU:', torch.cuda.get_device_name(0))"
if errorlevel 1 (
    echo.
    echo CUDA PyTorch or TorchVision is missing/broken.
    echo Reinstalling the matched Windows CUDA 12.6 pair...
    echo.
    ".venv\Scripts\python.exe" -m pip uninstall -y torch torchvision
    if errorlevel 1 goto :torch_failed

    ".venv\Scripts\python.exe" -m pip install --no-cache-dir --force-reinstall torch==2.14.0 torchvision==0.29.0 --index-url https://download.pytorch.org/whl/cu126
    if errorlevel 1 goto :torch_failed
)

echo.
echo Verifying final ML stack...
".venv\Scripts\python.exe" -c "import torch, torchvision, torchaudio; from pyannote.audio import Pipeline; print('PyTorch:', torch.__version__); print('TorchVision:', torchvision.__version__); print('TorchAudio:', torchaudio.__version__); print('CUDA runtime:', torch.version.cuda); print('CUDA available:', torch.cuda.is_available()); print('GPU:', torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'NONE')"
if errorlevel 1 goto :stack_failed

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

:torch_failed
echo.
echo CUDA PyTorch repair failed. Scroll up for the pip error.
echo.
pause
exit /b 1

:stack_failed
echo.
echo Python ML stack is still inconsistent after repair.
echo Scroll up for the import error.
echo.
pause
exit /b 1
