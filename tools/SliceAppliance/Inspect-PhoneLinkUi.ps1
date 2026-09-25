param(
    [switch]$ButtonsOnly
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$processes = Get-Process -Name "PhoneExperienceHost" -ErrorAction SilentlyContinue

if (-not $processes) {
    Write-Host "Phone Link is not running."
    Write-Host "Open Phone Link, then run this script again."
    exit 1
}

$root = [Windows.Automation.AutomationElement]::RootElement
$trueCondition = [Windows.Automation.Condition]::TrueCondition

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
        Write-Host ("  WINDOW: Name='{0}' Class='{1}' AutomationId='{2}'" -f $window.Current.Name, $window.Current.ClassName, $window.Current.AutomationId)

        $elements = $window.FindAll(
            [Windows.Automation.TreeScope]::Descendants,
            $trueCondition
        )

        for ($i = 0; $i -lt $elements.Count; $i++) {
            $element = $elements.Item($i)
            $controlType = $element.Current.ControlType.ProgrammaticName

            if ($ButtonsOnly -and $controlType -ne "ControlType.Button") {
                continue
            }

            $name = $element.Current.Name
            $automationId = $element.Current.AutomationId
            $className = $element.Current.ClassName
            $enabled = $element.Current.IsEnabled
            $offscreen = $element.Current.IsOffscreen

            if ([string]::IsNullOrWhiteSpace($name) -and [string]::IsNullOrWhiteSpace($automationId)) {
                continue
            }

            Write-Host ("    {0} Name='{1}' Id='{2}' Class='{3}' Enabled={4} Offscreen={5}" -f $controlType, $name, $automationId, $className, $enabled, $offscreen)
        }
    }
}
