# =========================================================================
#  Import the old SoapBox / community server data from ..\refs into
#  data\profiles\<id>\, which is what the multi-version engine serves.
#
#  refs/ is not part of the repository (see .gitignore), so this script is the
#  bridge between the research material and the shipped web data. Run it once
#  after cloning on a machine that has refs\, then commit data\profiles.
#
#  usage:
#    powershell -ExecutionPolicy Bypass -File tools\import-refs.ps1
#    ... -RefsRoot D:\NFSWorld\refs -Force
# =========================================================================
[CmdletBinding()]
param(
    [string]$RefsRoot = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $RefsRoot) { $RefsRoot = Join-Path $root 'refs' }
$profiles = Join-Path $root 'data\profiles'

if (-not (Test-Path $RefsRoot)) {
    Write-Host "refs folder not found: $RefsRoot" -ForegroundColor Yellow
    Write-Host "Nothing to import. The server still runs: it serves whatever is already in data\profiles."
    exit 0
}

New-Item -ItemType Directory -Path $profiles -Force | Out-Null

function Copy-Tree([string]$from, [string]$to) {
    if (-not (Test-Path -LiteralPath $from)) { Write-Host "  missing: $from" -ForegroundColor DarkYellow; return $false }
    $existing = 0
    if (Test-Path -LiteralPath $to) { $existing = (Get-ChildItem -LiteralPath $to -Recurse -File | Measure-Object).Count }
    if ($existing -gt 0 -and -not $Force) { Write-Host "  exists : $to ($existing files, use -Force)" -ForegroundColor DarkGray; return $true }
    New-Item -ItemType Directory -Path $to -Force | Out-Null
    Copy-Item -Path (Join-Path $from '*') -Destination $to -Recurse -Force
    $n = (Get-ChildItem -LiteralPath $to -Recurse -File | Measure-Object).Count
    Write-Host "  copied : $to ($n files)" -ForegroundColor Green
    return $true
}

function Write-ProfileIni([string]$id, [string]$name, [string]$build, [string]$format, [string]$description, [int]$sort) {
    $path = Join-Path $profiles "$id\profile.ini"
    if ((Test-Path $path) -and -not $Force) { return }
    New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
    @"
; DPT.Server client profile
id=$id
name=$name
build=$build
personaformat=$format
sort=$sort
description=$description
"@ | Set-Content -Path $path -Encoding UTF8
    Write-Host "  ini    : $path" -ForegroundColor Green
}

function Write-Template([string]$id, [string]$file, [string]$content) {
    $dir = Join-Path $profiles "$id\templates"
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $path = Join-Path $dir $file
    if ((Test-Path $path) -and -not $Force) { return }
    Set-Content -Path $path -Value $content -Encoding UTF8
    Write-Host "  tpl    : $path" -ForegroundColor Green
}

$legacyPersona = @'
<ProfileData>
	<Boost>0</Boost>
	<Cash>{{Cash}}</Cash>
	<IconIndex>{{IconIndex}}</IconIndex>
	<Level>{{Level}}</Level>
	<Motto>{{Motto}}</Motto>
	<Name>{{Name}}</Name>
	<PercentToLevel>{{PercentToLevel}}</PercentToLevel>
	<PersonaId>{{PersonaId}}</PersonaId>
	<Rating>{{Rating}}</Rating>
	<Rep>{{Rep}}</Rep>
	<RepAtCurrentLevel>0</RepAtCurrentLevel>
	<Score>{{Score}}</Score>
</ProfileData>
'@

$modernPersona = @'
<ProfileData xmlns="http://schemas.datacontract.org/2004/07/EA.NFSWO.ENGINE.TransferObjects.DriverPersona" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
	<bio i:nil="true"/>
	<boost>0</boost>
	<cash>{{Cash}}</cash>
	<clan>0</clan>
	<clanIcon i:nil="true"/>
	<iconIndex>{{IconIndex}}</iconIndex>
	<level>{{Level}}</level>
	<name>{{Name}}</name>
	<nickname>{{Name}}</nickname>
	<percentToLevel>{{PercentToLevel}}</percentToLevel>
	<personaId>{{PersonaId}}</personaId>
	<raceTickets>0</raceTickets>
	<rating>{{Rating}}</rating>
	<rep>{{Rep}}</rep>
	<repAtCurrentLevel>0</repAtCurrentLevel>
	<statusMessage>{{Motto}}</statusMessage>
</ProfileData>
'@

Write-Host ""
Write-Host "=== SoapBox 1.9.3 (build 1930) ==="
$ok = Copy-Tree (Join-Path $RefsRoot 'SoapBox193\www\soapbox\Engine.svc') (Join-Path $profiles 'soapbox-1930\Engine.svc')
if ($ok) {
    Write-ProfileIni 'soapbox-1930' 'SoapBox 1.9.3' '1930' 'legacy' 'SoapBox Race Offline 1.9.3 data, catalog + baskets + announcements' 10
    Write-Template 'soapbox-1930' 'ProfileData.xml' $legacyPersona
}

Write-Host ""
Write-Host "=== SoapBox offline 1.9.0 (August 2010) ==="
$ok = Copy-Tree (Join-Path $RefsRoot 'NFSW_August2010\www\soapbox\Engine.svc') (Join-Path $profiles 'soapbox-aug2010\Engine.svc')
if ($ok) {
    Write-ProfileIni 'soapbox-aug2010' 'SoapBox 1.9.0 (Aug 2010)' '2010' 'legacy' 'Early August 2010 offline build, small catalog' 30
    Write-Template 'soapbox-aug2010' 'ProfileData.xml' $legacyPersona
}

Write-Host ""
Write-Host "=== December 2010 server + DB ==="
$ok = Copy-Tree (Join-Path $RefsRoot 'Dec2010Server+DB\www\soapbox\Engine.svc') (Join-Path $profiles 'dec2010\Engine.svc')
if ($ok) {
    Write-ProfileIni 'dec2010' 'December 2010 build' '2010.12' 'legacy' 'December 2010 community server data' 20
    Write-Template 'dec2010' 'ProfileData.xml' $legacyPersona
}

Write-Host ""
Write-Host "=== 2011 rebuild (nfsw-2011) ==="
$ok = Copy-Tree (Join-Path $RefsRoot 'Nfsw_2011_Updated\Need For Speed World 2011\www\nfsw\Engine.svc') (Join-Path $profiles 'nfsw-2011\Engine.svc')
if ($ok) {
    Copy-Tree (Join-Path $RefsRoot 'Nfsw_2011_Updated\Need For Speed World 2011\www\basket') (Join-Path $profiles 'nfsw-2011\basket') | Out-Null
    Write-ProfileIni 'nfsw-2011' 'NFSW 2011 rebuild' '2011' 'modern' 'May 2011 server tree: per-persona folders, powerups, LuckyDraw' 5
    Write-Template 'nfsw-2011' 'ProfileData.xml' $modernPersona
}

# news images used by /Engine.svc/LoginAnnouncements
$newsSrc = Join-Path $RefsRoot 'NFSWorldServer\bin\Debug\net48\data\news'
if (Test-Path $newsSrc) {
    Write-Host ""
    Write-Host "=== news images ==="
    foreach ($p in @('soapbox-1930', 'soapbox-aug2010', 'dec2010', 'nfsw-2011')) {
        $dst = Join-Path $profiles "$p\Engine.svc\news"
        if (-not (Test-Path (Join-Path $profiles $p))) { continue }
        New-Item -ItemType Directory -Path $dst -Force | Out-Null
        Copy-Item (Join-Path $newsSrc '*.png') $dst -Force
        Write-Host "  news   : $dst" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Done. Profiles in ${profiles}:" -ForegroundColor Cyan
Get-ChildItem $profiles -Directory | ForEach-Object {
    $n = (Get-ChildItem $_.FullName -Recurse -File | Measure-Object).Count
    Write-Host ("  {0,-18} {1} files" -f $_.Name, $n)
}