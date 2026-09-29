<#
.SYNOPSIS
    Boots the agent on a throwaway port and data directory, probes it, and tears it down.

.DESCRIPTION
    This is the fastest way to prove a change did not break the HTTP surface. It is
    deliberately external to the test project: it exercises the *built, published* single
    file, which is the artefact that actually ships, rather than an in-memory test host.

    Usage:
        pwsh -File tests/smoke-test.ps1
        pwsh -File tests/smoke-test.ps1 -Port 48999 -KeepRunning
#>
[CmdletBinding()]
param(
    [int]    $Port = 48999,
    [string] $Configuration = 'Release',
    [switch] $KeepRunning,
    [switch] $SkipDiscovery
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot   = Split-Path -Parent $PSScriptRoot
$exe        = Join-Path $repoRoot "src\ESI.RemotePc.Agent.Service\bin\$Configuration\net10.0-windows\win-x64\ESI.RemotePc.Agent.Service.exe"
$dataDir    = Join-Path ([System.IO.Path]::GetTempPath()) ("esi-smoke-" + [guid]::NewGuid().ToString('n').Substring(0, 8))
$curl       = "$env:SystemRoot\System32\curl.exe"

if (-not (Test-Path $exe)) {
    throw "The agent has not been built. Expected: $exe"
}

New-Item -ItemType Directory -Path $dataDir -Force | Out-Null

# The agent throttles the unauthenticated surface at 30 requests a minute with a burst of
# 10, which is correct for a machine on a real network and far too tight for a diagnostic
# that deliberately probes that surface a dozen times in a few seconds. Raising the budget
# here is the point of the flag: it proves the throttle exists, and the throttling itself has
# its own unit tests.
$settingsPath = Join-Path $dataDir 'settings.json'
$json = @'
{
  "security": {
    "unauthenticatedPerMinute": 100000,
    "unauthenticatedBurst": 100000,
    "pairingPerHour": 1000
  }
}
'@
[System.IO.File]::WriteAllText($settingsPath, $json)

# ---------------------------------------------------------------------------
# Result tracking
# ---------------------------------------------------------------------------
$script:Pass = 0
$script:Fail = 0
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Invoke-Probe {
    param(
        [Parameter(Mandatory)] [string]   $Name,
        [Parameter(Mandatory)] [string]   $Method,
        [Parameter(Mandatory)] [string]   $Path,
        [string]   $Body,
        [string[]] $Headers = @(),
        [int]      $ExpectStatus = 200,
        # A regular expression matched against the body. Omit to assert only on status.
        [string]   $ExpectBody,
        # A substring that must NOT appear. The negative assertions are often the
        # security-relevant ones: "the response is fine" is much weaker than "the
        # response contains no credentials".
        [string]   $ExpectNotContains
    )

    $args = @('-sS', '-k', '-i', '-X', $Method, "https://127.0.0.1:$Port$Path")
    foreach ($h in $Headers) { $args += @('-H', $h) }
    if ($Body) {
        # Sent from a temp file: PowerShell's native argument handling mangles embedded
        # quotes in an inline -d string, which silently turns valid JSON into invalid JSON
        # and makes a working endpoint look broken.
        $file = Join-Path $dataDir 'probe-body.json'
        [System.IO.File]::WriteAllText($file, $Body)
        $args += @('--data-binary', "@$file")
    }

    $raw = & $curl @args 2>&1 | Out-String

    $status = 0
    if ($raw -match 'HTTP/1\.[01] (\d{3})') { $status = [int]$Matches[1] }

    # The body is everything after the blank line that separates headers from content.
    $index = $raw.IndexOf("`r`n`r`n")
    $bodyText = if ($index -ge 0) { $raw.Substring($index + 4) } else { '' }

    $ok = $status -eq $ExpectStatus
    $why = if ($ok) { '' } else { "expected HTTP $ExpectStatus, got $status" }

    if ($ok -and $ExpectBody) {
        if ($bodyText -notmatch $ExpectBody) {
            $ok = $false
            $why = "body did not match /$ExpectBody/"
        }
    }

    if ($ok -and $ExpectNotContains) {
        if ($bodyText.Contains($ExpectNotContains)) {
            $ok = $false
            $why = "body should not have contained '$ExpectNotContains'"
        }
    }

    if ($ok) {
        $script:Pass++
        Write-Host ("  [ ok ] {0}" -f $Name) -ForegroundColor DarkGreen
    }
    else {
        $script:Fail++
        $script:Failures.Add("$Name - $why")
        Write-Host ("  [FAIL] {0} - {1}" -f $Name, $why) -ForegroundColor Red
        if ($bodyText.Length -gt 0) {
            Write-Host ("         body: " + $bodyText.Substring(0, [Math]::Min(400, $bodyText.Length))) -ForegroundColor DarkYellow
        }
    }

    return $bodyText
}

# ---------------------------------------------------------------------------
# Boot
# ---------------------------------------------------------------------------
Write-Host "`nStarting the agent on 127.0.0.1:$Port (data dir $dataDir)`n" -ForegroundColor Cyan

$process = Start-Process -FilePath $exe -ArgumentList @('--port', "$Port", '--data-dir', "`"$dataDir`"") `
                          -WindowStyle Hidden -PassThru

$deadline = (Get-Date).AddSeconds(45)
$ready = $false

# curl writes its "connection refused" to stderr, and under $ErrorActionPreference = 'Stop'
# PowerShell turns that into a terminating error before the loop can retry. The failed
# connection is the normal state while the agent is still binding its socket.
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'

try {
    while ((Get-Date) -lt $deadline) {
        if ($process.HasExited) {
            throw "The agent exited during startup with code $($process.ExitCode). See $dataDir\logs."
        }

        & $curl -sS -k --max-time 2 "https://127.0.0.1:$Port/api/v1/health" *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }

        Start-Sleep -Milliseconds 750
    }
}
finally {
    $ErrorActionPreference = $previousPreference
}

if (-not $ready) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    throw "The agent did not answer /api/v1/health within 45s. See $dataDir\logs."
}

Write-Host "Agent is up.`n" -ForegroundColor Cyan

try {
    # =======================================================================
    # Public surface
    # =======================================================================
    Write-Host "Public surface" -ForegroundColor White

    Invoke-Probe -Name 'GET  /api/v1/handshake' -Method GET -Path '/api/v1/handshake' `
                 -ExpectBody '"deviceId"|"spkiSha256"|"protocolVersion"' | Out-Null

    Invoke-Probe -Name 'GET  /api/v1/health' -Method GET -Path '/api/v1/health' `
                 -ExpectBody '"status":"ok"' | Out-Null

    Invoke-Probe -Name 'GET  / (redirect to pairing)' -Method GET -Path '/' -ExpectStatus 302 | Out-Null

    Invoke-Probe -Name 'GET  /pairing (landing page)' -Method GET -Path '/pairing' `
                 -ExpectBody 'ESI' | Out-Null

    # =======================================================================
    # Pairing - the flow that used to fail with a body-less 400
    # =======================================================================
    Write-Host "`nPairing" -ForegroundColor White

    $pairBody = '{"deviceName":"Smoke Test","platform":"android","osVersion":"16","appVersion":"1.0.0"}'

    $created = Invoke-Probe -Name 'POST /api/v1/pair/request' -Method POST -Path '/api/v1/pair/request' `
                           -Body $pairBody -Headers @('Content-Type: application/json') `
                           -ExpectStatus 201 -ExpectBody '"userCode"|"pairingId"'

    $pairingId = $null
    $userCode = $null
    if ($created -match '"pairingId":"([^"]+)"') { $pairingId = $Matches[1] }
    if ($created -match '"userCode":"([^"]+)"') { $userCode = $Matches[1] }
    if (-not $pairingId) { throw 'pair/request did not return a pairingId; the rest of the flow cannot be tested.' }

    Invoke-Probe -Name 'GET  /api/v1/pair/session (pending)' -Method GET `
                 -Path "/api/v1/pair/session?id=$pairingId" -ExpectBody '"status":"pending"' | Out-Null

    # The QR is the only way the provisioning secret reaches the phone, and a pairing the
    # phone cannot complete is a product that does not work - so its absence is a failure
    # here even though nothing else in the flow complains.
    Invoke-Probe -Name 'GET  /pairing/qr.svg (for a pending request)' -Method GET `
                 -Path "/pairing/qr.svg?code=$userCode&host=127.0.0.1" `
                 -ExpectBody '<svg|viewBox' | Out-Null

    Invoke-Probe -Name 'GET  /pairing?code= (shows the QR inline)' -Method GET `
                 -Path "/pairing?code=$userCode" -ExpectBody 'qr.svg' | Out-Null

    Invoke-Probe -Name 'GET  /pairing/qr.svg (unknown code)' -Method GET `
                 -Path '/pairing/qr.svg?code=000000' -ExpectStatus 404 -ExpectBody 'esi_not_found' | Out-Null

    # A phone polling without a usable secret must get no credentials, whatever it guesses.
    # A wrong-length secret is a 400; a well-formed but wrong one is answered without the
    # secrets, which is the case that actually matters.
    Invoke-Probe -Name 'GET  /api/v1/pair/session (malformed secret)' -Method GET `
                 -Path "/api/v1/pair/session?id=$pairingId&secret=AAAA" -ExpectStatus 400 `
                 -ExpectBody 'esi_bad_request' | Out-Null

    Invoke-Probe -Name 'GET  /api/v1/pair/session (wrong secret)' -Method GET `
                 -Path "/api/v1/pair/session?id=$pairingId&secret=$('A' * 43)" `
                 -ExpectNotContains 'wrappedSecrets' | Out-Null

    Invoke-Probe -Name 'GET  /api/v1/pair/session (no secret)' -Method GET `
                 -Path "/api/v1/pair/session?id=$pairingId" `
                 -ExpectNotContains 'wrappedSecrets' | Out-Null

    # =======================================================================
    # Authentication is enforced
    # =======================================================================
    Write-Host "`nAuthentication" -ForegroundColor White

    Invoke-Probe -Name 'GET  /api/v1/system/metrics unsigned' -Method GET -Path '/api/v1/system/metrics' `
                 -ExpectStatus 401 -ExpectBody '"code":"esi_unauthorized"' | Out-Null

    Invoke-Probe -Name 'GET  /api/v1/system/info unsigned' -Method GET -Path '/api/v1/system/info' `
                 -ExpectStatus 401 -ExpectBody 'esi_unauthorized' | Out-Null

    Invoke-Probe -Name 'POST /api/v1/power/shutdown unsigned' -Method POST -Path '/api/v1/power/shutdown' `
                 -Body '{}' -Headers @('Content-Type: application/json') -ExpectStatus 401 | Out-Null

    # =======================================================================
    # Framework-generated errors still speak the protocol
    # =======================================================================
    Write-Host "`nError envelope" -ForegroundColor White

    Invoke-Probe -Name 'POST /api/v1/pair/request with malformed JSON' -Method POST `
                 -Path '/api/v1/pair/request' -Body '{ not json at all ' `
                 -Headers @('Content-Type: application/json') -ExpectStatus 400 `
                 -ExpectBody '"code"' | Out-Null

    Invoke-Probe -Name 'POST /api/v1/pair/request with no Content-Type' -Method POST `
                 -Path '/api/v1/pair/request' -Body $pairBody -ExpectStatus 415 `
                 -ExpectBody '"code"' | Out-Null

    Invoke-Probe -Name 'GET  /api/v1/does-not-exist' -Method GET -Path '/api/v1/does-not-exist' `
                 -ExpectStatus 404 -ExpectBody '"code"' | Out-Null

    # =======================================================================
    # Discovery
    # =======================================================================
    if (-not $SkipDiscovery) {
        Write-Host "`nDiscovery" -ForegroundColor White

        $udp = Join-Path $dataDir 'discover.ps1'
        @'
$udpClient = New-Object System.Net.Sockets.UdpClient
$udpClient.Client.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::Socket,
                                   [System.Net.Sockets.SocketOptionName]::Broadcast, $true)
$udpClient.Client.ReceiveTimeout = 3000
$probe = [System.Text.Encoding]::UTF8.GetBytes('ESI_DISCOVER_V1')
$null = $udpClient.Send($probe, $probe.Length, '255.255.255.255', 48211)
try {
    $remote = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
    $bytes = $udpClient.Receive([ref]$remote)
    [System.Text.Encoding]::UTF8.GetString($bytes)
} catch { 'NO-REPLY' }
'@ | Set-Content -Path $udp -Encoding utf8

        $reply = & powershell -NoProfile -ExecutionPolicy Bypass -File $udp 2>&1 | Out-String
        if ($reply -match 'deviceId|httpsPort') {
            Write-Host "  [ ok ] UDP broadcast discovery replied from the agent" -ForegroundColor DarkGreen
            $script:Pass++
        }
        else {
            Write-Host "  [FAIL] UDP broadcast discovery: $reply" -ForegroundColor Red
            $script:Fail++
            $script:Failures.Add("UDP discovery - no handshake reply ($($reply.Trim()))")
        }
    }
}
finally {
    if ($KeepRunning) {
        Write-Host "`nLeft running: pid $($process.Id), data dir $dataDir" -ForegroundColor Yellow
        Write-Host "Stop it with: Stop-Process -Id $($process.Id)" -ForegroundColor Yellow
    }
    else {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(5000) *> $null
        Remove-Item -Recurse -Force $dataDir -ErrorAction SilentlyContinue
    }
}

Write-Host ("`n{0} passed, {1} failed" -f $script:Pass, $script:Fail) -ForegroundColor $(if ($script:Fail) { 'Red' } else { 'Green' })

if ($script:Fail) {
    foreach ($f in $script:Failures) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}

exit 0
