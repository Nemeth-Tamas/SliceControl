param(
    [string]$Mic = "HP Bang & Olufsen Audio Module",
    [string]$RadioUrl = "https://myonlineradio.hu/retro-radio",
    [string]$WhisperUrl = "http://192.168.1.2:8765",
    [string]$DiarizationUrl = "http://192.168.1.2:8766"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repoRoot "src\SliceTranscribe\SliceTranscribe.csproj"

Write-Host "Building SliceTranscribe Release..."
dotnet build $project -c Release

if ($LASTEXITCODE -ne 0) {
    throw "SliceTranscribe Release build failed."
}

$exe = Join-Path $repoRoot "src\SliceTranscribe\bin\Release\net8.0-windows\SliceTranscribe.exe"
$radioScript = Join-Path $PSScriptRoot "Start-RetroRadio.ps1"

if (-not (Test-Path $exe)) {
    throw "SliceTranscribe executable was not found at $exe"
}

$transcribeArgs = 'run --mic "{0}" --remote-url "{1}" --diarization-url "{2}"' -f $Mic, $WhisperUrl, $DiarizationUrl
$radioArgs = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -RadioUrl "{1}"' -f $radioScript, $RadioUrl

$transcribeAction = New-ScheduledTaskAction -Execute $exe -Argument $transcribeArgs -WorkingDirectory (Split-Path -Parent $exe)
$radioAction = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $radioArgs -WorkingDirectory $PSScriptRoot

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

$transcribeSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
$radioSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)

$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest

Register-ScheduledTask -TaskName "SliceTranscribe" -Action $transcribeAction -Trigger $trigger -Settings $transcribeSettings -Principal $principal -Description "Auto-start SliceTranscribe and restart it after failures." -Force | Out-Null
Register-ScheduledTask -TaskName "Slice Retro Radio" -Action $radioAction -Trigger $trigger -Settings $radioSettings -Principal $principal -Description "Open Retro Radio in Microsoft Edge with autoplay enabled." -Force | Out-Null

Write-Host
Write-Host "Installed scheduled tasks:"
Write-Host "  SliceTranscribe"
Write-Host "  Slice Retro Radio"
Write-Host
Write-Host "They will start automatically at the next sign-in."
Write-Host "Test them now with:"
Write-Host '  Start-ScheduledTask -TaskName "SliceTranscribe"'
Write-Host '  Start-ScheduledTask -TaskName "Slice Retro Radio"'
