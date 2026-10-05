param(
    [string]$Mic = "HP Bang & Olufsen Audio Module",
    [string]$WhisperUrl = "http://192.168.1.2:8765",
    [string]$DiarizationUrl = "http://192.168.1.2:8766"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $repoRoot "src\SliceTranscribe\bin\Release\net8.0-windows10.0.19041.0\SliceTranscribe.exe"

if (-not (Test-Path $exe)) {
    throw "SliceTranscribe executable was not found at $exe"
}

$logDirectory = Join-Path $env:LOCALAPPDATA "SliceAppliance\Logs"
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

$stdoutLog = Join-Path $logDirectory "SliceTranscribe.log"
$stderrLog = Join-Path $logDirectory "SliceTranscribe-error.log"

$transcribeArgs = @(
    "run",
    "--mic", $Mic,
    "--remote-url", $WhisperUrl,
    "--diarization-url", $DiarizationUrl
)

$restartCount = 0

while ($true) {
    $restartCount++

    $stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    Add-Content -Path $stdoutLog -Value ("{0} WRAPPER -> starting SliceTranscribe (attempt {1})" -f $stamp, $restartCount)

    $exitCode = 1

    try {
        # Windows PowerShell 5.1 turns native stderr into PowerShell error
        # records. SliceTranscribe intentionally writes recoverable warnings
        # (for example remote Whisper being unavailable) to stderr, so using
        # ErrorActionPreference=Stop here can kill the appliance even though
        # the application itself is handling the failure correctly.
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"

        try {
            & $exe @transcribeArgs >> $stdoutLog 2>> $stderrLog
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousPreference
        }
    }
    catch {
        $_ | Out-String | Add-Content -Path $stderrLog
        $exitCode = 1
    }

    $stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    Add-Content -Path $stderrLog -Value ("{0} WRAPPER -> SliceTranscribe exited with code {1}; restarting in 2 s" -f $stamp, $exitCode)

    Start-Sleep -Seconds 2
}
