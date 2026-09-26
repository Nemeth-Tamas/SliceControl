param(
    [Parameter(Mandatory = $true)]
    [string]$Group,

    [string]$Username = "Slice",

    [string]$ConnectionServer = "aoo.sonobus.net",

    [string]$SetupFile = "",

    [switch]$NoPassword
)

$ErrorActionPreference = "Stop"

$configDirectory = Join-Path $env:LOCALAPPDATA "SliceAppliance"
$configPath = Join-Path $configDirectory "sonobus.json"

New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null

$password = ""

if (-not $NoPassword) {
    $securePassword = Read-Host "SonoBus group password (leave blank for none)" -AsSecureString

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)

    try {
        $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

$config = [ordered]@{
    Group = $Group
    Username = $Username
    Password = $password
    ConnectionServer = $ConnectionServer
    SetupFile = $SetupFile
}

$config |
    ConvertTo-Json |
    Set-Content -Path $configPath -Encoding UTF8

try {
    $acl = Get-Acl $configPath
    $acl.SetAccessRuleProtection($true, $false)

    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "$env:USERDOMAIN\$env:USERNAME",
        "FullControl",
        "Allow"
    )

    $acl.SetAccessRule($rule)
    Set-Acl -Path $configPath -AclObject $acl
}
catch {
    Write-Warning "Could not restrict SonoBus config ACL. The file contains the group password in plaintext."
}

Write-Host
Write-Host "SonoBus receiver configured:"
Write-Host ("  Group:   {0}" -f $Group)
Write-Host ("  Name:    {0}" -f $Username)
Write-Host ("  Server:  {0}" -f $ConnectionServer)
Write-Host ("  Setup:   {0}" -f $(if ($SetupFile) { $SetupFile } else { "(last/default SonoBus audio setup)" }))
Write-Host ("  Config:  {0}" -f $configPath)

$task = Get-ScheduledTask -TaskName "Slice SonoBus" -ErrorAction SilentlyContinue

if ($null -ne $task) {
    Stop-ScheduledTask -TaskName "Slice SonoBus" -ErrorAction SilentlyContinue

    Get-Process -Name "SonoBus" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    Start-ScheduledTask -TaskName "Slice SonoBus"

    Write-Host
    Write-Host "Slice SonoBus task restarted."
}
else {
    Write-Host
    Write-Warning "Slice SonoBus scheduled task is not installed yet."
    Write-Host "Run Install-SliceAppliance.ps1 once, then rerun this configuration script."
}
