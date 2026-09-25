param(
    [string]$RadioUrl = "https://myonlineradio.hu/retro-radio"
)

$ErrorActionPreference = "Stop"

function Find-ChromiumBrowser {
    $candidates = @(
        "$env:ProgramFiles(x86)\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "$env:LOCALAPPDATA\Microsoft\Edge\Application\msedge.exe",

        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "$env:ProgramFiles(x86)\Google\Chrome\Application\chrome.exe",
        "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe",

        "$env:ProgramFiles\BraveSoftware\Brave-Browser\Application\brave.exe",
        "$env:ProgramFiles(x86)\BraveSoftware\Brave-Browser\Application\brave.exe",
        "$env:LOCALAPPDATA\BraveSoftware\Brave-Browser\Application\brave.exe",

        "$env:ProgramFiles\Chromium\Application\chrome.exe",
        "$env:LOCALAPPDATA\Chromium\Application\chrome.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    foreach ($name in @(
        "msedge.exe",
        "chrome.exe",
        "brave.exe",
        "chromium.exe"
    )) {
        $command = Get-Command $name -ErrorAction SilentlyContinue

        if ($null -ne $command -and $command.Source) {
            return $command.Source
        }
    }

    foreach ($exeName in @(
        "msedge.exe",
        "chrome.exe",
        "brave.exe"
    )) {
        foreach ($root in @(
            "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths",
            "HKLM:\Software\Microsoft\Windows\CurrentVersion\App Paths"
        )) {
            $key = Join-Path $root $exeName

            try {
                $value = (Get-ItemProperty -Path $key -ErrorAction Stop).'(default)'

                if ($value -and (Test-Path $value)) {
                    return $value
                }
            }
            catch {
            }
        }
    }

    return $null
}

$browser = Find-ChromiumBrowser

if ($browser) {
    Write-Host "Radio browser: $browser"

    $arguments = @(
        "--app=$RadioUrl",
        "--autoplay-policy=no-user-gesture-required",
        "--no-first-run",
        "--start-minimized"
    )

    Start-Process -FilePath $browser -ArgumentList $arguments
    exit 0
}

Write-Warning "No Chromium-based browser was found. Falling back to the Windows default browser; autoplay may require one manual click."

Start-Process $RadioUrl
