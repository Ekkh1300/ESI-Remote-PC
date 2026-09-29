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
    [switch] $SkipPublish
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

function Invoke-Step {
    param([string] $Name, [scriptblock] $Action)

    Write-Host "==> $Name" -ForegroundColor Yellow
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
    Write-Host ("    done in {0:N1}s" -f $watch.Elapsed.TotalSeconds) -ForegroundColor DarkGray
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
    foreach ($project in @(
        'ESI.RemotePc.Agent.Service',
        'ESI.RemotePc.Session',
        'ESI.RemotePc.Agent.Cli'
    )) {
        Invoke-Step "publish $project" {
            & $dotnetExe publish (Join-Path $root "src\$project\$project.csproj") `
                -c $Configuration -o $publishDir -v minimal
        }
    }

    Write-Host ''
    Write-Host 'Artifacts' -ForegroundColor Cyan
    Get-ChildItem $publishDir -File |
        Sort-Object Name |
        ForEach-Object { '  {0,-42} {1,10:N1} MB' -f $_.Name, ($_.Length / 1MB) }

    Write-Host ''
    Write-Host "  publish directory: $publishDir"
    Write-Host '  To build the installer:  iscc /DAppVersion=1.0.0 installer\ESI-RemotePC.iss'
    Write-Host ''
}
