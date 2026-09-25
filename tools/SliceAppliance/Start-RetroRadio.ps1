param(
    [string]$RadioUrl = "https://myonlineradio.hu/retro-radio"
)

$ErrorActionPreference = "Stop"

function Find-Edge {
    $candidates = @(
        "$env:ProgramFiles(x86)\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "Microsoft Edge was not found."
}

$edge = Find-Edge

$arguments = @(
    "--app=$RadioUrl",
    "--autoplay-policy=no-user-gesture-required",
    "--no-first-run",
    "--start-minimized"
)

Start-Process -FilePath $edge -ArgumentList $arguments
