param(
    [string]$WireGuardAddress = "10.10.10.12"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $repoRoot "src\SliceMcp\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\SliceMcp.exe"

if (-not (Test-Path $exe)) {
    throw "Slice MCP executable was not found at $exe"
}

$logDirectory = Join-Path $env:LOCALAPPDATA "SliceAppliance\Logs"
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

$stdoutLog = Join-Path $logDirectory "SliceMcp.log"
$stderrLog = Join-Path $logDirectory "SliceMcp-error.log"

$deadline = (Get-Date).AddMinutes(2)

while ((Get-Date) -lt $deadline) {
    $address = Get-NetIPAddress -IPAddress $WireGuardAddress -ErrorAction SilentlyContinue

    if ($null -ne $address) {
        break
    }

    Start-Sleep -Seconds 2
}

if ($null -eq (Get-NetIPAddress -IPAddress $WireGuardAddress -ErrorAction SilentlyContinue)) {
    "WireGuard address $WireGuardAddress was not available after two minutes." |
        Add-Content -Path $stderrLog

    exit 2
}

try {
    & $exe >> $stdoutLog 2>> $stderrLog
    exit $LASTEXITCODE
}
catch {
    $_ | Out-String | Add-Content -Path $stderrLog
    exit 1
}
