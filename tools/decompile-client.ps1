# =========================================================================
#  DPT.Server / NFS World - декомпиляция managed-части клиента
#
#  gameplay.dll - управляемая сборка (.NET). В ней весь контрактный слой:
#    server_packet/MyBinding.cs   - карта вызовов Engine.svc (пути, параметры)
#    Victory.TransferObjects.*    - DTO: имя поля = имя XML-элемента
#    xsd2cpp/XSD2CPP.cs           - порядок элементов и правила разбора
#
#  Реверс - источник правды (AGENTS.md). Дерево кладём в refs\_Src, а refs/
#  в .gitignore: чужие исходники в репозиторий не попадают, но каждый может
#  получить их одной командой и сверить наши шаблоны:
#      powershell -File tools\decompile-client.ps1
#      powershell -File tools\contract-check.ps1
#
#  usage:
#    powershell -File tools\decompile-client.ps1
#    ... -GameDir "D:\Games\NFW\Data"     где лежит gameplay.dll
#    ... -OutDir  "refs\_Src"             куда декомпилировать
#    ... -Force                           переделать, даже если дерево уже есть
# =========================================================================
[CmdletBinding()]
param(
    [string]$GameDir = 'E:\_DPT_Projects_E\_Games\DPT_NFSWorld\Data',
    [string]$OutDir = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'refs\_Src' }

$dll = Join-Path $GameDir 'gameplay.dll'
if (-not (Test-Path -LiteralPath $dll)) {
    Write-Host "[skip] gameplay.dll not found: $dll"
    Write-Host "       Pass the client folder with -GameDir (it must contain gameplay.dll)."
    exit 0
}

if ((Test-Path -LiteralPath (Join-Path $OutDir 'xsd2cpp\XSD2CPP.cs')) -and -not $Force) {
    Write-Host ("Already decompiled: " + $OutDir)
    Write-Host "Use -Force to redo it."
    exit 0
}

$env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
$ilspy = (Get-Command ilspycmd -ErrorAction SilentlyContinue).Source
if (-not $ilspy) {
    Write-Host 'ilspycmd not found, installing the global dotnet tool...'
    dotnet tool install --global ilspycmd 2>&1 | Select-Object -Last 3
    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
    $ilspy = (Get-Command ilspycmd -ErrorAction SilentlyContinue).Source
}
if (-not $ilspy) {
    Write-Host '[ERROR] ilspycmd is still unavailable. Is the .NET SDK installed?'
    exit 1
}

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
Write-Host ("Decompiling " + $dll)
Write-Host ("        -> " + $OutDir)
& $ilspy -p -o $OutDir $dll 2>&1 | Select-Object -Last 3

$count = (Get-ChildItem -LiteralPath $OutDir -Recurse -File -Filter *.cs -ErrorAction SilentlyContinue | Measure-Object).Count
if ($count -lt 100) {
    Write-Host ("[ERROR] only $count source files produced - decompilation failed")
    exit 1
}

Write-Host ''
Write-Host ("Done: $count .cs files.")
Write-Host 'Useful entry points:'
Write-Host '  server_packet\MyBinding.cs          все вызовы Engine.svc с параметрами'
Write-Host '  GameCore\Server.cs                  транспорт (P/Invoke в gameplay.native.dll)'
Write-Host '  Victory.TransferObjects.User\       контракты UserInfo/User/ProfileData'
Write-Host '  xsd2cpp\XSD2CPP.cs                  точный порядок элементов в XML'
Write-Host ''
Write-Host 'Now validate our templates against the client:'
Write-Host '  powershell -File tools\contract-check.ps1'
exit 0