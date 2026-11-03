# =========================================================================
#  DPT.Server / NFS World - сверка наших XML-шаблонов с контрактами клиента
#
#  Реверс - источник правды (AGENTS.md). gameplay.dll управляемая, поэтому
#  порядок и состав полей берём прямо из неё, а не из головы. Скрипт читает
#  DTO рефлексией и сравнивает с нашими templates/*.xml.
#
#  usage:
#    powershell -File tools\contract-check.ps1
#    ... -GameDir "D:\Games\NFW\Data"      где лежит gameplay.dll
#    ... -Profile soapbox-1930             проверить один профиль
#
#  Код выхода: 0 - всё сходится, 1 - есть расхождения.
# =========================================================================
[CmdletBinding()]
param(
    [string]$GameDir = 'E:\_DPT_Projects_E\_Games\DPT_NFSWorld\Data',
    [string]$Profile = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $GameDir 'gameplay.dll'

if (-not (Test-Path -LiteralPath $dll)) {
    Write-Host "[skip] gameplay.dll not found: $dll"
    Write-Host "       Contract check needs a client build; on CI it is simply skipped."
    exit 0
}

# template root element -> контракт клиента (первый подошедший тип используется)
$contracts = @(
    @{ Template = 'ProfileData'; Types = @('Victory.TransferObjects.User.ProfileData', 'Victory.Service.Objects.ProfileData'); Family = 'soapbox' },
    @{ Template = 'UserInfo';    Types = @('Victory.TransferObjects.User.UserInfo');                                   Family = 'soapbox' },
    @{ Template = 'User';        Types = @('Victory.TransferObjects.User.User');                                       Family = 'soapbox' }
)

$asm = [Reflection.Assembly]::ReflectionOnlyLoadFrom($dll)
$allTypes = $asm.GetTypes()

function Get-FieldOrder([string]$elementName, [string]$xmlns, [string[]]$typeNames) {
    # 1) точное совпадение по namespace из xmlns (schemas.datacontract.org/2004/07/<NS>)
    if ($xmlns) {
        $m = [regex]::Match($xmlns, 'schemas\.datacontract\.org/\d{4}/\d{2}/(.+)$')
        if ($m.Success) {
            $ns = $m.Groups[1].Value.Replace('/', '.')
            $exact = $allTypes | Where-Object { $_.FullName -eq ($ns + '.' + $elementName) } | Select-Object -First 1
            if ($exact) {
                $fields = $exact.GetFields([Reflection.BindingFlags]'Public,NonPublic,Instance')
                return @{ Type = $exact.FullName; Fields = @($fields | ForEach-Object { $_.Name }) }
            }
        }
    }
    # 2) иначе - известные кандидаты, но только для шаблонов без namespace:
    #    если xmlns есть, а типа с таким namespace в этой сборке нет, это профиль
    #    другого клиента (2011/2010) и проверять его нечем.
    if ($xmlns) { return $null }
    foreach ($name in $typeNames) {
        $t = $allTypes | Where-Object { $_.FullName -eq $name } | Select-Object -First 1
        if ($t) {
            $fields = $t.GetFields([Reflection.BindingFlags]'Public,NonPublic,Instance')
            return @{ Type = $t.FullName; Fields = @($fields | ForEach-Object { $_.Name }) }
        }
    }
    return $null
}

function Get-TemplateInfo([string]$path) {
    $text = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
    $rootMatch = [regex]::Match($text, '<([A-Za-z0-9_]+)([^>]*)>')
    if (-not $rootMatch.Success) { return @{ Root = ''; Xmlns = ''; Tags = @() } }
    $rootName = $rootMatch.Groups[1].Value
    $attrs = $rootMatch.Groups[2].Value
    $xmlnsMatch = [regex]::Match($attrs, 'xmlns\s*=\s*"([^"]+)"')
    $xmlns = if ($xmlnsMatch.Success) { $xmlnsMatch.Groups[1].Value } else { '' }

    $inner = [regex]::Match($text, '<' + $rootName + '[^>]*>(.*)</' + $rootName + '>', 'Singleline').Groups[1].Value
    $depth = 0
    $tags = @()
    foreach ($m in [regex]::Matches($inner, '<(/?)([A-Za-z0-9_]+)([^>]*?)(/?)>')) {
        $closing = $m.Groups[1].Value -eq '/'
        $selfClosed = $m.Groups[4].Value -eq '/'
        if ($closing) { $depth--; continue }
        if ($depth -eq 0) { $tags += $m.Groups[2].Value }
        if (-not $selfClosed) { $depth++ }
    }
    return @{ Root = $rootName; Xmlns = $xmlns; Tags = $tags }
}

$profilesDir = Join-Path $root 'data\profiles'
$targets = @()
if ($Profile) {
    $targets = @(Join-Path $profilesDir $Profile)
}
else {
    $targets = Get-ChildItem -LiteralPath $profilesDir -Directory | Select-Object -ExpandProperty FullName
}

$problems = 0
$checked = 0

foreach ($dir in $targets) {
    $tplDir = Join-Path $dir 'templates'
    if (-not (Test-Path -LiteralPath $tplDir)) { continue }
    $id = Split-Path -Leaf $dir

    foreach ($c in $contracts) {
        $file = Join-Path $tplDir ($c.Template + '.xml')
        if (-not (Test-Path -LiteralPath $file)) { continue }

        $info = Get-TemplateInfo $file
        $order = Get-FieldOrder $info.Root $info.Xmlns $c.Types
        if (-not $order) {
            Write-Host ("[skip] {0}/{1}: contract {2} not found in gameplay.dll" -f $id, $c.Template, ($c.Types -join ' or '))
            continue
        }

        $tags = $info.Tags
        $checked++

        $extra = @($tags | Where-Object { $order.Fields -notcontains $_ })
        $missing = @($order.Fields | Where-Object { $tags -notcontains $_ -and $_ -ne 'personas' })
        $expected = @($order.Fields | Where-Object { $tags -contains $_ })
        $actual = @($tags | Where-Object { $order.Fields -contains $_ })
        $misordered = ($expected -join ',') -ne ($actual -join ',')

        if ($extra.Count -eq 0 -and -not $misordered) {
            Write-Host ("[ok]   {0}/{1} ({2} fields)" -f $id, $c.Template, $tags.Count) -ForegroundColor Green
            if ($missing.Count -gt 0) {
                Write-Host ("       optional, not sent: " + ($missing -join ', '))
            }
        }
        else {
            $problems++
            Write-Host ("[FAIL] {0}/{1}" -f $id, $c.Template) -ForegroundColor Red
            if ($extra.Count -gt 0) { Write-Host ("       not in the contract: " + ($extra -join ', ')) -ForegroundColor Red }
            if ($misordered) {
                Write-Host ("       order: " + ($actual -join ', '))
                Write-Host ("       client: " + ($expected -join ', '))
            }
        }
    }
}

Write-Host ''
if ($checked -eq 0) {
    Write-Host "No templates checked (no templates/ProfileData.xml etc.)."
    exit 1
}
Write-Host ("checked {0} template(s), {1} problem(s)" -f $checked, $problems)
exit $(if ($problems -eq 0) { 0 } else { 1 })