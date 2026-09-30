# Proves the installer works, by running it.
#
#   powershell -ExecutionPolicy Bypass -File installer/install-cycle-test.ps1
#
# Needs elevation, because the thing being tested is an installer.
#
# The installer is the one artefact in this repository that no automated check ever ran.
# It is also the artefact a user meets first, and the one whose failures are silent: a
# service that is registered but stopped looks exactly like a working install if you only
# check that the service exists. So this exercises four states that a human would otherwise
# have to discover one at a time:
#
#   1. install on a clean machine          - does it work at all
#   2. install AGAIN over its own output   - the upgrade path, and the one that used to
#                                           leave a delete-pending service that could not
#                                           be started
#   3. uninstall                           - does it clean up, including the task
#   4. install again                       - and can it be recovered after an uninstall
#
# After every step it asks the question a user would ask - "is it working?" - by connecting
# to the agent's port, not by reading the SCM's opinion.

[CmdletBinding()]
param(
    [string] $Setup = '..\artifacts\ESI-RemotePC-Setup-1.0.0-x64.exe',
    [int]    $HttpsPort = 48220
)

$ErrorActionPreference = 'Stop'

$script:Failures = 0
$script:Checks = 0

function Test-Step {
    param([string] $Name, [scriptblock] $Body)

    Write-Host ''
    Write-Host "== $Name" -ForegroundColor Cyan

    try {
        & $Body
    }
    catch {
        Write-Host "   EXCEPTION: $_" -ForegroundColor Red
        $script:Failures++
    }
}

function Assert-That {
    param([string] $What, [bool] $Condition, [string] $Detail = '')

    $script:Checks++
    if ($Condition) {
        Write-Host "   ok    $What"
    }
    else {
        Write-Host "   FAIL  $What$(if ($Detail) { " - $Detail" })" -ForegroundColor Red
        $script:Failures++
    }
}

function Invoke-Setup {
    param([string[]] $Arguments)

    $full = (Resolve-Path (Join-Path $PSScriptRoot $Setup)).Path
    Write-Host "   running $([IO.Path]::GetFileName($full)) $($Arguments -join ' ')"
    $p = Start-Process -FilePath $full -ArgumentList $Arguments -PassThru -Wait
    Write-Host "   setup exit code: $($p.ExitCode)"
    return $p.ExitCode
}

function Uninstall-IfPresent {
    $uninstaller = Get-ChildItem "$env:ProgramFiles\ESI Remote PC\unins*.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($uninstaller) {
        Write-Host "   uninstalling via $($uninstaller.Name)"
        Start-Process -FilePath $uninstaller.FullName `
            -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
        Start-Sleep 4
    }
}

function Get-ServiceState {
    $output = & sc.exe query ESIRemotePcAgent 2>&1 | Out-String
    if ($output -match 'FAILED 1060') { return 'ABSENT' }
    if ($output -match 'RUNNING')      { return 'RUNNING' }
    if ($output -match 'STOPPED')      { return 'STOPPED' }
    if ($output -match 'START_PENDING'){ return 'START_PENDING' }
    return 'UNKNOWN'
}

function Test-AgentServing {
    param([string] $Label)

    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        try {
            $body = curl.exe -sS -k --max-time 5 "https://127.0.0.1:$HttpsPort/api/v1/health" 2>$null | Out-String
            if ($body -match '"status":"ok"') {
                Assert-That "$Label : the agent answers on https://127.0.0.1:$HttpsPort" $true
                return $true
            }
        }
        catch { }
        Start-Sleep -Milliseconds 700
    }
    Assert-That "$Label : the agent answers on https://127.0.0.1:$HttpsPort" $false `
        'nothing answered /api/v1/health within 45 s'
    return $false
}

function Test-AgentState {
    $state = Get-ServiceState
    Assert-That 'the agent service is RUNNING' ($state -eq 'RUNNING') "state was $state"
}

function Test-Registered {
    $registered = (Get-ServiceState) -ne 'ABSENT'
    Assert-That 'the agent service is registered' $registered
    return $registered
}

function Test-Description {
    $description = & sc.exe qdescription ESIRemotePcAgent 2>&1 | Out-String
    Assert-That 'the service has a description' ($description -match 'HTTPS API') `
        ($description -replace '\s+', ' ').Trim()
}

function Test-FailureActions {
    $actions = & sc.exe qfailure ESIRemotePcAgent 2>&1 | Out-String
    Assert-That 'the service restarts on failure' `
        (($actions -match 'RESTART') -and ($actions -match '60000')) `
        ($actions -replace '\s+', ' ').Trim()
}

function Test-Privileges {
    $value = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\ESIRemotePcAgent' `
                -Name requiredPrivileges -ErrorAction SilentlyContinue).requiredPrivileges
    Assert-That 'requiredPrivileges is set for the service host' `
        ($value -like '*SeTcbPrivilege*') "value was '$value'"
}

function Test-LogonTask {
    # schtasks reports "not found" on stderr, and with $ErrorActionPreference = 'Stop' a
    # native command writing to stderr aborts the script before the assertion runs. So the
    # query is asked for a format that fails quietly, and the answer is the exit code.
    $output = Query-Schtasks 'ESI Remote PC Session Agent'
    Assert-That 'the logon task for the session agent exists' `
        ($output.Found) $output.Detail
    if ($output.Found) {
        Assert-That 'the logon task points at the installed session agent' `
            ($output.Text -match [regex]::Escape("$env:ProgramFiles\ESI Remote PC\ESI.RemotePc.Session.exe")) `
            ($output.Text -replace '\s+', ' ').Trim()
    }
}

# Asks Task Scheduler about one task, without letting a "not found" on stderr abort anything.
function Query-Schtasks {
    param([string] $Name)

    # [Diagnostics.Process] rather than `& schtasks.exe 2>&1`.
    #
    # Under Windows PowerShell 5.1 with $ErrorActionPreference = 'Stop', a native command
    # that writes to stderr is a terminating error, so the "ERROR: cannot find the file"
    # that Task Scheduler prints for a task that is not there - which is the answer, not a
    # problem - aborts the whole script before the assertion can be made. Redirecting the
    # streams on the Process object keeps stderr as data.
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = "$env:SystemRoot\System32\schtasks.exe"
    # /v, because "Task To Run" is only in the verbose listing. Without it the check below
    # would compare against a document that does not mention the path at all.
    $psi.Arguments = "/query /tn `"$Name`" /fo LIST /v"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $process = [Diagnostics.Process]::Start($psi)
    $text = $process.StandardOutput.ReadToEnd()
    $errorText = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    [pscustomobject]@{
        # A task that is not there makes schtasks exit non-zero, so the exit code is the
        # answer. Nothing else is inferred from the text.
        Found  = ($process.ExitCode -eq 0)
        Text   = $text
        Detail = ((($text + ' ' + $errorText) -replace '\s+', ' ')).Trim()
    }
}

function Test-SessionAgentProcess {
    # Only meaningful when somebody is signed in: /it on the task means a headless install
    # correctly produces nothing here.
    $signedIn = (Get-Process -Name explorer -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0
    if (-not $signedIn) {
        Write-Host '   skip  nobody is signed in, so the session agent correctly is not running'
        return
    }
    Start-Sleep 3
    $running = Get-Process -Name 'ESI.RemotePc.Session' -ErrorAction SilentlyContinue
    Assert-That 'the session agent is running for the signed-in user' `
        ($null -ne $running) 'no ESI.RemotePc.Session.exe process, so there is no tray icon'
}

function Test-CleanedUp {
    Assert-That 'the install directory is gone' (-not (Test-Path "$env:ProgramFiles\ESI Remote PC"))
    Assert-That 'the data directory is gone' (-not (Test-Path "$env:ProgramData\ESI-RemotePC"))
    Assert-That 'the agent service is gone' ((Get-ServiceState) -eq 'ABSENT')
    $task = Query-Schtasks 'ESI Remote PC Session Agent'
    Assert-That 'the logon task is gone' (-not $task.Found) $task.Detail
}

# =========================================================================================

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This test has to run elevated: it is testing an installer.'
}

Write-Host 'Installer cycle test' -ForegroundColor White
Write-Host "  setup: $Setup"
Write-Host "  port : $HttpsPort"

Test-Step '1. a clean uninstall first, so step 2 is a genuine upgrade' {
    Uninstall-IfPresent
    Test-CleanedUp
}

Test-Step '2. install on a clean machine' {
    $code = Invoke-Setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    Assert-That 'the installer reported success' ($code -eq 0) "exit code $code"
    Test-Registered | Out-Null
    Test-AgentState
    Test-AgentServing 'install' | Out-Null
    Test-Description
    Test-FailureActions
    Test-Privileges
    Test-LogonTask
    Test-SessionAgentProcess
}

Test-Step '3. install AGAIN, over its own output (the upgrade path)' {
    # This is the case that used to fail. `sc stop` is asynchronous and `sc delete` on a
    # running service only marks the key, so an un-waited create failed 1073 silently and
    # start then failed 87, leaving a registered service that never ran.
    #
    # It is slower than it should be: the session helper from step 2 holds the agent's
    # service handle open, so the installer's wait for the key to disappear times out at
    # 30 s. That is ROADMAP D19, and it is why the timing below is a ceiling rather than a
    # target. The run still has to end with a working agent, which is the part that matters.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $code = Invoke-Setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    $elapsed = $sw.Elapsed.TotalSeconds

    Assert-That 'the installer reported success' ($code -eq 0) "exit code $code"
    Test-AgentState
    Test-AgentServing 'reinstall' | Out-Null
    Test-Description
    Test-FailureActions

    if ($elapsed -gt 90) {
        Write-Host "   note  the reinstall took $([math]::Round($elapsed))s; the session helper" -ForegroundColor Yellow
        Write-Host '         holds the service handle (ROADMAP D19), so the SCM wait times out.'
    }
}

Test-Step '4. uninstall' {
    Uninstall-IfPresent
    Test-CleanedUp
}

Test-Step '5. install again after an uninstall' {
    $code = Invoke-Setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    Assert-That 'the installer reported success' ($code -eq 0) "exit code $code"
    Test-AgentState
    Test-AgentServing 'reinstall-after-uninstall' | Out-Null
}

Write-Host ''
if ($script:Failures -eq 0) {
    Write-Host "passed: $script:Checks checks, 0 failed" -ForegroundColor Green
    exit 0
}

Write-Host "FAILED: $($script:Failures) of $script:Checks checks" -ForegroundColor Red
exit 1
