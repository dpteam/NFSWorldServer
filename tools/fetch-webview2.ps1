# =========================================================================
#  DPT.Server / NFS World - fetch the WebView2 SDK used by the launcher
#
#  The launcher window is a WebView2 host, so build.cmd needs three managed
#  DLLs and the native loader next to the exe. They are NOT in the repo:
#  this script downloads the NuGet package once and unpacks exactly the files
#  the launcher references. The WebView2 *Runtime* itself comes from Windows
#  Update / Edge and is not part of the package.
#
#  usage:
#    powershell -ExecutionPolicy Bypass -File tools\fetch-webview2.ps1
#    ... -Version 1.0.4314.35    pin another SDK build
#    ... -Force                  re-download even if the files are present
# =========================================================================
[CmdletBinding()]
param(
    [string]$Version = '1.0.4314.35',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
$dst = Join-Path $root 'lib\webview2'
$core = Join-Path $dst 'Microsoft.Web.WebView2.Core.dll'
$winforms = Join-Path $dst 'Microsoft.Web.WebView2.WinForms.dll'
$loader = Join-Path $dst 'WebView2Loader.dll'

if ((Test-Path $core) -and (Test-Path $winforms) -and (Test-Path $loader) -and -not $Force) {
    Write-Host "WebView2 SDK already present in lib\webview2"
    Write-Host ("  " + (Get-Item $core).Length + " bytes Core, " + (Get-Item $winforms).Length + " bytes WinForms")
    Write-Host "Use -Force to refresh it."
    exit 0
}

New-Item -ItemType Directory -Path $dst -Force | Out-Null
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('webview2-' + $Version)
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
$pkg = Join-Path $tmp 'webview2.nupkg'

$url = 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/' + $Version.ToLowerInvariant() +
       '/microsoft.web.webview2.' + $Version.ToLowerInvariant() + '.nupkg'

if (-not (Test-Path $pkg)) {
    Write-Host "Downloading $Version ..."
    try {
        Invoke-WebRequest -Uri $url -OutFile $pkg -TimeoutSec 180 -UseBasicParsing
    }
    catch {
        Write-Host ("[ERROR] download failed: " + $_.Exception.Message)
        Write-Host "        Check the version: https://www.nuget.org/packages/Microsoft.Web.WebView2"
        exit 1
    }
}

Write-Host ("Package: " + $pkg + " (" + (Get-Item $pkg).Length + " bytes)")

$map = @{
    'lib/net462/Microsoft.Web.WebView2.Core.dll'   = 'Microsoft.Web.WebView2.Core.dll'
    'lib/net462/Microsoft.Web.WebView2.WinForms.dll' = 'Microsoft.Web.WebView2.WinForms.dll'
    'runtimes/win-x64/native/WebView2Loader.dll'   = 'WebView2Loader.dll'
    'runtimes/win-x86/native/WebView2Loader.dll'   = 'WebView2Loader.x86.dll'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($pkg)
try {
    foreach ($entry in $map.Keys) {
        $item = $zip.Entries | Where-Object { $_.FullName -eq $entry }
        if (-not $item) {
            Write-Host ("[ERROR] missing in package: " + $entry)
            exit 1
        }
        $out = Join-Path $dst $map[$entry]
        [IO.Compression.ZipFileExtensions]::ExtractToFile($item, $out, $true)
        Write-Host ("  " + $entry + " -> lib\webview2\" + $map[$entry])
    }
}
finally {
    $zip.Dispose()
}

$missing = @()
foreach ($f in @($core, $winforms, $loader)) { if (-not (Test-Path $f)) { $missing += $f } }
if ($missing.Count -gt 0) {
    Write-Host ("[ERROR] not extracted: " + ($missing -join ', '))
    exit 1
}

Write-Host ''
Write-Host "WebView2 SDK $Version ready."
Write-Host 'The runtime (WebView2 Runtime / Edge) must be installed on the machine:'
Write-Host '  https://go.microsoft.com/fwlink/p/?LinkId=2124703'
exit 0