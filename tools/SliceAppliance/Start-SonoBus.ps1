$ErrorActionPreference = "Stop"

$configPath = Join-Path $env:LOCALAPPDATA "SliceAppliance\sonobus.json"
$logDirectory = Join-Path $env:LOCALAPPDATA "SliceAppliance\Logs"
$logPath = Join-Path $logDirectory "SonoBus.log"

New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

function Write-SonoBusLog {
    param([string]$Message)

    $line = "{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Add-Content -Path $logPath -Value $line
}

function Find-SonoBus {
    $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")

    $candidates = @(
        (Join-Path $env:ProgramFiles "SonoBus\SonoBus.exe"),
        $(if ($programFilesX86) { Join-Path $programFilesX86 "SonoBus\SonoBus.exe" } else { $null }),
        (Join-Path $env:LOCALAPPDATA "Programs\SonoBus\SonoBus.exe")
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    $command = Get-Command "SonoBus.exe" -ErrorAction SilentlyContinue

    if ($null -ne $command -and $command.Source) {
        return $command.Source
    }

    foreach ($root in @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\App Paths"
    )) {
        $key = Join-Path $root "SonoBus.exe"

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

if (-not (Test-Path $configPath)) {
    Write-SonoBusLog "SonoBus config not found; task will remain idle."
    exit 0
}

$config = Get-Content -Raw -Path $configPath | ConvertFrom-Json

if (-not $config.Group) {
    Write-SonoBusLog "SonoBus config is missing Group."
    exit 1
}

$sonoBus = Find-SonoBus

if (-not $sonoBus) {
    Write-SonoBusLog "SonoBus.exe was not found."
    exit 1
}

Get-Process -Name "SonoBus" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

Start-Sleep -Milliseconds 300

$userName = if ($config.Username) { [string]$config.Username } else { "Slice" }

$arguments = @(
    "--headless",
    "--group", [string]$config.Group,
    "--username", $userName
)

if ($config.Password) {
    $arguments += @(
        "--group-password",
        [string]$config.Password
    )
}

if ($config.ConnectionServer) {
    $arguments += @(
        "--connectionserver",
        [string]$config.ConnectionServer
    )
}

if ($config.SetupFile) {
    $setupPath = [Environment]::ExpandEnvironmentVariables([string]$config.SetupFile)

    if (Test-Path $setupPath) {
        $arguments += @(
            "--load-setup",
            $setupPath
        )
    }
    else {
        Write-SonoBusLog ("Configured setup file does not exist: {0}" -f $setupPath)
    }
}

Write-SonoBusLog ("Starting SonoBus group '{0}' as '{1}'." -f $config.Group, $userName)

while ($true) {
    try {
        $process = Start-Process -FilePath $sonoBus -ArgumentList $arguments -WindowStyle Hidden -PassThru
        Write-SonoBusLog ("SonoBus started (PID {0})." -f $process.Id)

        $process.WaitForExit()

        Write-SonoBusLog ("SonoBus exited (PID {0}); restarting in 3 seconds." -f $process.Id)
    }
    catch {
        Write-SonoBusLog ("SonoBus launch/watch error: {0}" -f $_.Exception.Message)
    }

    Start-Sleep -Seconds 3
}
