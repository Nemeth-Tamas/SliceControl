param(
    [string]$Mic = "HP Bang & Olufsen Audio Module",
    [string]$WhisperUrl = "http://192.168.1.2:8765",
    [string]$DiarizationUrl = "http://192.168.1.2:8766",
    [string]$WireGuardAddress = "10.10.10.12",
    [string[]]$TrustedRemoteAddresses = @("10.10.10.0/24", "192.168.1.0/24"),
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

$transcribeProject = Join-Path $repoRoot "src\SliceTranscribe\SliceTranscribe.csproj"
$mcpProject = Join-Path $repoRoot "src\SliceMcp\SliceMcp.csproj"

$remotePort = 8787
$mcpPort = 8790
$wireGuardSubnet = "10.10.10.0/24"

$remotePrefix = "http://+:{0}/" -f $remotePort

$remoteRuleName = "Slice Remote Control - WireGuard"
$mcpRuleName = "Slice MCP - WireGuard"

$legacyTokenPath = Join-Path $env:LOCALAPPDATA "SliceAppliance\remote-token.txt"
Remove-Item $legacyTokenPath -Force -ErrorAction SilentlyContinue

Stop-ScheduledTask -TaskName "SliceTranscribe" -ErrorAction SilentlyContinue
Stop-ScheduledTask -TaskName "Slice MCP" -ErrorAction SilentlyContinue
Stop-ScheduledTask -TaskName "Slice SonoBus" -ErrorAction SilentlyContinue

Get-Process -Name "SliceTranscribe" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

Get-Process -Name "SliceMcp" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

Get-Process -Name "SonoBus" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

Start-Sleep -Milliseconds 300

Write-Host "Configuring WireGuard-only remote control access..."

try {
    & netsh http delete urlacl url="http://+:8787/" *> $null
}
catch {
}

try {
    & netsh http delete urlacl url=$remotePrefix *> $null
}
catch {
}

try {
    & netsh http add urlacl url=$remotePrefix user="$env:USERDOMAIN\$env:USERNAME" *> $null
}
catch {
    Write-Warning "Could not reserve $remotePrefix. The elevated scheduled task may still be able to listen."
}

try {
    Get-NetFirewallRule -DisplayName "Slice Remote Control" -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue

    Get-NetFirewallRule -DisplayName $remoteRuleName -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue

    Get-NetFirewallRule -DisplayName $mcpRuleName -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue

    New-NetFirewallRule -DisplayName $remoteRuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $remotePort -RemoteAddress $TrustedRemoteAddresses -Profile Any | Out-Null
    New-NetFirewallRule -DisplayName $mcpRuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $mcpPort -RemoteAddress $TrustedRemoteAddresses -Profile Any | Out-Null
}
catch {
    Write-Warning "Could not create one or more WireGuard-only Windows Firewall rules."
}

Write-Host "Building SliceTranscribe Release..."
dotnet build $transcribeProject -c Release

if ($LASTEXITCODE -ne 0) {
    throw "SliceTranscribe Release build failed."
}

Write-Host "Publishing SliceMcp self-contained Release..."
dotnet publish $mcpProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false

if ($LASTEXITCODE -ne 0) {
    throw "SliceMcp self-contained publish failed."
}

$transcribeExe = Join-Path $repoRoot "src\SliceTranscribe\bin\Release\net8.0-windows10.0.19041.0\SliceTranscribe.exe"
$mcpExe = Join-Path $repoRoot "src\SliceMcp\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\SliceMcp.exe"

$radioScript = Join-Path $PSScriptRoot "Start-RetroRadio.ps1"
$transcribeScript = Join-Path $PSScriptRoot "Start-SliceTranscribe.ps1"
$mcpScript = Join-Path $PSScriptRoot "Start-SliceMcp.ps1"
$sonoBusScript = Join-Path $PSScriptRoot "Start-SonoBus.ps1"

foreach ($required in @($transcribeExe, $mcpExe, $radioScript, $transcribeScript, $mcpScript, $sonoBusScript)) {
    if (-not (Test-Path $required)) {
        throw "Required appliance file was not found: $required"
    }
}

$transcribeArgs = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -Mic "{1}" -WhisperUrl "{2}" -DiarizationUrl "{3}"' -f $transcribeScript, $Mic, $WhisperUrl, $DiarizationUrl
$radioArgs = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}"' -f $radioScript
$mcpArgs = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -WireGuardAddress "{1}"' -f $mcpScript, $WireGuardAddress
$sonoBusArgs = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}"' -f $sonoBusScript

$transcribeAction = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $transcribeArgs -WorkingDirectory $PSScriptRoot
$radioAction = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $radioArgs -WorkingDirectory $PSScriptRoot
$mcpAction = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $mcpArgs -WorkingDirectory $PSScriptRoot
$sonoBusAction = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $sonoBusArgs -WorkingDirectory $PSScriptRoot

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$trigger.Delay = "PT5S"

$transcribeSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
$radioSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
$mcpSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
$sonoBusSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)

$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest

Register-ScheduledTask -TaskName "SliceTranscribe" -Action $transcribeAction -Trigger $trigger -Settings $transcribeSettings -Principal $principal -Description "Auto-start SliceTranscribe and restart it after failures." -Force | Out-Null
Register-ScheduledTask -TaskName "Slice Retro Radio" -Action $radioAction -Trigger $trigger -Settings $radioSettings -Principal $principal -Description "Play online shop radio directly in VLC." -Force | Out-Null
Register-ScheduledTask -TaskName "Slice MCP" -Action $mcpAction -Trigger $trigger -Settings $mcpSettings -Principal $principal -Description "Expose Slice shop controls over MCP on the WireGuard interface only." -Force | Out-Null
Register-ScheduledTask -TaskName "Slice SonoBus" -Action $sonoBusAction -Trigger $trigger -Settings $sonoBusSettings -Principal $principal -Description "Auto-connect SonoBus audio receiver for Slice playback." -Force | Out-Null

Write-Host
Write-Host "Installed scheduled tasks:"
Write-Host "  SliceTranscribe"
Write-Host "  Slice Retro Radio"
Write-Host "  Slice MCP"
Write-Host "  Slice SonoBus"

if (-not $NoStart) {
    Write-Host
    Write-Host "Starting Slice appliance services now..."

    Stop-ScheduledTask -TaskName "SliceTranscribe" -ErrorAction SilentlyContinue
    Stop-ScheduledTask -TaskName "Slice Retro Radio" -ErrorAction SilentlyContinue
    Stop-ScheduledTask -TaskName "Slice MCP" -ErrorAction SilentlyContinue
    Stop-ScheduledTask -TaskName "Slice SonoBus" -ErrorAction SilentlyContinue

    Get-Process -Name "SliceTranscribe" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    Get-Process -Name "SliceMcp" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    Get-Process -Name "vlc" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    Get-Process -Name "SonoBus" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    Start-Sleep -Milliseconds 300

    Start-ScheduledTask -TaskName "SliceTranscribe"
    Start-ScheduledTask -TaskName "Slice Retro Radio"
    Start-ScheduledTask -TaskName "Slice MCP"
    Start-ScheduledTask -TaskName "Slice SonoBus"

    Start-Sleep -Seconds 3

    $transcribeTask = Get-ScheduledTaskInfo -TaskName "SliceTranscribe"
    $radioTask = Get-ScheduledTaskInfo -TaskName "Slice Retro Radio"
    $mcpTask = Get-ScheduledTaskInfo -TaskName "Slice MCP"
    $sonoBusTask = Get-ScheduledTaskInfo -TaskName "Slice SonoBus"

    $transcribeState = (Get-ScheduledTask -TaskName "SliceTranscribe").State
    $radioState = (Get-ScheduledTask -TaskName "Slice Retro Radio").State
    $mcpState = (Get-ScheduledTask -TaskName "Slice MCP").State
    $sonoBusState = (Get-ScheduledTask -TaskName "Slice SonoBus").State

    Write-Host
    Write-Host ("  SliceTranscribe: {0} (LastTaskResult {1})" -f $transcribeState, $transcribeTask.LastTaskResult)
    Write-Host ("  Slice Retro Radio: {0} (LastTaskResult {1})" -f $radioState, $radioTask.LastTaskResult)
    Write-Host ("  Slice MCP: {0} (LastTaskResult {1})" -f $mcpState, $mcpTask.LastTaskResult)
    Write-Host ("  Slice SonoBus: {0} (LastTaskResult {1})" -f $sonoBusState, $sonoBusTask.LastTaskResult)

    Start-Sleep -Seconds 1

    $mcpListener = Get-NetTCPConnection -LocalPort $mcpPort -State Listen -ErrorAction SilentlyContinue

    if ($null -eq $mcpListener) {
        Write-Warning "Slice MCP is not listening on TCP $mcpPort."

        $mcpErrorLog = Join-Path $env:LOCALAPPDATA "SliceAppliance\Logs\SliceMcp-error.log"
        $mcpOutputLog = Join-Path $env:LOCALAPPDATA "SliceAppliance\Logs\SliceMcp.log"

        if (Test-Path $mcpErrorLog) {
            Write-Host
            Write-Host "Last Slice MCP errors:"
            Get-Content $mcpErrorLog -Tail 30 | ForEach-Object { Write-Host ("  " + $_) }
        }

        if (Test-Path $mcpOutputLog) {
            Write-Host
            Write-Host "Last Slice MCP output:"
            Get-Content $mcpOutputLog -Tail 20 | ForEach-Object { Write-Host ("  " + $_) }
        }
    }
    else {
        Write-Host ("  Slice MCP listener: TCP {0} OK" -f $mcpPort)
    }

    $announcementPath = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "SliceTranscribe\Announcements"

    Write-Host
    Write-Host "WireGuard-only services:"
    Write-Host ("  Web UI: http://{0}:{1}/" -f $WireGuardAddress, $remotePort)
    Write-Host ("  MCP:    http://{0}:{1}/mcp" -f $WireGuardAddress, $mcpPort)
    Write-Host ("  Remote networks allowed by firewall: {0}" -f ($TrustedRemoteAddresses -join ", "))
    Write-Host ("  Announcements: {0}" -f $announcementPath)
    Write-Host ("  Logs: {0}" -f (Join-Path $env:LOCALAPPDATA "SliceAppliance\Logs"))

    $assistantConfig = Join-Path $env:LOCALAPPDATA "SliceAppliance\assistant.json"

    if (Test-Path $assistantConfig) {
        Write-Host ("  ECHO assistant: configured ({0})" -f $assistantConfig)
    }
    else {
        Write-Warning "ECHO assistant is not configured yet."
        Write-Host "  Run tools\SliceAppliance\Configure-SliceAssistant.ps1 -BaseUrl <Hermes URL> -ApiKey <key>"
    }

    $sonoBusConfig = Join-Path $env:LOCALAPPDATA "SliceAppliance\sonobus.json"

    if (Test-Path $sonoBusConfig) {
        Write-Host ("  SonoBus receiver: configured ({0})" -f $sonoBusConfig)
    }
    else {
        Write-Host "  SonoBus receiver: not configured"
        Write-Host "  Run tools\SliceAppliance\Configure-SliceSonoBus.ps1 -Group <group-name>"
    }

    if ($null -eq (Get-NetIPAddress -IPAddress $WireGuardAddress -ErrorAction SilentlyContinue)) {
        Write-Warning "WireGuard address $WireGuardAddress is not present yet. Slice MCP will wait for it for up to two minutes each start."
        Write-Warning "Run tools\SliceAppliance\Setup-SliceWireGuard.ps1 after installing WireGuard for Windows."
    }
}

Write-Host
Write-Host "All four tasks will start automatically 5 seconds after each sign-in."
Write-Host "Use -NoStart if you only want to install/update the tasks without starting them immediately."
