$ErrorActionPreference = "Stop"

foreach ($name in @(
    "SliceTranscribe",
    "Slice Retro Radio"
)) {
    $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue

    if ($null -ne $task) {
        Unregister-ScheduledTask -TaskName $name -Confirm:$false
        Write-Host "Removed: $name"
    }
}
