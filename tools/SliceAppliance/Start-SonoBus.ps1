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

$configuredGroup = "(SonoBus last group)"

if (Test-Path $configPath) {
    try {
        $config = Get-Content -Raw -Path $configPath | ConvertFrom-Json

        if ($config.Group) {
            $configuredGroup = [string]$config.Group
        }
    }
    catch {
        Write-SonoBusLog ("Could not read legacy SonoBus config label: {0}" -f $_.Exception.Message)
    }
}

$sonoBus = Find-SonoBus

if (-not $sonoBus) {
    Write-SonoBusLog "SonoBus.exe was not found; task will remain idle."
    exit 0
}

# On the tested Windows SonoBus build, command-line group auto-connect
# is unreliable. SonoBus's built-in "Auto-Reconnect to Last Group"
# setting is the source of truth. Launch the normal application only.
$arguments = @()

Write-SonoBusLog ("Watching SonoBus in Windows GUI mode; expected last group '{0}'." -f $configuredGroup)

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
