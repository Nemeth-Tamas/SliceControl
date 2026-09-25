param(
    [Parameter(Mandatory = $true)]
    [string]$EndpointHost,

    [int]$EndpointPort = 13231,

    [string]$Address = "10.10.10.12/32",

    [string]$AllowedIPs = "10.10.10.0/24",

    [string]$RouterPublicKey = "oVkzTLGDWgKWtook3V208z3utg1ezAVDO6Vv0x8yTw8=",

    [string]$TunnelName = "SliceHome",

    [switch]$ForceNewKey
)

$ErrorActionPreference = "Stop"

$wireGuardDirectory = Join-Path $env:ProgramFiles "WireGuard"
$wg = Join-Path $wireGuardDirectory "wg.exe"
$wireGuard = Join-Path $wireGuardDirectory "wireguard.exe"

if (-not (Test-Path $wg) -or -not (Test-Path $wireGuard)) {
    throw @"
WireGuard for Windows is not installed.
Install it first, then rerun this script:

  winget install --id WireGuard.WireGuard -e
"@
}

$configDirectory = Join-Path $env:ProgramData "SliceAppliance\WireGuard"
New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null

$configPath = Join-Path $configDirectory "$TunnelName.conf"
$publicKeyPath = Join-Path $configDirectory "$TunnelName.publickey.txt"

$privateKey = $null

if ((Test-Path $configPath) -and -not $ForceNewKey) {
    $privateKeyLine = Get-Content $configPath |
        Where-Object { $_ -match '^PrivateKey\s*=' } |
        Select-Object -First 1

    if ($privateKeyLine) {
        $privateKey = ($privateKeyLine -split '=', 2)[1].Trim()
        Write-Host "Reusing existing SliceHome WireGuard keypair."
    }
}

if ([string]::IsNullOrWhiteSpace($privateKey)) {
    $privateKey = (& $wg genkey).Trim()
}

if ([string]::IsNullOrWhiteSpace($privateKey)) {
    throw "wg.exe did not generate a private key."
}

$publicKey = ($privateKey | & $wg pubkey).Trim()

if ([string]::IsNullOrWhiteSpace($publicKey)) {
    throw "wg.exe did not generate a public key."
}

$endpoint = "{0}:{1}" -f $EndpointHost, $EndpointPort

$config = @"
[Interface]
PrivateKey = $privateKey
Address = $Address

[Peer]
PublicKey = $RouterPublicKey
AllowedIPs = $AllowedIPs
Endpoint = $endpoint
PersistentKeepalive = 25
"@

Set-Content -Path $configPath -Value $config -Encoding ascii
Set-Content -Path $publicKeyPath -Value $publicKey -Encoding ascii

try {
    & icacls $configPath /inheritance:r /grant:r "SYSTEM:(F)" "Administrators:(F)" "$env:USERNAME:(R)" *> $null
    & icacls $publicKeyPath /inheritance:r /grant:r "SYSTEM:(F)" "Administrators:(F)" "$env:USERNAME:(R)" *> $null
}
catch {
    Write-Warning "Could not tighten ACLs on the WireGuard files."
}

try {
    & $wireGuard /uninstalltunnelservice $TunnelName *> $null
}
catch {
}

Write-Host "Installing WireGuard tunnel service $TunnelName..."
& $wireGuard /installtunnelservice $configPath

Start-Sleep -Seconds 2

Write-Host
Write-Host "Slice WireGuard public key:"
Write-Host "  $publicKey"
Write-Host
Write-Host "Add this peer on RB5009-Main-Router:"
Write-Host
Write-Host ('/interface/wireguard/peers/add interface=wireguard1 public-key="{0}" allowed-address=10.10.10.12/32 comment="HP Slice G2"' -f $publicKey)
Write-Host
Write-Host "Tunnel configuration:"
Write-Host "  Slice address:   $Address"
Write-Host "  Router endpoint: $endpoint"
Write-Host "  Router key:      $RouterPublicKey"
Write-Host "  Allowed IPs:     $AllowedIPs"
Write-Host "  Config:          $configPath"
Write-Host
Write-Host "After adding the MikroTik peer, check:"
Write-Host "  ping 10.10.10.9"
Write-Host "  ping 10.10.10.11"
Write-Host "  & '$wg' show"
