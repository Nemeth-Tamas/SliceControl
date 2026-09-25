param(
    [string]$Mic = "HP Bang & Olufsen Audio Module",
    [string]$WhisperUrl = "http://192.168.1.2:8765",
    [string]$DiarizationUrl = "http://192.168.1.2:8766",
    [switch]$NoStart
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
$radioArgs = '-NoProfile -ExecutionPolicy Bypass -File "{0}"' -f $radioScript

$transcribeAction = New-ScheduledTaskAction -Execute $exe -Argument $transcribeArgs -WorkingDirectory (Split-Path -Parent $exe)
$radioAction = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $radioArgs -WorkingDirectory $PSScriptRoot

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$trigger.Delay = "PT5S"

$transcribeSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
$radioSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)

$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest

Register-ScheduledTask -TaskName "SliceTranscribe" -Action $transcribeAction -Trigger $trigger -Settings $transcribeSettings -Principal $principal -Description "Auto-start SliceTranscribe and restart it after failures." -Force | Out-Null
Register-ScheduledTask -TaskName "Slice Retro Radio" -Action $radioAction -Trigger $trigger -Settings $radioSettings -Principal $principal -Description "Play Retro Radio directly in VLC." -Force | Out-Null

Write-Host
Write-Host "Installed scheduled tasks:"
Write-Host "  SliceTranscribe"
Write-Host "  Slice Retro Radio"
Write-Host
if (-not $NoStart) {
    Write-Host
    Write-Host "Starting SliceTranscribe and Retro Radio now..."

    Stop-ScheduledTask -TaskName "SliceTranscribe" -ErrorAction SilentlyContinue
    Stop-ScheduledTask -TaskName "Slice Retro Radio" -ErrorAction SilentlyContinue

    Start-ScheduledTask -TaskName "SliceTranscribe"
    Start-ScheduledTask -TaskName "Slice Retro Radio"

    Start-Sleep -Seconds 2

    $transcribeTask = Get-ScheduledTaskInfo -TaskName "SliceTranscribe"
    $radioTask = Get-ScheduledTaskInfo -TaskName "Slice Retro Radio"

    Write-Host ("  SliceTranscribe LastTaskResult: {0}" -f $transcribeTask.LastTaskResult)
    Write-Host ("  Slice Retro Radio LastTaskResult: {0}" -f $radioTask.LastTaskResult)
}

Write-Host
Write-Host "Both tasks will start automatically 5 seconds after each sign-in."
Write-Host "Use -NoStart if you only want to install/update the tasks without starting them immediately."
