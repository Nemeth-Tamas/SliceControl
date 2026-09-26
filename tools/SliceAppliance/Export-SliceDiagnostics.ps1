$ErrorActionPreference = "Continue"

param(
    [int]$Tail = 250,
    [string]$OutputPath = ""
)

$logDirectory = Join-Path $env:LOCALAPPDATA "SliceAppliance\Logs"
$documentsRoot = [Environment]::GetFolderPath("MyDocuments")
$diagnosticDirectory = Join-Path $documentsRoot "SliceTranscribe\Diagnostics"

if (-not $OutputPath) {
    New-Item -ItemType Directory -Path $diagnosticDirectory -Force | Out-Null
    $OutputPath = Join-Path $diagnosticDirectory ("slice-diagnostics-{0}.txt" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
}

$builder = [System.Text.StringBuilder]::new()

function Add-Line {
    param([string]$Text = "")
    [void]$builder.AppendLine($Text)
}

function Add-Section {
    param([string]$Title)
    Add-Line
    Add-Line ("=" * 80)
    Add-Line $Title
    Add-Line ("=" * 80)
}

function Add-CommandOutput {
    param(
        [string]$Title,
        [scriptblock]$Command
    )

    Add-Section $Title

    try {
        $result = & $Command | Out-String -Width 260
        Add-Line ($result.TrimEnd())
    }
    catch {
        Add-Line ("ERROR: {0}" -f $_.Exception.ToString())
    }
}

Add-Line "Slice Appliance Diagnostic Bundle"
Add-Line ("Generated: {0}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss zzz"))
Add-Line ("Computer:  {0}" -f $env:COMPUTERNAME)
Add-Line ("User:      {0}\{1}" -f $env:USERDOMAIN, $env:USERNAME)
Add-Line ("Logs:      {0}" -f $logDirectory)

Add-CommandOutput "SYSTEM / UPTIME" {
    $os = Get-CimInstance Win32_OperatingSystem
    [pscustomobject]@{
        Caption = $os.Caption
        Version = $os.Version
        LastBootUpTime = $os.LastBootUpTime
        FreePhysicalMemoryMB = [math]::Round($os.FreePhysicalMemory / 1024, 1)
        TotalVisibleMemoryMB = [math]::Round($os.TotalVisibleMemorySize / 1024, 1)
    } | Format-List
}

Add-CommandOutput "SCHEDULED TASKS" {
    $rows = foreach ($name in @("SliceTranscribe","Slice Retro Radio","Slice MCP","Slice SonoBus")) {
        $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        $info = Get-ScheduledTaskInfo -TaskName $name -ErrorAction SilentlyContinue

        if ($null -eq $task) {
            [pscustomobject]@{
                TaskName = $name
                State = "Not installed"
                LastTaskResult = $null
                LastRunTime = $null
                NextRunTime = $null
            }
        }
        else {
            [pscustomobject]@{
                TaskName = $name
                State = $task.State
                LastTaskResult = $info.LastTaskResult
                LastRunTime = $info.LastRunTime
                NextRunTime = $info.NextRunTime
            }
        }
    }

    $rows | Format-Table -AutoSize
}

Add-CommandOutput "RELEVANT PROCESSES" {
    Get-Process -Name "SliceTranscribe","SliceMcp","vlc","SonoBus","powershell","pwsh" -ErrorAction SilentlyContinue |
        Select-Object ProcessName, Id, @{N="WorkingSetMB";E={[math]::Round($_.WorkingSet64 / 1MB, 1)}}, @{N="PrivateMB";E={[math]::Round($_.PrivateMemorySize64 / 1MB, 1)}}, HandleCount, @{N="Threads";E={$_.Threads.Count}}, StartTime |
        Sort-Object ProcessName, Id |
        Format-Table -AutoSize
}

Add-CommandOutput "WINDOWS SOUND DEVICES" {
    Get-CimInstance Win32_SoundDevice |
        Select-Object Name, Status, Manufacturer, PNPDeviceID |
        Format-Table -AutoSize
}

Add-CommandOutput "HP SLICE COLLABORATION COVER PNP" {
    Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object InstanceId -Like "*VID_03F0&PID_0D66*" |
        Select-Object Status, Class, FriendlyName, InstanceId |
        Format-Table -AutoSize
}

Add-CommandOutput "HP TELEPHONY SERVICE" {
    Get-Service HPSliceTelephonyService -ErrorAction SilentlyContinue |
        Select-Object Status, Name, DisplayName |
        Format-Table -AutoSize
}

Add-Section "STRUCTURED DIAGNOSTICS (RECENT)"
$structuredPath = Join-Path $logDirectory "diagnostics.jsonl"

if (Test-Path $structuredPath) {
    foreach ($line in Get-Content $structuredPath -Tail $Tail) {
        try {
            $event = $line | ConvertFrom-Json
            $data = if ($null -ne $event.data) { $event.data | ConvertTo-Json -Compress -Depth 10 } else { "" }
            $exception = if ($null -ne $event.exception) { " EXCEPTION=" + ($event.exception | ConvertTo-Json -Compress -Depth 10) } else { "" }

            Add-Line ("{0} [{1}] [{2}] {3} {4}{5}" -f $event.timestamp, $event.level, $event.category, $event.event, $data, $exception)
        }
        catch {
            Add-Line $line
        }
    }
}
else {
    Add-Line "diagnostics.jsonl does not exist yet."
}

foreach ($file in @("SliceTranscribe.log","SliceTranscribe-error.log","SonoBus.log")) {
    Add-Section ("RAW LOG: {0} (last {1} lines)" -f $file, $Tail)
    $path = Join-Path $logDirectory $file

    if (Test-Path $path) {
        Get-Content $path -Tail $Tail | ForEach-Object { Add-Line $_ }
    }
    else {
        Add-Line "File not found."
    }
}

[System.IO.File]::WriteAllText($OutputPath, $builder.ToString(), [System.Text.UTF8Encoding]::new($false))

Write-Host
Write-Host "Diagnostic bundle written to:"
Write-Host $OutputPath
Write-Host
Write-Host "This bundle intentionally does NOT include assistant.json or API credentials."
