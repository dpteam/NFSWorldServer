# =========================================================================
#  DPT.Server - UDP relay (NAT hairpin) test
#
#  Registers two clients in the same room, forwards a datagram each way,
#  then checks the admin API sees the room and flags the shared public IP
#  as "hairpin" (both clients are 127.0.0.1 here, which is exactly the case
#  the relay exists for).
#
#    powershell -ExecutionPolicy Bypass -File tools\test-relay.ps1
#    ... -Host 192.168.1.10 -Port 44610 -Admin http://127.0.0.1:44602
# =========================================================================
[CmdletBinding()]
param(
    [string]$HostName = '127.0.0.1',
    [int]$Port = 44610,
    [string]$Admin = 'http://127.0.0.1:44602',
    [string]$Token = '',
    [string]$Room = ''
)

$ErrorActionPreference = 'Stop'
$script:pass = 0
$script:fail = 0

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  [ok]   " + $name) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  [FAIL] " + $name + $(if ($detail) { " -> $detail" } else { '' })) -ForegroundColor Red }
}

function Packet([byte]$type, [byte[]]$body) {
    $list = New-Object 'System.Collections.Generic.List[byte]'
    $list.AddRange([byte[]](0x4E, 0x46, 0x53, 0x52, $type))
    if ($body) { $list.AddRange($body) }
    return $list.ToArray()
}

function Register([System.Net.Sockets.UdpClient]$client, [string]$persona, [string]$room) {
    $body = [Text.Encoding]::UTF8.GetBytes($persona + '|' + $room)
    $pkt = Packet 1 $body
    [void]$client.Send($pkt, $pkt.Length, $HostName, $Port)
    $client.Client.ReceiveTimeout = 3000
    $ep = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
    $resp = $client.Receive([ref]$ep)
    return $resp
}

function Recv([System.Net.Sockets.UdpClient]$client, [int]$timeoutMs) {
    try {
        $client.Client.ReceiveTimeout = $timeoutMs
        $ep = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
        return $client.Receive([ref]$ep)
    } catch {
        return $null
    }
}

if (-not $Token) {
    $cfg = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\config.ini'
    if (Test-Path $cfg) {
        $m = Select-String -LiteralPath $cfg -Pattern '^adminToken=(.+)$' | Select-Object -First 1
        if ($m) { $Token = $m.Matches[0].Groups[1].Value.Trim() }
    }
}
if (-not $Room) { $Room = 'probe-' + (Get-Random -Minimum 1000 -Maximum 9999) }

$personaA = '900' + (Get-Random -Minimum 100 -Maximum 999)
$personaB = '901' + (Get-Random -Minimum 100 -Maximum 999)

Write-Host ''
Write-Host '=== relay: registration ===' -ForegroundColor Cyan
$a = New-Object System.Net.Sockets.UdpClient(0)
$b = New-Object System.Net.Sockets.UdpClient(0)
try {
    $ackA = Register $a $personaA $Room
    Check 'client A registered (ACK)' ($ackA.Length -ge 5 -and $ackA[4] -eq 0x81) ("len=" + $ackA.Length)
    Check 'ACK carries the room name' ([Text.Encoding]::UTF8.GetString($ackA, 5, $ackA.Length - 5) -eq $Room)

    $ackB = Register $b $personaB $Room
    Check 'client B registered (ACK)' ($ackB.Length -ge 5 -and $ackB[4] -eq 0x81)

    Write-Host ''
    Write-Host '=== relay: forwarding ===' -ForegroundColor Cyan
    $payloadA = [Text.Encoding]::ASCII.GetBytes('hello-from-A-' + (Get-Random))
    $pkt = Packet 2 $payloadA
    [void]$a.Send($pkt, $pkt.Length, $HostName, $Port)
    $gotB = Recv $b 3000
    Check 'A -> B datagram delivered' ([bool]$gotB)
    if ($gotB) {
        Check 'B sees DATA opcode' ($gotB[4] -eq 0x82)
        $body = New-Object byte[] ($gotB.Length - 5)
        [Array]::Copy($gotB, 5, $body, 0, $body.Length)
        Check 'B payload is intact' ([Text.Encoding]::ASCII.GetString($body) -eq [Text.Encoding]::ASCII.GetString($payloadA))
    }
    $selfEcho = Recv $a 400
    Check 'A does not receive its own datagram' (-not [bool]$selfEcho)

    $payloadB = [Text.Encoding]::ASCII.GetBytes('hello-from-B-' + (Get-Random))
    $pkt = Packet 2 $payloadB
    [void]$b.Send($pkt, $pkt.Length, $HostName, $Port)
    $gotA = Recv $a 3000
    Check 'B -> A datagram delivered' ([bool]$gotA)
    if ($gotA) {
        $body = New-Object byte[] ($gotA.Length - 5)
        [Array]::Copy($gotA, 5, $body, 0, $body.Length)
        Check 'A payload is intact' ([Text.Encoding]::ASCII.GetString($body) -eq [Text.Encoding]::ASCII.GetString($payloadB))
    }

    Write-Host ''
    Write-Host '=== relay: headerless game traffic ===' -ForegroundColor Cyan
    # Once registered, a client may send bare game datagrams (no NFSR header)
    $bare = [Text.Encoding]::ASCII.GetBytes('raw-game-bytes-' + (Get-Random))
    [void]$a.Send($bare, $bare.Length, $HostName, $Port)
    $gotB2 = Recv $b 3000
    Check 'registered client may send bare datagrams' ([bool]$gotB2 -and $gotB2[4] -eq 0x82)

    Write-Host ''
    Write-Host '=== relay: ping + leave ===' -ForegroundColor Cyan
    $pkt = Packet 5 $null
    [void]$a.Send($pkt, $pkt.Length, $HostName, $Port)
    $pong = Recv $a 2000
    Check 'PING answered with PONG' ([bool]$pong -and $pong[4] -eq 0x85)

    $pkt = Packet 4 $null
    [void]$a.Send($pkt, $pkt.Length, $HostName, $Port)
    Start-Sleep -Milliseconds 300

    Write-Host ''
    Write-Host '=== relay: admin API view ===' -ForegroundColor Cyan
    if (-not $Token) {
        Check 'admin token available for the API view' $false 'run the server once to generate config.ini'
    } else {
        $json = Invoke-WebRequest -UseBasicParsing -Uri "$Admin/api/admin/relay" -Headers @{ 'X-Admin-Token' = $Token } -TimeoutSec 10 |
                ForEach-Object { [Text.Encoding]::UTF8.GetString($_.RawContentStream.ToArray()) }
        Check 'admin relay API lists the room' ($json -match [regex]::Escape($Room)) $json
        Check 'admin relay API marks the hairpin' ($json -match '"hairpin":true')
        Check 'admin relay API shows endpoints' ($json -match '"endpoint":"127.0.0.1:')
    }
} finally {
    $a.Close()
    $b.Close()
}

Write-Host ''
Write-Host ('RESULT: {0} passed, {1} failed' -f $script:pass, $script:fail) -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
if ($script:fail -gt 0) { exit 1 }
exit 0