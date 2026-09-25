param(
    [switch]$ButtonsOnly,
    [switch]$Notifications
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [Windows.Automation.AutomationElement]::RootElement
$trueCondition = [Windows.Automation.Condition]::TrueCondition

function Get-ProcessNameSafe([int]$ProcessId) {
    try {
        return (Get-Process -Id $ProcessId -ErrorAction Stop).ProcessName
    }
    catch {
        return "PID-$ProcessId"
    }
}

function Write-ElementTree(
    [Windows.Automation.AutomationElement]$Container,
    [string]$Prefix
) {
    $elements = $Container.FindAll(
        [Windows.Automation.TreeScope]::Descendants,
        $trueCondition
    )

    for ($i = 0; $i -lt $elements.Count; $i++) {
        $element = $elements.Item($i)
        $controlType = $element.Current.ControlType.ProgrammaticName

        if ($ButtonsOnly -and $controlType -ne "ControlType.Button") {
            continue
        }

        if ($element.Current.IsOffscreen) {
            continue
        }

        $name = $element.Current.Name
        $automationId = $element.Current.AutomationId
        $className = $element.Current.ClassName
        $enabled = $element.Current.IsEnabled
        $pid = $element.Current.ProcessId

        if ([string]::IsNullOrWhiteSpace($name) -and [string]::IsNullOrWhiteSpace($automationId)) {
            continue
        }

        $processName = Get-ProcessNameSafe $pid
        $line = "{0}{1} Process='{2}' Name='{3}' Id='{4}' Class='{5}' Enabled={6}" -f $Prefix, $controlType, $processName, $name, $automationId, $className, $enabled
        Write-Host $line
    }
}

if ($Notifications) {
    Write-Host "Scanning visible desktop notification / toast UI..."
    Write-Host

    $candidateProcesses = @(
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "explorer",
        "ApplicationFrameHost",
        "PhoneExperienceHost"
    )

    $windows = $root.FindAll(
        [Windows.Automation.TreeScope]::Children,
        $trueCondition
    )

    for ($i = 0; $i -lt $windows.Count; $i++) {
        $window = $windows.Item($i)

        if ($window.Current.IsOffscreen) {
            continue
        }

        $pid = $window.Current.ProcessId
        $processName = Get-ProcessNameSafe $pid

        if ($candidateProcesses -notcontains $processName) {
            continue
        }

        $header = "WINDOW Process='{0}' Name='{1}' Class='{2}' Id='{3}'" -f $processName, $window.Current.Name, $window.Current.ClassName, $window.Current.AutomationId
        Write-Host $header

        Write-ElementTree $window "  "
        Write-Host
    }

    exit 0
}

$processes = Get-Process -Name "PhoneExperienceHost" -ErrorAction SilentlyContinue

if (-not $processes) {
    Write-Host "Phone Link is not running."
    Write-Host "Open Phone Link, then run this script again."
    exit 1
}

foreach ($process in $processes) {
    Write-Host
    Write-Host ("Phone Link PID {0}" -f $process.Id)

    $pidCondition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ProcessIdProperty,
        $process.Id
    )

    $windows = $root.FindAll(
        [Windows.Automation.TreeScope]::Children,
        $pidCondition
    )

    if ($windows.Count -eq 0) {
        Write-Host "  No top-level UI Automation windows found."
        continue
    }

    for ($w = 0; $w -lt $windows.Count; $w++) {
        $window = $windows.Item($w)

        Write-Host
        $header = "  WINDOW: Name='{0}' Class='{1}' AutomationId='{2}'" -f $window.Current.Name, $window.Current.ClassName, $window.Current.AutomationId
        Write-Host $header

        Write-ElementTree $window "    "
    }
}
