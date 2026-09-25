param(
    [string]$Mic = "HP Bang & Olufsen Audio Module",
    [string]$WhisperUrl = "http://192.168.1.2:8765",
    [string]$DiarizationUrl = "http://192.168.1.2:8766"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $repoRoot "src\SliceTranscribe\bin\Release\net8.0-windows\SliceTranscribe.exe"

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

try {
    & $exe @transcribeArgs >> $stdoutLog 2>> $stderrLog
    exit $LASTEXITCODE
}
catch {
    $_ | Out-String | Add-Content -Path $stderrLog
    exit 1
}
