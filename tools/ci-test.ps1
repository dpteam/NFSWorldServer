# =========================================================================
#  DPT.Server - one shot CI: build, start, exercise every listener, stop.
#  Exit code 0 means everything is green. Local use is identical to CI.
#
#    powershell -ExecutionPolicy Bypass -File tools\ci-test.ps1
#    ... -SkipBuild        reuse bin\NFSWorldServer.exe
#    ... -KeepRunning      do not stop the server at the end
# =========================================================================
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$KeepRunning,
    [string]$EnginePort = '44600',
    [string]$PublicPort = '44601',
    [string]$AdminPort = '44602',
    [string]$XmppPort = '44603',
    [string]$RelayPort = '44610'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root 'bin'
$exe = Join-Path $bin 'NFSWorldServer.exe'
$logTail = $null
$server = $null
$failed = 0

function Step([string]$name) {
    Write-Host ''
    Write-Host ('>>> ' + $name) -ForegroundColor Cyan
}

function Get-AdminToken {
    $ini = Join-Path $bin 'config.ini'
    if (-not (Test-Path $ini)) { return '' }
    $m = Select-String -LiteralPath $ini -Pattern '^adminToken=(.+)$' | Select-Object -First 1
    if ($m) { return $m.Matches[0].Groups[1].Value.Trim() }
    return ''
}

function Wait-Health([string]$url, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 2
            if ($r.StatusCode -eq 200) { return $true }
        } catch { }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

try {
    Step 'stop leftovers'
    Get-Process NFSWorldServer -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host ('  stopping pid ' + $_.Id)
        $_ | Stop-Process -Force
    }
    Start-Sleep -Milliseconds 800

    if (-not $SkipBuild) {
        Step 'build'
        & (Join-Path $root 'build.cmd')
        if ($LASTEXITCODE -ne 0) { throw 'build failed' }
    } else {
        if (-not (Test-Path $exe)) { throw 'bin\NFSWorldServer.exe is missing, build first' }
    }

    Step 'start server (headless)'
    $args = @('--headless', '--engine-port', $EnginePort, '--public-port', $PublicPort,
              '--admin-port', $AdminPort, '--xmpp-port', $XmppPort, '--relay-port', $RelayPort)
    $server = Start-Process -FilePath $exe -WorkingDirectory $bin -ArgumentList $args -PassThru `
                            -RedirectStandardOutput (Join-Path $bin 'ci-out.txt') `
                            -RedirectStandardError (Join-Path $bin 'ci-err.txt')
    Write-Host ('  pid ' + $server.Id)

    if (-not (Wait-Health ("http://127.0.0.1:$PublicPort/api/health") 25)) {
        throw 'server did not answer /api/health within 25s'
    }
    Write-Host '  server is up' -ForegroundColor Green

    $envs = @{
        Engine = "http://127.0.0.1:$EnginePort"
        Web    = "http://127.0.0.1:$PublicPort"
        Admin  = "http://127.0.0.1:$AdminPort"
        Relay  = [int]$RelayPort
        Xmpp   = [int]$XmppPort
        Token  = (Get-AdminToken)
    }

    Step 'engine + web API tests'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\test-engine.ps1') `
        -Base $envs.Engine -Web $envs.Web -Admin $envs.Admin -Token $envs.Token
    if ($LASTEXITCODE -ne 0) { $failed++ }

    Step 'protocol contracts (client DTO vs our templates)'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\contract-check.ps1')
    if ($LASTEXITCODE -ne 0) { $failed++ }

    Step 'relay tests'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\test-relay.ps1') `
        -Port $envs.Relay -Admin $envs.Admin -Token $envs.Token
    if ($LASTEXITCODE -ne 0) { $failed++ }

    Step 'xmpp tests'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\test-xmpp.ps1') `
        -Port $envs.Xmpp
    if ($LASTEXITCODE -ne 0) { $failed++ }
}
catch {
    Write-Host ''
    Write-Host ('CI ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    $failed++
}
finally {
    if ($server -and -not $KeepRunning) {
        Step 'stop server'
        try { $server | Stop-Process -Force } catch { }
        Start-Sleep -Milliseconds 600
    }

    $logDir = Join-Path $bin 'logs'
    if (Test-Path $logDir) {
        $latest = Get-ChildItem $logDir -Filter 'nfsw-*.log' | Sort-Object LastWriteTime | Select-Object -Last 1
        if ($latest) {
            $logTail = Join-Path $bin 'ci-server-tail.log'
            Get-Content $latest.FullName -Tail 80 | Set-Content $logTail -Encoding UTF8
            if ($failed -gt 0) {
                Write-Host ''
                Write-Host ('--- last 80 log lines (' + $logTail + ') ---') -ForegroundColor Yellow
                Get-Content $latest.FullName -Tail 80 | ForEach-Object { Write-Host $_ }
            }
        }
    }
}

Write-Host ''
if ($failed -eq 0) {
    Write-Host 'CI RESULT: all green' -ForegroundColor Green
    exit 0
}
Write-Host ('CI RESULT: ' + $failed + ' suite(s) failed') -ForegroundColor Red
exit 1