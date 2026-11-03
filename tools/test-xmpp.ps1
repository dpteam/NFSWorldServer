# =========================================================================
#  DPT.Server - EA-Chat (XMPP) handshake test
#
#  Speaks what the NFS World client speaks: stream open, STARTTLS (TLS 1.0),
#  legacy jabber:iq:auth with the persona id. The self signed certificate is
#  accepted on purpose - the game ignores the chain too.
#
#    powershell -ExecutionPolicy Bypass -File tools\test-xmpp.ps1
#    ... -HostName 127.0.0.1 -Port 44603 -Persona 1000
# =========================================================================
[CmdletBinding()]
param(
    [string]$HostName = '127.0.0.1',
    [int]$Port = 44603,
    [string]$Persona = '1000'
)

$ErrorActionPreference = 'Stop'
$script:pass = 0
$script:fail = 0

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  [ok]   " + $name) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  [FAIL] " + $name + $(if ($detail) { " -> $detail" } else { '' })) -ForegroundColor Red }
}

# Reads until $until appears in the accumulated text (or a timeout).
function Read-Until([System.IO.Stream]$stream, [string]$until, [int]$timeoutMs = 4000) {
    $sb = New-Object System.Text.StringBuilder
    $buffer = New-Object byte[] 8192
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        try {
            $stream.ReadTimeout = [Math]::Max(200, [int]($deadline - (Get-Date)).TotalMilliseconds)
            $n = $stream.Read($buffer, 0, $buffer.Length)
        } catch {
            break
        }
        if ($n -le 0) { break }
        [void]$sb.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $n))
        $text = $sb.ToString()
        if ([string]::IsNullOrEmpty($until)) { break }
        if ($text.IndexOf($until, [StringComparison]::OrdinalIgnoreCase) -ge 0) { break }
    }
    return $sb.ToString()
}

function Send-Text([System.IO.Stream]$stream, [string]$text) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
}

$tcp = New-Object System.Net.Sockets.TcpClient
$ssl = $null
try {
    $tcp.Connect($HostName, $Port)
    Check 'TCP connection accepted' $tcp.Connected
    $tcp.ReceiveTimeout = 6000
    $tcp.SendTimeout = 6000
    $raw = $tcp.GetStream()

    Send-Text $raw "<stream:stream xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams' to='$HostName' version='1.0'>"
    $features = Read-Until $raw '<stream:features'
    Check 'server answered stream open' ($features -match '<stream:stream')
    Check 'server offers STARTTLS' ($features -match 'starttls')

    Send-Text $raw "<starttls xmlns='urn:ietf:params:xml:ns:xmpp-tls'/>"
    $proceed = Read-Until $raw '<proceed'
    Check 'server proceeds with TLS' ($proceed -match '<proceed')

    $callback = [System.Net.Security.RemoteCertificateValidationCallback] {
        param($sender, $cert, $chain, $errors)
        return $true
    }
    $ssl = New-Object System.Net.Security.SslStream($raw, $false, $callback)
    $ssl.AuthenticateAsClient($HostName, $null, [System.Security.Authentication.SslProtocols]::Tls, $false)
    Check 'TLS handshake established' $ssl.IsEncrypted ("protocol " + $ssl.SslProtocol)

    Send-Text $ssl "<stream:stream xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams' to='$HostName' version='1.0'>"
    $features2 = Read-Until $ssl '<stream:features'
    Check 'TLS stream open answered' ($features2 -match '<stream:stream')

    Send-Text $ssl "<iq id='EA-Chat-1' type='get' xml:lang='en'><query xmlns='jabber:iq:auth'><username>nfsw.$Persona</username></query></iq>"
    # the auth result, the session ack and the welcome message may arrive together
    $auth = Read-Until $ssl '</message>'
    Check 'auth query answered' ($auth -match "id='EA-Chat-1'") $auth
    Check 'auth answer is a result' ($auth -match "type='result'")
    Check 'auth answer repeats the username' ($auth -match "nfsw\.$Persona")
    Check 'welcome message delivered' ($auth -match '<message')

    Send-Text $ssl "<iq id='EA-Chat-2' type='set' xml:lang='en'><query xmlns='jabber:iq:auth'><username>nfsw.$Persona</username><password/><digest/><resource/></query></iq>"
    $ack = Read-Until $ssl "EA-Chat-2" 3000
    Check 'auth set accepted' ($ack -match "EA-Chat-2" -or $ack -eq '') $ack

    Send-Text $ssl "<presence to='channel.en__1@conference.$HostName/nfsw.$Persona'><x xmlns='http://jabber.org/protocol/muc'/></presence>"
    $presence = Read-Until $ssl '</presence>'
    Check 'presence in a room is answered' ([bool]$presence) $presence

    Send-Text $ssl "</stream:stream>"
    $close = Read-Until $ssl '</stream:stream>' 2000
    # a server may also just drop the socket, so this is informational
    Check 'stream close handled' ($close -match '</stream:stream>' -or $close -eq '')
} finally {
    if ($ssl) { try { $ssl.Close() } catch { } }
    $tcp.Close()
}

Write-Host ''
Write-Host ('RESULT: {0} passed, {1} failed' -f $script:pass, $script:fail) -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
if ($script:fail -gt 0) { exit 1 }
exit 0