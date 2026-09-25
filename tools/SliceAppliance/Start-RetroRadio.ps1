param(
    [string]$PrimaryStream = "https://icast.connectmedia.hu/5002/live.mp3",
    [string]$BackupStream = "https://icast.connectmedia.hu/5001/live.mp3"
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
    "--one-instance",
    "--no-video",
    "--qt-start-minimized",
    "--playlist-autostart",
    $playlistPath
)

Start-Process -FilePath $vlc -ArgumentList $arguments