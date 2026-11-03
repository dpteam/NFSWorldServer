# =========================================================================
#  DPT.Server - engine + web API smoke test
#  Starts nothing: point it at a running server.
#
#    powershell -ExecutionPolicy Bypass -File tools\test-engine.ps1
#    ... -Base http://127.0.0.1:44600 -Web http://127.0.0.1:44601 -Admin http://127.0.0.1:44602
# =========================================================================
[CmdletBinding()]
param(
    [string]$Base = 'http://127.0.0.1:44600',
    [string]$Web = 'http://127.0.0.1:44601',
    [string]$Admin = 'http://127.0.0.1:44602',
    [string]$Token = ''
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$script:pass = 0
$script:fail = 0

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  [ok]   " + $name) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  [FAIL] " + $name + $(if ($detail) { " -> $detail" } else { '' })) -ForegroundColor Red }
}

function HttpText([string]$url, [string]$method = 'GET', [string]$body = '', [hashtable]$headers = @{}) {
    if ($body) {
        $r = Invoke-WebRequest -Uri $url -Method $method -UseBasicParsing -TimeoutSec 10 -Headers $headers `
             -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/xml'
    } else {
        $r = Invoke-WebRequest -Uri $url -Method $method -UseBasicParsing -TimeoutSec 10 -Headers $headers
    }
    return [Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray())
}

function HttpStatus([string]$url, [string]$method = 'GET', [string]$body = '', [hashtable]$headers = @{}) {
    try {
        if ($body) {
            $r = Invoke-WebRequest -Uri $url -Method $method -UseBasicParsing -TimeoutSec 10 -Headers $headers `
                 -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/xml'
        } else {
            $r = Invoke-WebRequest -Uri $url -Method $method -UseBasicParsing -TimeoutSec 10 -Headers $headers
        }
        return [int]$r.StatusCode
    } catch {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        return 0
    }
}

function StopServer([string]$baseUrl) {
    try {
        # the engine has no shutdown endpoint on purpose; the test just stops testing
        return $true
    } catch { return $false }
}

if (-not $Token) {
    $cfg = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\config.ini'
    if (Test-Path $cfg) {
        $m = Select-String -LiteralPath $cfg -Pattern '^adminToken=(.+)$' | Select-Object -First 1
        if ($m) { $Token = $m.Matches[0].Groups[1].Value.Trim() }
    }
}

$engine = $Base.TrimEnd('/') + '/Engine.svc'
$user = 'probe' + (Get-Random -Minimum 1000 -Maximum 9999)
$personaName = 'Probe' + (Get-Random -Minimum 100 -Maximum 999)

Write-Host ''
Write-Host '=== engine: health ===' -ForegroundColor Cyan
Check 'engine answers /heartbeat' ((HttpText "$engine/heartbeat") -match 'HeartBeat')

Write-Host ''
Write-Host '=== engine: account + session ===' -ForegroundColor Cyan
$authXml = "<AuthenticateUserRequest><userName>$user</userName><password>pw</password></AuthenticateUserRequest>"
$auth = HttpText "$engine/User/authenticateUser" 'POST' $authXml
Check 'authenticateUser returns LoginStatusVO' ($auth -match '<LoginStatusVO>')
$loginToken = [regex]::Match($auth, '<LoginToken>(.*?)</LoginToken>').Groups[1].Value
Check 'login token issued' ($loginToken.Length -ge 16)

$permXml = '<GetPermanentSessionRequest><machineID>PROBE-MACHINE</machineID></GetPermanentSessionRequest>'
$perm = HttpText "$engine/User/GetPermanentSession" 'POST' $permXml @{ securityToken = $loginToken }
Check 'GetPermanentSession returns UserInfo' ($perm -match '<UserInfo')
$sessionToken = [regex]::Match($perm, '<securityToken>(.*?)</securityToken>').Groups[1].Value
Check 'session securityToken issued' ($sessionToken.Length -ge 16)
Check 'userId is present' ($perm -match '<userId>\d+</userId>')

Write-Host ''
Write-Host '=== engine: persona ===' -ForegroundColor Cyan
$create = HttpText "$engine/DriverPersona/CreatePersona?name=$personaName&iconIndex=3" 'POST' '' @{ securityToken = $sessionToken }
Check 'CreatePersona returns ProfileData' ($create -match '<PersonaId>(\d+)</PersonaId>')
$personaId = [regex]::Match($create, '<PersonaId>(\d+)</PersonaId>').Groups[1].Value
Check 'persona id assigned' ($personaId.Length -gt 0) $personaId

Check 'SecureLoginPersona accepts' ((HttpStatus "$engine/User/SecureLoginPersona?personaId=$personaId" 'POST' '' @{ securityToken = $sessionToken }) -eq 200)
Check 'GetPersonaInfo returns the persona' ((HttpText "$engine/DriverPersona/GetPersonaInfo" 'GET' '' @{ securityToken = $sessionToken }) -match [regex]::Escape($personaName))

$slots = HttpText "$engine/personas/$personaId/carslots" 'GET' '' @{ securityToken = $sessionToken }
Check 'carslots lists the starting car' ($slots -match '<CarsOwnedByPersona>' -and $slots -match '<OwnedCarTrans')
Check 'starting car is a preset with a base car hash' ($slots -match '<BaseCar>-?\d+</BaseCar>')

$cars = HttpText "$engine/personas/$personaId/cars" 'GET' '' @{ securityToken = $sessionToken }
Check 'cars endpoint returns ArrayOfOwnedCarTrans' ($cars -match '<ArrayOfOwnedCarTrans>')
$carCountBefore = ([regex]::Matches($cars, '<OwnedCarTrans')).Count

$inv = HttpText "$engine/personas/$personaId/objects" 'GET' '' @{ securityToken = $sessionToken }
Check 'inventory has powerups' ($inv -match 'InventoryItemTrans' -and $inv -match 'nosshot')

Write-Host ''
Write-Host '=== engine: catalog + commerce ===' -ForegroundColor Cyan
$basket = HttpText "$engine/catalog/baskets/SRV-CAR1.xml" 'GET'
Check 'catalog basket is served from the profile' ($basket -match '<CustomCar>' -and $basket -match '<BaseCar>')

$buy = '<BasketTrans><BasketItems><ProductTrans><ProductId>SRV-CAR1</ProductId><Price>0.0</Price></ProductTrans></BasketItems></BasketTrans>'
$result = HttpText "$engine/personas/$personaId/baskets" 'POST' $buy @{ securityToken = $sessionToken }
Check 'purchase returns CommerceResultTrans' ($result -match '<CommerceResultTrans')
Check 'purchase reports Success' ($result -match '<Status>Success</Status>')
$cars2 = HttpText "$engine/personas/$personaId/cars" 'GET' '' @{ securityToken = $sessionToken }
$carCountAfter = ([regex]::Matches($cars2, '<OwnedCarTrans')).Count
Check 'purchase added a car' ($carCountAfter -eq ($carCountBefore + 1)) "before=$carCountBefore after=$carCountAfter"

Write-Host ''
Write-Host '=== engine: server information ===' -ForegroundColor Cyan
$info = HttpText "$engine/GetServerInformation" 'GET'
Check 'GetServerInformation is JSON' ($info -match '"xmppPort"')
Check 'GetServerInformation names a profile' ($info -match '"serverProfile"')
Check 'unknown endpoint is lenient (200)' ((HttpStatus "$engine/Unknown/ProbeEndpoint") -eq 200)

Write-Host ''
Write-Host '=== web: public board ===' -ForegroundColor Cyan
$status = HttpText "$Web/api/status" 'GET'
Check 'public /api/status' ($status -match '"ok":true')
Check 'public status hides private server block' (-not ($status -match '"workingSet"'))
$players = HttpText "$Web/api/players" 'GET'
Check 'public /api/players' ($players -match '"players"')
Check 'public players hide ip' (-not ($players -match '"ip":'))
Check 'public players hide token' (-not ($players -match '"token":'))
Check 'public players list the probe persona' ($players -match [regex]::Escape($personaName))
Check 'public board index.html' ((HttpText "$Web/") -match 'DPT.Server')
Check 'public board admin.html is reachable' ((HttpText "$Web/admin.html") -match 'Admin sign in')

Write-Host ''
Write-Host '=== web: admin board ===' -ForegroundColor Cyan
if (-not $Token) {
    Check 'admin token discovered in config.ini' $false 'run the server once to generate it'
} else {
    Check 'admin API rejects missing token' ((HttpStatus "$Admin/api/admin/accounts") -eq 401)
    Check 'admin API rejects a wrong token' ((HttpStatus "$Admin/api/admin/accounts" 'GET' '' @{ 'X-Admin-Token' = 'nope-nope-nope' }) -eq 401)
    Check 'admin API accepts the token' ((HttpStatus "$Admin/api/admin/accounts" 'GET' '' @{ 'X-Admin-Token' = $Token }) -eq 200)
    $accounts = HttpText "$Admin/api/admin/accounts" 'GET' '' @{ 'X-Admin-Token' = $Token }
    Check 'admin accounts include the probe account' ($accounts -match [regex]::Escape($user))
    $logs = HttpText "$Admin/api/admin/logs?limit=20" 'GET' '' @{ 'X-Admin-Token' = $Token }
    Check 'admin logs are readable' ($logs -match '"lines"')
    $kick = HttpText "$Admin/api/admin/kick" 'POST' ("{""personaId"":$personaId,""reason"":""probe""}") @{ 'X-Admin-Token' = $Token }
    Check 'admin kick works' ($kick -match '"ok":true')
    Check 'kicked session leaves the online list' (-not ((HttpText "$Web/api/players") -match [regex]::Escape($personaName)))
}

Write-Host ''
Write-Host ('RESULT: {0} passed, {1} failed' -f $script:pass, $script:fail) -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
if ($script:fail -gt 0) { exit 1 }
exit 0