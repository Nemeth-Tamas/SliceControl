param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$ApiKey,

    [string]$SessionKey = "echo-puck-main",

    [switch]$NoRestart
)

$ErrorActionPreference = "Stop"

$directory = Join-Path $env:LOCALAPPDATA "SliceAppliance"
$configPath = Join-Path $directory "assistant.json"

New-Item -ItemType Directory -Path $directory -Force | Out-Null

$config = [ordered]@{
    BaseUrl = $BaseUrl.TrimEnd("/")
    ApiKey = $ApiKey
    SessionKey = $SessionKey
    SessionId = $null
}

$config |
    ConvertTo-Json |
    Set-Content -Path $configPath -Encoding UTF8

try {
    & icacls $configPath /inheritance:r /grant:r "$env:USERNAME:(R,W)" "SYSTEM:(F)" "Administrators:(F)" *> $null
}
catch {
    Write-Warning "Could not tighten ACLs on $configPath"
}

Write-Host
Write-Host "Slice assistant configured:"
Write-Host ("  Hermes:      {0}" -f $config.BaseUrl)
Write-Host ("  Session key: {0}" -f $config.SessionKey)
Write-Host ("  Config:      {0}" -f $configPath)
Write-Host
Write-Host "The API key was written locally and was not printed."

if (-not $NoRestart) {
    Write-Host
    Write-Host "Restarting SliceTranscribe..."

    Stop-ScheduledTask -TaskName "SliceTranscribe" -ErrorAction SilentlyContinue

    Get-Process -Name "SliceTranscribe" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    Start-Sleep -Milliseconds 300

    Start-ScheduledTask -TaskName "SliceTranscribe"

    Start-Sleep -Seconds 2

    $state = (Get-ScheduledTask -TaskName "SliceTranscribe").State

    Write-Host ("  SliceTranscribe: {0}" -f $state)
    Write-Host
    Write-Host "Open http://10.10.10.12:8787/ and check the Assistant card."
}
