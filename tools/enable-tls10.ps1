# =========================================================================
#  DPT.Server / NFS World - enable TLS 1.0 for EA-Chat
#
#  The 2010/2011 client speaks TLS 1.0. Windows 10/11 disable it by default in
#  some configurations (and TLS 1.3/1.2-only setups), which shows up as
#  "TLS handshake failed" in the log and no in-game chat.
#
#  This script sets the SCHANNEL registry values for the SERVER role of TLS 1.0
#  (and optionally 1.1). Reboot or restart the server afterwards.
#
#    powershell -ExecutionPolicy Bypass -File tools\enable-tls10.ps1
#    ... -Disable    revert to the Windows defaults
#
#  Requires admin (UAC prompt).
# =========================================================================
[CmdletBinding()]
param(
    [switch]$Disable,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath))
    if ($Disable) { $argList += '-Disable' }
    if ($NoPause) { $argList += '-NoPause' }
    Write-Host "Нужны права администратора - сейчас появится запрос UAC." -ForegroundColor Yellow
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList | Out-Null
    Start-Sleep -Seconds 2
    exit
}

$enabled = if ($Disable) { 0 } else { 1 }
$disabledByDefault = if ($Disable) { 1 } else { 0 }

$protocols = @('TLS 1.0', 'TLS 1.1')
Write-Host ''
Write-Host ('=== EA-Chat TLS: ' + $(if ($Disable) { 'disable TLS 1.0/1.1' } else { 'enable TLS 1.0/1.1' }) + ' ===') -ForegroundColor Cyan

foreach ($proto in $protocols) {
    foreach ($role in @('Server', 'Client')) {
        $path = 'HKLM:\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\' + $proto + '\' + $role
        try {
            if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
            New-ItemProperty -Path $path -Name 'Enabled' -Value $enabled -PropertyType DWord -Force | Out-Null
            New-ItemProperty -Path $path -Name 'DisabledByDefault' -Value $disabledByDefault -PropertyType DWord -Force | Out-Null
            Write-Host ('  ok   : ' + $proto + ' / ' + $role + ' -> Enabled=' + $enabled + ' DisabledByDefault=' + $disabledByDefault) -ForegroundColor Green
        } catch {
            Write-Host ('  FAIL : ' + $proto + ' / ' + $role + ' -> ' + $_.Exception.Message) -ForegroundColor Red
        }
    }
}

Write-Host ''
Write-Host 'Restart NFSWorldServer.exe (and possibly Windows) for the change to apply.'
Write-Host 'Check the log for: XMPP  TLS established with ... (Tls)'

if (-not $NoPause) {
    Write-Host ''
    Read-Host 'Enter - close' | Out-Null
}