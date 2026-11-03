# =========================================================================
#  DPT.Server / NFS World - покрытие Engine.svc по карте вызовов клиента
#
#  Берём пути из декомпилированного MyBinding.cs (реверс в приоритете,
#  AGENTS.md) и проверяем: отвечает ли сервер кодом, файлом профиля или
#  никак. "Никак" - это и есть список того, что клиент может дёрнуть и
#  получить пустоту.
#
#  usage:
#    powershell -File tools\protocol-coverage.ps1
#    ... -Src "refs\_Src"            дерево декомпила (см. decompile-client.ps1)
#    ... -Profile soapbox-1613       проверять конкретный профиль
#    ... -Verbose                    печатать и покрытые тоже
#
#  Код выхода: 0 - всегда (это отчёт, а не тест).
# =========================================================================
[CmdletBinding()]
param(
    [string]$Src = '',
    [string]$Profile = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Src) { $Src = Join-Path $root 'refs\_Src' }

$binding = Join-Path $Src 'server_packet\MyBinding.cs'
if (-not (Test-Path -LiteralPath $binding)) {
    Write-Host "[skip] no decompiled client: $binding"
    Write-Host "       Run tools\decompile-client.ps1 first."
    exit 0
}

# ---------------------------------------------------------------- из клиента

$text = [IO.File]::ReadAllText($binding, [Text.Encoding]::UTF8)
$raw = [regex]::Matches($text, '/Engine\.svc/([A-Za-z0-9_/\{\}\.\-]*)') |
    ForEach-Object { $_.Groups[1].Value.TrimEnd('/') } |
    Sort-Object -Unique

function Normalize([string]$p) {
    # {Server.UrlEncode(personaId)} -> * ; всё после "{" считаем подстановкой
    $q = $p
    $brace = $q.IndexOf('{')
    if ($brace -ge 0) { $q = $q.Substring(0, $brace) + '*' }
    $q = $q -replace '\.\*$', '/*'
    return $q.ToLowerInvariant()
}

$client = @{}
foreach ($p in $raw) {
    $n = Normalize $p
    if ($n.Length -eq 0) { continue }
    if ($n -match '^description$') { continue }
    $client[$n] = $true
}

# ------------------------------------------------------------------ из кода

$routerFile = Join-Path $root 'src\Dpt.NfsWorld.Server\Engine\EngineRouter.cs'
$router = [IO.File]::ReadAllText($routerFile, [Text.Encoding]::UTF8)
$codeLiterals = [regex]::Matches($router, '"(/[A-Za-z0-9_/\.\-]*)"') |
    ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() } |
    Sort-Object -Unique

function In-Code([string]$path) {
    foreach ($lit in $codeLiterals) {
        if ($lit.Length -le 1) { continue }
        $bare = $lit.TrimStart('/')
        # точный маршрут
        if ($path -eq $bare) { return $true }
        # семейство: /performanceupgrades + /performanceupgrades/*
        if ($path -eq ($bare + '/*')) { return $true }
        if ($path.StartsWith($bare + '/')) { return $true }
        # явный префикс: lower.StartsWith("/personas/") или "/crypto/relaycryptoticket"
        if ($lit.EndsWith('/') -and $path.StartsWith($bare)) { return $true }
        if ($bare.Contains('/') -and $path.StartsWith($bare)) { return $true }
    }
    return $false
}

# ---------------------------------------------------------------- из данных

$profilesDir = Join-Path $root 'data\profiles'
$defaultsDir = Join-Path $root 'data\defaults'
$targets = @()
if ($Profile) { $targets = @(Join-Path $profilesDir $Profile) }
else { $targets = @(Get-ChildItem -LiteralPath $profilesDir -Directory | Select-Object -ExpandProperty FullName) }

$dataIndex = @{}
foreach ($dir in $targets) {
    $svc = Join-Path $dir 'Engine.svc'
    if (-not (Test-Path -LiteralPath $svc)) { continue }
    Get-ChildItem -LiteralPath $svc -Recurse -File -Filter *.xml | ForEach-Object {
        $rel = $_.FullName.Substring($svc.Length + 1) -replace '\\', '/'
        $rel = $rel -replace '\.xml$', ''
        $dataIndex[$rel.ToLowerInvariant()] = $true
    }
}
if (Test-Path -LiteralPath $defaultsDir) {
    $d = Join-Path $defaultsDir 'Engine.svc'
    if (Test-Path -LiteralPath $d) {
        Get-ChildItem -LiteralPath $d -Recurse -File -Filter *.xml | ForEach-Object {
            $rel = $_.FullName.Substring($d.Length + 1) -replace '\\', '/'
            $rel = $rel -replace '\.xml$', ''
            $dataIndex[$rel.ToLowerInvariant()] = $true
        }
    }
}

function In-Data([string]$path) {
    if ($dataIndex.ContainsKey($path)) { return $true }
    # personas/<id>/carslots и подобное: сравниваем форму с * на месте id
    foreach ($key in $dataIndex.Keys) {
        if ($key -notmatch '\*') { continue }
        $pattern = '^' + ([regex]::Escape($key) -replace '\\\*', '[^/]+') + '$'
        if ($path -match $pattern) { return $true }
    }
    # путь с числовым сегментом: personas/100/carslots -> сравниваем с personas/*/carslots
    $segments = $path -split '/'
    $forms = @()
    for ($i = 0; $i -lt $segments.Count; $i++) {
        if ($segments[$i] -match '^\d+$' -or $segments[$i] -eq '*') {
            $copy = $segments.Clone()
            $copy[$i] = '*'
            $forms += ($copy -join '/')
        }
    }
    foreach ($f in $forms) { if ($dataIndex.ContainsKey($f)) { return $true } }
    return $false
}

# -------------------------------------------------------------------- отчёт

$coveredCode = @()
$coveredData = @()
$missing = @()

foreach ($p in ($client.Keys | Sort-Object)) {
    if (In-Code $p) { $coveredCode += $p }
    elseif (In-Data $p) { $coveredData += $p }
    else { $missing += $p }
}

Write-Host ''
Write-Host ("Engine.svc endpoints in the client : " + $client.Count) -ForegroundColor Cyan
Write-Host ("  answered by server code          : " + $coveredCode.Count) -ForegroundColor Green
Write-Host ("  answered from profile data       : " + $coveredData.Count) -ForegroundColor Green
Write-Host ("  no answer at all                 : " + $missing.Count) -ForegroundColor $(if ($missing.Count -gt 0) { 'Yellow' } else { 'Green' })
Write-Host ''

if ($missing.Count -gt 0) {
    Write-Host 'Not answered by the server:' -ForegroundColor Yellow
    foreach ($m in $missing) { Write-Host ("  - /Engine.svc/" + $m) }
    Write-Host ''
}

if ($VerbosePreference -ne 'SilentlyContinue') {
    Write-Host 'From profile data:' -ForegroundColor DarkGray
    foreach ($d in $coveredData) { Write-Host ("  - " + $d) -ForegroundColor DarkGray }
}
exit 0