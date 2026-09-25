param(
    [string]$Mic = "HP Bang & Olufsen Audio Module",
    [string]$WhisperUrl = "http://192.168.1.2:8765",
    [string]$DiarizationUrl = "http://192.168.1.2:8766",
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repoRoot "src\SliceTranscribe\SliceTranscribe.csproj"
$remotePort = 8787
$remotePrefix = "http://+:$remotePort/"
$remoteRuleName = "Slice Remote Control"

# Stop an already-running copy before rebuilding so Windows does not keep the
# Release executable locked. This also replaces the old visible console copy
# with the hidden scheduled-task launcher below.
Stop-ScheduledTask -TaskName "SliceTranscribe" -ErrorAction SilentlyContinue

Get-Process -Name "SliceTranscribe" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

Start-Sleep -Milliseconds 250

Write-Host "Configuring Slice remote control listener..."

try {
    & netsh http delete urlacl url=$remotePrefix *> $null
}
catch {
}

try {
    & netsh http add urlacl url=$remotePrefix user="$env:USERDOMAIN\$env:USERNAME" *> $null
}
catch {
    Write-Warning "Could not reserve $remotePrefix. The scheduled task may still work when elevated."
}

try {
    Get-NetFirewallRule -DisplayName $remoteRuleName -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue

    New-NetFirewallRule -DisplayName $remoteRuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $remotePort -Profile Any | Out-Null
}
catch {
    Write-Warning "Could not create Windows Firewall rule for TCP $remotePort."
}

Write-Host "Building SliceTranscribe Release..."
dotnet build $project -c Release

if ($LASTEXITCODE -ne 0) {
    throw "SliceTranscribe Release build failed."
}

$exe = Join-Path $repoRoot "src\SliceTranscribe\bin\Release\net8.0-windows10.0.19041.0\SliceTranscribe.exe"
$radioScript = Join-Path $PSScriptRoot "Start-RetroRadio.ps1"
$transcribeScript = Join-Path $PSScriptRoot "Start-SliceTranscribe.ps1"

if (-not (Test-Path $exe)) {
    throw "SliceTranscribe executable was not found at $exe"
}

if (-not (Test-Path $transcribeScript)) {
    throw "SliceTranscribe launcher was not found at $transcribeScript"
}

$transcribeArgs = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -Mic "{1}" -WhisperUrl "{2}" -DiarizationUrl "{3}"' -f $transcribeScript, $Mic, $WhisperUrl, $DiarizationUrl
$radioArgs = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}"' -f $radioScript

$transcribeAction = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $transcribeArgs -WorkingDirectory $PSScriptRoot
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

    Get-Process -Name "SliceTranscribe" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    # The pre-RC radio launcher did not track its VLC PID. Stop any old VLC
    # instance once during installation so the newly controlled radio owns
    # localhost:4212 cleanly.
    Get-Process -Name "vlc" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    Start-Sleep -Milliseconds 300

    Start-ScheduledTask -TaskName "SliceTranscribe"
    Start-ScheduledTask -TaskName "Slice Retro Radio"

    Start-Sleep -Seconds 2

    $transcribeTask = Get-ScheduledTaskInfo -TaskName "SliceTranscribe"
    $radioTask = Get-ScheduledTaskInfo -TaskName "Slice Retro Radio"

    $transcribeState = (Get-ScheduledTask -TaskName "SliceTranscribe").State
    $radioState = (Get-ScheduledTask -TaskName "Slice Retro Radio").State

    Write-Host ("  SliceTranscribe: {0} (LastTaskResult {1})" -f $transcribeState, $transcribeTask.LastTaskResult)
    Write-Host ("  Slice Retro Radio: {0} (LastTaskResult {1})" -f $radioState, $radioTask.LastTaskResult)
    Write-Host ("  SliceTranscribe logs: {0}" -f (Join-Path $env:LOCALAPPDATA "SliceAppliance\Logs"))

    $tokenPath = Join-Path $env:LOCALAPPDATA "SliceAppliance\remote-token.txt"
    $announcementPath = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "SliceTranscribe\Announcements"

    if (Test-Path $tokenPath) {
        $token = (Get-Content $tokenPath -Raw).Trim()

        Write-Host
        Write-Host "Remote control:"
        Write-Host ("  URL:   http://<Slice-VPN-IP>:{0}/" -f $remotePort)
        Write-Host ("  Token: {0}" -f $token)
        Write-Host ("  Token file: {0}" -f $tokenPath)
        Write-Host ("  Announcements: {0}" -f $announcementPath)
    }
}

Write-Host
Write-Host "Both tasks will start automatically 5 seconds after each sign-in."
Write-Host "Use -NoStart if you only want to install/update the tasks without starting them immediately."
