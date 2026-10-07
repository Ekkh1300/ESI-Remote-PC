<#
.SYNOPSIS
    Builds the Windows agent, the session agent, the CLI and the tests, then publishes the
    three shippable executables into artifacts\publish.

.DESCRIPTION
    One entry point, because the three executables have to be published from the same build
    or they will disagree about the protocol constants baked into them - and a service and a
    client that disagree on a wire constant fail at runtime, not at build time.

    Usage:
        pwsh -File build.ps1
        pwsh -File build.ps1 -Configuration Debug
        pwsh -File build.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipTests,
    [switch] $SkipPublish,

    # Runs the installer itself, through install, reinstall, uninstall and reinstall-again.
    # Needs administrator rights, so it is opt-in rather than part of the default build: a
    # build that silently requires elevation is a build people stop running.
    #
    # It is the only check in the repository that exercises the installer, and every one of
    # the defects it has found was invisible everywhere else - a service registered but
    # unstartable, a logon task that never fired, an uninstall that left 139 MB behind.
    [switch] $WithInstallCycle
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root       = $PSScriptRoot
$solution   = Join-Path $root 'ESI.RemotePc.Agent.slnx'
$artifacts  = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish'

# Resolve a dotnet that actually has a new enough SDK.
#
# Two things go wrong if this is done naively. A `dotnet` on PATH is often a stub - a Store
# alias, a shim left by a removed install - and it reports itself successfully while failing
# every subcommand. And on a machine with several SDKs installed side by side, taking the
# first one that answers will happily pick a .NET 8 SDK, which cannot even parse the .slnx
# solution format this repository uses. So: probe with --list-sdks, and take the newest.
function Resolve-Dotnet {
    $required = [version]'10.0.100'

    $candidates = [System.Collections.Generic.List[string]]::new()
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates.Add($onPath.Source) }
    $candidates.Add('C:\dotnet\dotnet.exe')
    $candidates.Add("$env:ProgramFiles\dotnet\dotnet.exe")
    $candidates.Add("$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")

    $best = $null
    $bestVersion = $null

    foreach ($candidate in $candidates) {
        if (-not (Test-Path $candidate)) { continue }

        try {
            $sdks = & $candidate --list-sdks 2>$null
            if ($LASTEXITCODE -ne 0 -or -not $sdks) { continue }

            $versions = @($sdks |
                ForEach-Object { if ($_ -match '^\s*(\d+\.\d+\.\d+)') { [version]$Matches[1] } } |
                Where-Object { $_ -and $_ -ge $required })

            if (-not $versions) { continue }

            $top = ($versions | Sort-Object -Descending)[0]

            if ($null -eq $bestVersion -or $top -gt $bestVersion) {
                $best = $candidate
                $bestVersion = $top
            }
        }
        catch {
            # Unusable candidate; try the next one.
        }
    }

    if ($null -eq $best) {
        throw ("No dotnet with SDK $($required.Major).0 or newer was found. Tried: " +
               ($candidates -join ', '))
    }

    return $best
}

$dotnetExe = Resolve-Dotnet

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Write-Host ''
Write-Host '  ESI Remote PC - Windows build' -ForegroundColor Cyan
Write-Host "  sdk         $($dotnetExe)"
Write-Host "  version     $(& $dotnetExe --version)"
Write-Host "  config      $Configuration"
Write-Host ''

# The version lives in two places that cannot see each other: the SDK needs it in
# Directory.Build.props to stamp the assembly the handshake reports, and Inno needs it as a
# #define to name the installer. Nothing stops them drifting apart, and a release cut with a
# setup.exe that says 1.0.1 next to an agent that reports 1.0.0 is exactly the sort of thing
# nobody notices until a support call. So they are compared before anything is built.
$productVersion = ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'Directory.Build.props') -Raw), '<Version>([^<]+)</Version>')).Groups[1].Value
$installerVersion = ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'installer\ESI-RemotePC.iss') -Raw), '#define\s+AppVersion\s+"([^"]+)"')).Groups[1].Value

if (-not $productVersion -or -not $installerVersion) {
    throw 'Could not read the product version from Directory.Build.props or the installer AppVersion.'
}
if ($productVersion -ne $installerVersion) {
    throw "Version mismatch: Directory.Build.props says $productVersion but the installer says $installerVersion. Fix one before cutting a release."
}


function Invoke-Step {
    param([string] $Name, [scriptblock] $Action)

    Write-Host "==> $Name" -ForegroundColor Yellow
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
    Write-Host ("    done in {0:N1}s" -f $watch.Elapsed.TotalSeconds) -ForegroundColor DarkGray
}

function Resolve-Iscc {
    # Inno's compiler is rarely on PATH, and an installation is not always where the
    # installer expects it - this machine has Inno Setup 7, and the repository predates it.
    $onPath = Get-Command 'iscc.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    foreach ($candidate in @(
        "${env:ProgramFiles}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe"
    )) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }

    return $null
}

Invoke-Step 'restore' { & $dotnetExe restore $solution }

Invoke-Step 'build' { & $dotnetExe build $solution -c $Configuration --no-restore -v minimal }

if (-not $SkipTests) {
    Invoke-Step 'test' {
        & $dotnetExe test (Join-Path $root 'tests\ESI.RemotePc.Tests\ESI.RemotePc.Tests.csproj') `
                         -c $Configuration --no-build --nologo -v minimal
    }

    Invoke-Step 'smoke test (boots the published agent)' {
        & powershell -NoProfile -ExecutionPolicy Bypass `
                     -File (Join-Path $root 'tests\smoke-test.ps1') -Configuration $Configuration
    }
}

if (-not $SkipPublish) {
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    # Each project declares its own RID and self-contained settings, so publish needs no
    # flags beyond the output path.
    #
    # PublishSingleFile is switched off here, which is the whole reason the installer is 43.8
    # MB rather than 129.7. A single-file bundle embeds its own LZMA-compressed copy of the
    # .NET runtime, so the installer cannot compress it any further and all three bundles
    # between them ship the runtime three times. A loose self-contained layout keeps the
    # runtime in the directory once, where Inno compresses it at install time. The three
    # entry points become 160 KB apphost launchers that need the DLLs beside them.
    #
    # The per-project PublishSingleFile in the csproj files is left alone: it is the right
    # default for running a single project from a build output, and the size argument only
    # applies when several of them ship in one installer.
    foreach ($project in @(
        'ESI.RemotePc.Agent.Service',
        'ESI.RemotePc.Session',
        'ESI.RemotePc.Agent.Cli'
    )) {
        Invoke-Step "publish $project" {
            & $dotnetExe publish (Join-Path $root "src\$project\$project.csproj") `
                -c $Configuration -o $publishDir -v minimal `
                -p:PublishSingleFile=false -p:EnableCompressionInSingleFile=false
        }
    }

    Write-Host ''
    Write-Host 'Artifacts' -ForegroundColor Cyan
    Get-ChildItem $publishDir -File -Filter '*.exe' |
        Sort-Object Name |
        ForEach-Object { '  {0,-42} {1,10:N1} MB' -f $_.Name, ($_.Length / 1MB) }

    # Into variables rather than inline: `$(Get-ChildItem ...).Count` inside a double-quoted
    # string captures only the command, so `.Count` becomes literal text and the whole file
    # list gets interpolated into the line.
    $published = Get-ChildItem $publishDir -Recurse -File
    $totalMb = ($published | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ('  {0,-42} {1,10:N1} MB' -f "($($published.Count) files, one shared layout)", $totalMb)

    Write-Host ''
    Write-Host "  publish directory: $publishDir"
    Write-Host '  To build the installer:  iscc /DAppVersion=1.0.0 installer\ESI-RemotePC.iss'
    Write-Host ''
}

if ($WithInstallCycle) {
    Invoke-Step 'installer cycle (install, reinstall, uninstall, reinstall)' {
        # Compile the installer here instead of trusting whatever is in artifacts. The
        # cycle is the only thing that ever tests the installer, and running it against a
        # binary left over from an earlier build reports twenty-six green checks on code
        # that is no longer in the tree - exactly the false confidence the test exists to
        # prevent. It happened: an install cycle passed against an installer built the
        # day before, while the code under review was never installed.
        $setup = Join-Path $artifacts 'ESI-RemotePC-Setup-1.0.0-x64.exe'
        $iscc  = Resolve-Iscc

        if (-not $iscc) {
            throw ("Inno Setup is not installed, so the installer under test would be stale. " +
                   "Install it from https://jrsoftware.org/isdl.php, or build $setup by hand " +
                   "and run installer\install-cycle-test.ps1 against it.")
        }

        Write-Host "    iscc: $iscc"
        & $iscc (Join-Path $root 'installer\ESI-RemotePC.iss')
        if ($LASTEXITCODE -ne 0) { throw "iscc failed with exit code $LASTEXITCODE" }

        if (-not (Test-Path $setup)) {
            throw "iscc reported success but there is no installer at $setup"
        }

        Write-Host "    installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB, built $((Get-Item $setup).LastWriteTime.ToString('HH:mm:ss')))"

        & (Join-Path $root 'installer\install-cycle-test.ps1') -Setup $setup
    }
}
