param(
    [string]$PrimaryStream = "https://icast.connectmedia.hu/5002/live.mp3",
    [string]$BackupStream = "https://icast.connectmedia.hu/5001/live.mp3",
    [string]$RadioUrl = ""
)

$ErrorActionPreference = "Stop"

function Find-Vlc {
    $candidates = @(
        "$env:ProgramFiles\VideoLAN\VLC\vlc.exe",
        "$env:ProgramFiles(x86)\VideoLAN\VLC\vlc.exe",
        "$env:LOCALAPPDATA\Programs\VideoLAN\VLC\vlc.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    $command = Get-Command "vlc.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command -and $command.Source) {
        return $command.Source
    }

    foreach ($root in @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\App Paths"
    )) {
        $key = Join-Path $root "vlc.exe"
        try {
            $value = (Get-ItemProperty -Path $key -ErrorAction Stop).'(default)'
            if ($value -and (Test-Path $value)) {
                return $value
            }
        }
        catch {
        }
    }

    return $null
}

$vlc = Find-Vlc

if (-not $vlc) {
    throw "VLC was not found. Install VLC or add vlc.exe to PATH."
}

$playlistDirectory = Join-Path $env:LOCALAPPDATA "SliceAppliance"
New-Item -ItemType Directory -Path $playlistDirectory -Force | Out-Null

$playlistPath = Join-Path $playlistDirectory "retro-radio.m3u8"
$pidPath = Join-Path $playlistDirectory "retro-radio.pid"

if (Test-Path $pidPath) {
    try {
        $oldPid = [int](Get-Content -Path $pidPath -ErrorAction Stop)
        $oldProcess = Get-Process -Id $oldPid -ErrorAction SilentlyContinue

        if ($null -ne $oldProcess -and $oldProcess.ProcessName -eq "vlc") {
            Stop-Process -Id $oldPid -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 250
        }
    }
    catch {
    }

    Remove-Item -Path $pidPath -Force -ErrorAction SilentlyContinue
}

$playlistLines = @(
    "#EXTM3U",
    "#EXTINF:-1,Retro Radio - primary",
    $PrimaryStream,
    "#EXTINF:-1,Retro Radio - backup",
    $BackupStream
)

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

[System.IO.File]::WriteAllLines(
    $playlistPath,
    $playlistLines,
    $utf8NoBom
)

Write-Host "VLC: $vlc"
Write-Host "Retro Radio primary: $PrimaryStream"
Write-Host "Retro Radio backup : $BackupStream"

$arguments = @(
    "--no-one-instance",
    "--no-video",
    "--qt-start-minimized",
    "--extraintf=rc",
    "--rc-host=127.0.0.1:4212",
    "--playlist-autostart",
    $playlistPath
)

$process = Start-Process -FilePath $vlc -ArgumentList $arguments -PassThru
[System.IO.File]::WriteAllText(
    $pidPath,
    $process.Id.ToString(),
    $utf8NoBom
)