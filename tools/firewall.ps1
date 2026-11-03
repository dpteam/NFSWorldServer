# =========================================================================
#  DPT.Server / NFS World - Windows Firewall setup
#
#  Opens the ports the server actually listens on (read from bin\config.ini):
#    * engine   TCP 44600  any source   (the game client connects from the internet)
#    * xmpp     TCP 44603  any source   (EA-Chat / lobby)
#    * relay    UDP 44610  any source   (NAT hairpin relay)
#    * public   TCP 44601  LAN by default (NGinx Proxy Manager sits next to us)
#    * admin    TCP 44602  LAN only
#
#  Rules are scoped to NFSWorldServer.exe when it exists, removed and recreated
#  on every run. Needs admin rights: the script relaunches itself with UAC.
#
#  usage (double click firewall.cmd, or):
#    powershell -ExecutionPolicy Bypass -File tools\firewall.ps1
#    ... -PublicAny       allow the public board from anywhere (direct expose)
#    ... -EngineLanOnly   game port only for LAN (offline/LAN party)
#    ... -Remove          delete the DPT.Server rules
#    ... -NoPause         do not wait for Enter at the end
# =========================================================================
[CmdletBinding()]
param(
    [switch]$PublicAny,
    [switch]$EngineLanOnly,
    [switch]$Remove,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'bin\NFSWorldServer.exe'
$ini = Join-Path $root 'bin\config.ini'
$reportPath = Join-Path $env:TEMP 'dpt-nfsw-firewall.log'
$report = New-Object 'System.Collections.Generic.List[string]'

function Add-Line([string]$text) {
    $report.Add($text)
    Write-Host $text
}

function Save-Report {
    try { Set-Content -Path $reportPath -Value $report -Encoding UTF8 } catch { }
}

# --------------------------------------------------------------- elevation

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath))
    if ($PublicAny) { $argList += '-PublicAny' }
    if ($EngineLanOnly) { $argList += '-EngineLanOnly' }
    if ($Remove) { $argList += '-Remove' }
    if ($NoPause) { $argList += '-NoPause' }

    Write-Host "Нужны права администратора - сейчас появится запрос UAC (нажмите «Да»)." -ForegroundColor Yellow
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList | Out-Null
    } catch {
        Write-Host "Повышение прав отменено: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "Запустите этот файл от имени администратора вручную." -ForegroundColor Red
    }
    Start-Sleep -Seconds 2
    exit
}

# ------------------------------------------------------------------- config

function Read-IniValues([string]$path) {
    $map = @{}
    if (-not (Test-Path $path)) { return $map }
    foreach ($line in Get-Content $path) {
        if ($line -match '^\s*([A-Za-z0-9_.]+)\s*=\s*(.*?)\s*$') {
            $map[$Matches[1].ToLowerInvariant()] = $Matches[2]
        }
    }
    return $map
}

$values = Read-IniValues $ini
$portEngine = 44600
$portPublic = 44601
$portAdmin  = 44602
$portXmpp   = 44603
$portRelay  = 44610
if ($values.ContainsKey('engine.port') -and $values['engine.port']) { $portEngine = [int]$values['engine.port'] }
if ($values.ContainsKey('web.publicport') -and $values['web.publicport']) { $portPublic = [int]$values['web.publicport'] }
if ($values.ContainsKey('web.adminport') -and $values['web.adminport']) { $portAdmin = [int]$values['web.adminport'] }
if ($values.ContainsKey('xmpp.port') -and $values['xmpp.port']) { $portXmpp = [int]$values['xmpp.port'] }
if ($values.ContainsKey('relay.port') -and $values['relay.port']) { $portRelay = [int]$values['relay.port'] }

# --------------------------------------------------------------- addresses

$lanRemote = @(
    'LocalSubnet',
    '127.0.0.1/32',
    '10.0.0.0/8',
    '172.16.0.0/12',
    '192.168.0.0/16',
    '169.254.0.0/16',
    '100.64.0.0/10'
)
$anyRemote = @('Any')

Add-Line ''
Add-Line '=== DPT.Server / NFS World - firewall setup ==='
Add-Line ('config : ' + $(if (Test-Path $ini) { $ini } else { 'not found, using defaults' }))
Add-Line ('exe    : ' + $(if (Test-Path $exe) { $exe } else { 'NOT FOUND (rules will be port-only)' }))
Add-Line ('ports  : engine=' + $portEngine + ' public=' + $portPublic + ' admin=' + $portAdmin +
          ' xmpp=' + $portXmpp + ' relay/udp=' + $portRelay)
Add-Line ''

$rules = @(
    @{
        Name    = 'DPT.Server Engine'
        Port    = $portEngine
        Proto   = 'TCP'
        Remote  = $(if ($EngineLanOnly) { $lanRemote } else { $anyRemote })
        Note    = 'NFS World game endpoint (Engine.svc)'
    },
    @{
        Name    = 'DPT.Server XMPP'
        Port    = $portXmpp
        Proto   = 'TCP'
        Remote  = $(if ($EngineLanOnly) { $lanRemote } else { $anyRemote })
        Note    = 'EA-Chat lobby (TLS)'
    },
    @{
        Name    = 'DPT.Server Relay'
        Port    = $portRelay
        Proto   = 'UDP'
        Remote  = $(if ($EngineLanOnly) { $lanRemote } else { $anyRemote })
        Note    = 'NAT hairpin UDP relay'
    },
    @{
        Name    = 'DPT.Server Public'
        Port    = $portPublic
        Proto   = 'TCP'
        Remote  = $(if ($PublicAny) { $anyRemote } else { $lanRemote })
        Note    = 'public web board'
    },
    @{
        Name    = 'DPT.Server Admin'
        Port    = $portAdmin
        Proto   = 'TCP'
        Remote  = $lanRemote
        Note    = 'admin web board (LAN only)'
    }
)

$haveCmdlets = $null -ne (Get-Command New-NetFirewallRule -ErrorAction SilentlyContinue)

function Remove-Rule([string]$name) {
    if ($haveCmdlets) {
        Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue |
            Remove-NetFirewallRule -ErrorAction SilentlyContinue
    } else {
        netsh advfirewall firewall delete rule name="$name" | Out-Null
    }
}

function Add-Rule($rule) {
    $name = $rule.Name
    $port = $rule.Port
    $proto = $rule.Proto
    $remote = $rule.Remote
    $note = $rule.Note

    Remove-Rule $name
    if ($Remove) { return }

    if ($haveCmdlets) {
        $params = @{
            DisplayName   = $name
            Description   = 'DPT.Server / NFS World - ' + $note
            Direction     = 'Inbound'
            Action        = 'Allow'
            Protocol      = $proto
            LocalPort     = $port
            RemoteAddress = $remote
            Profile       = 'Any'
            Enabled       = 'True'
        }
        if (Test-Path $exe) { $params['Program'] = $exe }
        New-NetFirewallRule @params | Out-Null
    } else {
        $cmd = 'netsh advfirewall firewall add rule name="' + $name + '" dir=in action=allow protocol=' + $proto +
               ' localport=' + $port + ' profile=any enable=yes'
        if (Test-Path $exe) { $cmd += ' program="' + $exe + '"' }
        if ($remote -notcontains 'Any') { $cmd += ' remoteip=' + ($remote -join ',') }
        Invoke-Expression $cmd | Out-Null
    }
}

# ------------------------------------------------------------------- apply

if ($Remove) { Add-Line 'Removing DPT.Server firewall rules...' }
else { Add-Line 'Adding inbound rules...' }

foreach ($rule in $rules) {
    try {
        Add-Rule $rule
        if ($Remove) {
            Add-Line ('  removed : ' + $rule.Name)
        } else {
            $scope = if ($rule.Remote -contains 'Any') { 'any source' } else { 'LAN/private only' }
            Add-Line ('  ok      : ' + $rule.Name.PadRight(22) + ' ' + $rule.Proto + '/' + $rule.Port + '  (' + $scope + ')')
        }
    } catch {
        Add-Line ('  FAILED  : ' + $rule.Name + ' -> ' + $_.Exception.Message)
    }
}

# ------------------------------------------------------------- diagnostics

Add-Line ''
Add-Line '--- listening sockets ---'
try {
    $netstat = netstat -ano | Select-String 'LISTENING|LISTEN'
    foreach ($port in @($portEngine, $portPublic, $portAdmin, $portXmpp)) {
        $hit = $netstat | Where-Object { $_.Line -match (':' + $port + '\s') } | Select-Object -First 1
        if ($hit) { Add-Line ('  TCP ' + $port + ' -> ' + (($hit.Line -split '\s+')[2])) }
        else { Add-Line ('  TCP ' + $port + ' -> not listening (server stopped?)') }
    }
} catch {
    Add-Line ('  netstat failed: ' + $_.Exception.Message)
}

Add-Line ''
Add-Line '--- local IPv4 addresses (вот эти адреса открывайте в браузере с других ПК) ---'
$ips = @()
try {
    $ips = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
        Select-Object -ExpandProperty IPAddress
} catch {
    try {
        $ips = [System.Net.Dns]::GetHostAddresses([System.Net.Dns]::GetHostName()) |
            Where-Object { $_.AddressFamily -eq 'InterNetwork' } |
            ForEach-Object { $_.ToString() } |
            Where-Object { $_ -notlike '127.*' -and $_ -notlike '169.254.*' }
    } catch { }
}
foreach ($ip in $ips) {
    Add-Line ('  http://' + $ip + ':' + $portPublic + '/        <- публичная веб-морда')
    Add-Line ('  http://' + $ip + ':' + $portAdmin + '/        <- админка (токен в bin\config.ini)')
    Add-Line ('  http://' + $ip + ':' + $portEngine + '/Engine.svc   <- игровой endpoint (для nfsw.exe)')
}
if ($ips.Count -eq 0) { Add-Line '  (no LAN address found)' }

Add-Line ''
Add-Line '--- local health check ---'
foreach ($port in @($portPublic, $portAdmin)) {
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri ('http://127.0.0.1:' + $port + '/api/health') -TimeoutSec 5
        Add-Line ('  ' + $port + ' -> HTTP ' + [int]$r.StatusCode + ' ' + $r.Content)
    } catch {
        Add-Line ('  ' + $port + ' -> FAILED: ' + $_.Exception.Message)
    }
}

Add-Line ''
Add-Line '--- rules ---'
if ($haveCmdlets) {
    try {
        Get-NetFirewallRule -DisplayName 'DPT.Server*' -ErrorAction SilentlyContinue |
            ForEach-Object {
                $pf = $_ | Get-NetFirewallPortFilter
                $af = $_ | Get-NetFirewallAddressFilter
                Add-Line ('  ' + $_.DisplayName + ' | ' + $_.Enabled + ' | ' + $_.Direction + '/' + $_.Action +
                          ' | ' + $pf.Protocol + '/' + $pf.LocalPort + ' | remote: ' + ($af.RemoteAddress -join ','))
            }
    } catch {
        Add-Line ('  ' + $_.Exception.Message)
    }
}

Add-Line ''
Add-Line 'Дальше:'
Add-Line '  * В роутере пробросьте на LAN-IP этого ПК:'
Add-Line ('      TCP ' + $portEngine + '  (игра)   TCP ' + $portXmpp + '  (чат)   UDP ' + $portRelay + '  (hairpin relay)')
Add-Line ('      TCP ' + $portPublic + '  (веб-морда) - или просто отдайте NPM, если он уже смотрит наружу')
Add-Line ('  * Админку (' + $portAdmin + ') наружу НЕ пробрасывайте: она для локалки.')
Add-Line '  * Публичная морда в браузере другого ПК: http://<LAN-IP>:' + $portPublic + '/'
Add-Line ''
Add-Line ('Отчёт: ' + $reportPath)

Save-Report

if (-not $NoPause) {
    Write-Host ''
    Read-Host 'Готово. Enter - закрыть окно' | Out-Null
}