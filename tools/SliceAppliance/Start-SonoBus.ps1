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
    exit 0
}

$sonoBus = Find-SonoBus

if (-not $sonoBus) {
    Write-SonoBusLog "SonoBus.exe was not found; task will remain idle."
    exit 0
}

$userName = if ($config.Username) { [string]$config.Username } else { "Slice" }

# SonoBus headless mode exits immediately on the tested Windows build.
# The normal standalone application supports the same command-line group
# auto-connect options and initializes the Windows audio device reliably.
$arguments = @(
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

Write-SonoBusLog ("Watching SonoBus group '{0}' as '{1}' in Windows GUI mode." -f $config.Group, $userName)

$hadRunningInstance = $false

while ($true) {
    try {
        $running = @(
            Get-Process -Name "SonoBus" -ErrorAction SilentlyContinue
        )

        if ($running.Count -gt 0) {
            if (-not $hadRunningInstance) {
                $ids = ($running | ForEach-Object { $_.Id }) -join ", "
                Write-SonoBusLog ("SonoBus is running (PID(s) {0})." -f $ids)
                $hadRunningInstance = $true
            }

            Start-Sleep -Seconds 2
            continue
        }

        if ($hadRunningInstance) {
            Write-SonoBusLog "SonoBus is no longer running; relaunching."
            $hadRunningInstance = $false
        }

        $launched = Start-Process -FilePath $sonoBus -ArgumentList $arguments -WindowStyle Minimized -PassThru
        Write-SonoBusLog ("SonoBus launch requested (PID {0})." -f $launched.Id)

        Start-Sleep -Seconds 2

        $runningAfterLaunch = @(
            Get-Process -Name "SonoBus" -ErrorAction SilentlyContinue
        )

        if ($runningAfterLaunch.Count -gt 0) {
            $ids = ($runningAfterLaunch | ForEach-Object { $_.Id }) -join ", "
            Write-SonoBusLog ("SonoBus healthy after launch (PID(s) {0})." -f $ids)
            $hadRunningInstance = $true
        }
        else {
            Write-SonoBusLog "SonoBus launch produced no surviving process; retrying in 3 seconds."
            Start-Sleep -Seconds 3
        }
    }
    catch {
        Write-SonoBusLog ("SonoBus launch/watch error: {0}" -f $_.Exception.Message)
        Start-Sleep -Seconds 3
    }
}
