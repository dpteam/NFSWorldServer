# =========================================================================
#  DPT.Server / NFS World - self signed certificate for EA-Chat (XMPP/TLS)
#
#  The 2010/2011 client validates nothing (it ignores the certificate chain),
#  it only needs *a* TLS certificate with a private key. This script creates a
#  20 year self signed cert and exports it as data\certs\xmpp.pfx (password nfsw).
#
#  usage:
#    powershell -ExecutionPolicy Bypass -File tools\make-cert.ps1
#    ... -Force          overwrite an existing pfx
#    ... -Password nfsw  different password (also set xmpp.certPassword)
# =========================================================================
[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Password = 'nfsw'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root 'data\certs'
$pfx = Join-Path $dir 'xmpp.pfx'
$cer = Join-Path $dir 'xmpp.cer'

if ((Test-Path $pfx) -and -not $Force) {
    Write-Host "Certificate already exists: $pfx"
    Write-Host "Use -Force to regenerate it."
    exit 0
}

New-Item -ItemType Directory -Path $dir -Force | Out-Null

$cert = $null
try {
    $cert = New-SelfSignedCertificate `
        -Subject 'CN=nfsw.local, O=DPT.Server, OU=NFS World' `
        -DnsName 'nfsw.local', 'localhost' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(20) `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -KeyExportPolicy Exportable `
        -HashAlgorithm SHA256
}
catch {
    Write-Host "New-SelfSignedCertificate failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "On Windows 7 / XP generate a pfx with makecert + pvk2pfx instead:"
    Write-Host "  makecert -r -pe -n ""CN=nfsw.local"" -sky exchange -ss my -len 2048 xmpp.cer"
    Write-Host "  pvk2pfx -pvk xmpp.pvk -spc xmpp.cer -pfx data\certs\xmpp.pfx -pi $Password"
    exit 1
}

$secure = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null

Write-Host ""
Write-Host "Certificate created:" -ForegroundColor Green
Write-Host "  pfx      : $pfx"
Write-Host "  cer      : $cer"
Write-Host "  subject  : $($cert.Subject)"
Write-Host "  expires  : $($cert.NotAfter)"
Write-Host "  password : $Password  (config.ini -> xmpp.certPassword)"
Write-Host ""
Write-Host "Keep the pfx next to the server in bin\data\certs\ (build.cmd copies it)."