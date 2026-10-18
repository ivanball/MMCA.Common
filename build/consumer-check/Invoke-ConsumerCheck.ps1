<#
.SYNOPSIS
    Package-mode consumer check: packs every MMCA.Common.slnx package into a local folder feed, then
    restores and builds a throwaway consumer (plus the layer-rule probes) against those nupkgs.

.DESCRIPTION
    The single implementation of the CI `package-consumption` job, runnable locally with the same
    behavior. Source-mode builds (local.props, the consumer-source-build job) bind ProjectReferences,
    so pack breaks (NU5xxx) and package-mode-only restore, analyzer and reference failures only show
    up here. Four stages, run in order by -Stage All (the local default); CI calls one stage per step
    so each keeps its own step name and log:

      Pack      dotnet pack MMCA.Common.slnx -c Release into -FeedDir, MinVer skipped with an explicit
                -Version (the same mechanism release.yml uses), so the consumer can pin it exactly.
      Scaffold  writes -ConsumerDir: a nuget.config mapping MMCA.Common.* to the local feed and
                everything else to nuget.org, a classlib referencing the meta set (API +
                Infrastructure + Testing.Architecture, which pulls the full package graph
                transitively) and a smoke type proving the references bind. -ConsumerDir sits
                outside the repo, so it does NOT inherit Directory.Build.props or
                Directory.Packages.props: it consumes the packages the way a downstream app does.
      Build     dotnet build Consumer.csproj -c Release in -ConsumerDir.
      Probes    MMCA.Common.Shared carries buildTransitive/MMCA.Common.Shared.targets (ADR-015 /
                ADR-058): a project named {App}.{Module}.{Layer} gets the layer rules with no opt-in.
                One positive probe must build and two negative probes must fail with MMCA0002 and
                MMCA0001 respectively, so a silently missing targets file cannot pass.

    Any failure exits 1. On GitHub Actions the failure line is also emitted as an ::error::
    annotation.

.PARAMETER Stage
    All (default), Pack, Scaffold, Build or Probes.

.PARAMETER Version
    The package version to pack and pin. Defaults to $env:PACK_VERSION (CI sets it per run), else to
    0.0.1-local.<UTC timestamp>, so a local re-run never restores a stale copy of an earlier run's
    packages from the global NuGet cache. A single stage other than All needs it passed or set, so
    that every stage agrees on the version.

.PARAMETER RepoRoot
    The MMCA.Common checkout to pack. Defaults to the repository containing this script.

.PARAMETER FeedDir
    The local folder feed the packages are packed into. Defaults to <RepoRoot>/nupkgs (gitignored).

.PARAMETER ConsumerDir
    Where the throwaway consumer is scaffolded. Defaults to $env:RUNNER_TEMP/pkg-consumer on GitHub
    Actions, else to <system temp>/mmca-pkg-consumer. The Scaffold stage empties it first.

.EXAMPLE
    pwsh build/consumer-check/Invoke-ConsumerCheck.ps1
    pwsh build/consumer-check/Invoke-ConsumerCheck.ps1 -Stage Probes -Version 0.0.1-local.20261018120000
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Pack', 'Scaffold', 'Build', 'Probes')]
    [string] $Stage = 'All',
    [string] $Version = $env:PACK_VERSION,
    [string] $RepoRoot = (Join-Path $PSScriptRoot '..' '..'),
    [string] $FeedDir = '',
    [string] $ConsumerDir = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Exit codes are checked explicitly: a negative probe is EXPECTED to exit non-zero.
$PSNativeCommandUseErrorActionPreference = $false

function Stop-Check {
    param([string] $Message)

    if ($env:GITHUB_ACTIONS -eq 'true') {
        Write-Host "::error::$Message"
    }
    else {
        Write-Host "ERROR: $Message"
    }

    exit 1
}

function Invoke-Dotnet {
    param([string[]] $Arguments, [string] $FailureMessage)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        Stop-Check "$FailureMessage (dotnet exited $LASTEXITCODE)"
    }
}

function Write-TextFile {
    param([string] $Path, [string[]] $Lines)

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    [System.IO.File]::WriteAllText($Path, (($Lines -join "`n") + "`n"))
}

$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
if ([string]::IsNullOrEmpty($FeedDir)) {
    $FeedDir = Join-Path $RepoRoot 'nupkgs'
}

if ([string]::IsNullOrEmpty($ConsumerDir)) {
    $tempRoot = if ([string]::IsNullOrEmpty($env:RUNNER_TEMP)) { [System.IO.Path]::GetTempPath() } else { $env:RUNNER_TEMP }
    $leaf = if ([string]::IsNullOrEmpty($env:RUNNER_TEMP)) { 'mmca-pkg-consumer' } else { 'pkg-consumer' }
    $ConsumerDir = Join-Path $tempRoot $leaf
}

$FeedDir = [System.IO.Path]::GetFullPath($FeedDir)
$ConsumerDir = [System.IO.Path]::GetFullPath($ConsumerDir)

if ([string]::IsNullOrEmpty($Version)) {
    if ($Stage -ne 'All') {
        Stop-Check "-Stage $Stage needs -Version (or PACK_VERSION) so every stage pins the same packed version"
    }

    $Version = '0.0.1-local.' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss', [System.Globalization.CultureInfo]::InvariantCulture)
}

Write-Host "Package-mode consumer check: stage $Stage, version $Version"
Write-Host "  repo:     $RepoRoot"
Write-Host "  feed:     $FeedDir"
Write-Host "  consumer: $ConsumerDir"

function Invoke-InDirectory {
    param([string] $Path, [scriptblock] $Action)

    # The working directory decides which global.json picks the SDK: the pack runs at the repo root
    # (the repo's global.json), the consumer and the probes outside it, exactly as the CI steps do.
    Push-Location -LiteralPath $Path
    try {
        & $Action
    }
    finally {
        Pop-Location
    }
}

function Invoke-PackStage {
    Invoke-InDirectory -Path $RepoRoot -Action {
        Invoke-Dotnet -FailureMessage 'dotnet pack MMCA.Common.slnx failed' -Arguments @(
            'pack', 'MMCA.Common.slnx', '-c', 'Release', '-o', $FeedDir,
            '-p:MinVerSkip=true', "-p:Version=$Version", "-p:PackageVersion=$Version")
    }
}

function Invoke-ScaffoldStage {
    if (Test-Path -LiteralPath $ConsumerDir) {
        Remove-Item -LiteralPath $ConsumerDir -Recurse -Force
    }

    New-Item -ItemType Directory -Path $ConsumerDir -Force | Out-Null

    Write-TextFile -Path (Join-Path $ConsumerDir 'nuget.config') -Lines @(
        '<?xml version="1.0" encoding="utf-8"?>'
        '<configuration>'
        '  <packageSources>'
        '    <clear />'
        "    <add key=`"local-nupkgs`" value=`"$FeedDir`" />"
        '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
        '  </packageSources>'
        '  <packageSourceMapping>'
        '    <packageSource key="local-nupkgs">'
        '      <package pattern="MMCA.Common.*" />'
        '    </packageSource>'
        '    <packageSource key="nuget.org">'
        '      <package pattern="*" />'
        '    </packageSource>'
        '  </packageSourceMapping>'
        '</configuration>')

    Write-TextFile -Path (Join-Path $ConsumerDir 'Consumer.csproj') -Lines @(
        '<Project Sdk="Microsoft.NET.Sdk">'
        '  <PropertyGroup>'
        '    <TargetFramework>net10.0</TargetFramework>'
        '    <Nullable>enable</Nullable>'
        '    <ImplicitUsings>enable</ImplicitUsings>'
        '  </PropertyGroup>'
        '  <ItemGroup>'
        "    <PackageReference Include=`"MMCA.Common.API`" Version=`"$Version`" />"
        "    <PackageReference Include=`"MMCA.Common.Infrastructure`" Version=`"$Version`" />"
        "    <PackageReference Include=`"MMCA.Common.Testing.Architecture`" Version=`"$Version`" />"
        '  </ItemGroup>'
        '</Project>')

    Write-TextFile -Path (Join-Path $ConsumerDir 'Smoke.cs') -Lines @(
        'using MMCA.Common.Shared.Abstractions;'
        ''
        'namespace PackageConsumer;'
        ''
        'public static class Smoke'
        '{'
        '    public static Result Ok() => Result.Success();'
        '}')

    Write-Host "Scaffolded the throwaway consumer in $ConsumerDir"
}

function Invoke-BuildStage {
    Invoke-InDirectory -Path $ConsumerDir -Action {
        Invoke-Dotnet -FailureMessage 'the package-mode consumer did not restore and build against the packed nupkgs' -Arguments @(
            'build', 'Consumer.csproj', '-c', 'Release')
    }
}

function New-Probe {
    param([string] $Name, [string[]] $Items)

    $lines = @(
        '<Project Sdk="Microsoft.NET.Sdk">'
        '  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>'
        '  <ItemGroup>')
    $lines += $Items | ForEach-Object { "    $_" }
    $lines += @(
        '  </ItemGroup>'
        '</Project>')

    Write-TextFile -Path (Join-Path $ConsumerDir 'probes' $Name "$Name.csproj") -Lines $lines
}

function Get-PackageItem {
    param([string] $Id)

    "<PackageReference Include=`"$Id`" Version=`"$Version`" />"
}

function Get-ProjectItem {
    param([string] $Name)

    "<ProjectReference Include=`"../$Name/$Name.csproj`" />"
}

function Assert-ProbeFails {
    param([string] $Name, [string] $ErrorCode)

    $project = Join-Path $ConsumerDir 'probes' $Name "$Name.csproj"
    $log = Join-Path $ConsumerDir 'probes' "$Name.log"
    & dotnet build $project -c Release *> $log
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        Get-Content -LiteralPath $log | Write-Host
        Stop-Check "$Name built although it violates a layer rule; $ErrorCode did not fire"
    }

    if (-not (Select-String -LiteralPath $log -SimpleMatch -Pattern $ErrorCode -Quiet)) {
        Get-Content -LiteralPath $log | Write-Host
        Stop-Check "$Name failed for a reason other than $ErrorCode"
    }

    Write-Host "${Name}: $ErrorCode fired as expected"
}

function Invoke-ProbesStage {
    Invoke-InDirectory -Path $ConsumerDir -Action { Invoke-Probes }
}

function Invoke-Probes {
    New-Probe -Name 'Probe.Sample.Domain' -Items @(Get-PackageItem 'MMCA.Common.Domain')
    New-Probe -Name 'Probe.Sample.Application' -Items @(
        (Get-PackageItem 'MMCA.Common.Application'), (Get-ProjectItem 'Probe.Sample.Domain'))
    Invoke-Dotnet -FailureMessage 'the positive layer-rule probe Probe.Sample.Application did not build' -Arguments @(
        'build', (Join-Path $ConsumerDir 'probes' 'Probe.Sample.Application' 'Probe.Sample.Application.csproj'), '-c', 'Release')

    New-Probe -Name 'Probe.BadPackage.Domain' -Items @(
        (Get-PackageItem 'MMCA.Common.Domain'), (Get-PackageItem 'MMCA.Common.Infrastructure'))
    Assert-ProbeFails -Name 'Probe.BadPackage.Domain' -ErrorCode 'MMCA0002'

    New-Probe -Name 'Probe.BadProject.Application' -Items @(Get-PackageItem 'MMCA.Common.Application')
    New-Probe -Name 'Probe.BadProject.Domain' -Items @(
        (Get-PackageItem 'MMCA.Common.Domain'), (Get-ProjectItem 'Probe.BadProject.Application'))
    Assert-ProbeFails -Name 'Probe.BadProject.Domain' -ErrorCode 'MMCA0001'

    Write-Host 'Layer-rule probes: positive built, MMCA0001 and MMCA0002 fired.'
}

switch ($Stage) {
    'Pack' { Invoke-PackStage }
    'Scaffold' { Invoke-ScaffoldStage }
    'Build' { Invoke-BuildStage }
    'Probes' { Invoke-ProbesStage }
    'All' {
        Invoke-PackStage
        Invoke-ScaffoldStage
        Invoke-BuildStage
        Invoke-ProbesStage
    }
}

Write-Host "Package-mode consumer check: stage $Stage passed."
exit 0
