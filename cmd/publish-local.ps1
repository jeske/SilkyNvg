#!/usr/bin/env pwsh
# publish-local.ps1 — Build release packages and deploy to local NuGet feed
#
# Versioning is timestamp-based (v2) — every build gets a unique version
# automatically via Silky.shared.Build.props. No version files to manage.
# The timestamp is captured once here and passed to MSBuild so all projects
# in the solution get the exact same version (no inter-project skew).
#
# Requires: LOCAL_NUGET_REPO environment variable must be set
#
# Usage:
#   .\cmd\publish-local.ps1              # build + pack + deploy
#   .\cmd\publish-local.ps1 -DryRun      # show what would happen, don't build
#
param(
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve project root (one level up from cmd/)
$projectRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $projectRoot 'SilkyNvg.sln'

# Require LOCAL_NUGET_REPO environment variable
if (-not $env:LOCAL_NUGET_REPO) {
    Write-Host 'ERROR: LOCAL_NUGET_REPO environment variable is not set!' -ForegroundColor Red
    exit 1
}
$localNuGetFeedPath = $env:LOCAL_NUGET_REPO

# Capture timestamp ONCE so all projects in the solution get the same version
$now = [System.DateTime]::Now
$buildYYMM   = $now.ToString('yyMM')
$buildDDHH   = $now.ToString('ddHH')
$buildmmss   = $now.ToString('mmss')
$buildYYMMDD = $now.ToString('yyMMdd')
$buildHHmmss = $now.ToString('HHmmss')
$versionProps = @("/p:_BuildYYMM=$buildYYMM", "/p:_BuildDDHH=$buildDDHH", "/p:_Buildmmss=$buildmmss", "/p:_BuildYYMMDD=$buildYYMMDD", "/p:_BuildHHmmss=$buildHHmmss")
$packageVersion = "1.$buildYYMMDD.$buildHHmmss"

Write-Host "`n=== Publishing SilkyNvg release to local NuGet feed ===" -ForegroundColor Cyan
Write-Host "Version stamp: 1.$buildYYMM.$buildDDHH.$buildmmss (pkg: $packageVersion)" -ForegroundColor Gray
Write-Host "Local feed:    $localNuGetFeedPath" -ForegroundColor Gray

if ($DryRun) {
    Write-Host "`n[DRY RUN] Would build and deploy version $packageVersion to $localNuGetFeedPath" -ForegroundColor Yellow
    exit 0
}

# ── Build and pack release packages ─────────────────────────────────────
$env:LOCAL_NUGET_REPO = $localNuGetFeedPath
$packageOutputDir = Join-Path $projectRoot 'bin\Packages\Release'
$failedSteps = @()

# Clean old packages to avoid deploying stale versions
if (Test-Path $packageOutputDir) {
    Remove-Item -Path "$packageOutputDir\ArtificialNecessity.SilkyNvg*.nupkg" -Force -ErrorAction SilentlyContinue
}

# Capture timestamp before build/pack so we can identify newly deployed packages
$deployStartTime = Get-Date

Write-Host "`n=== Building and packaging ===" -ForegroundColor Cyan

# Pack only the 3 public packages (not the whole solution, which would produce unwanted granular packages)
$packableProjects = @(
    (Join-Path $projectRoot 'src\SilkyNvg.Package\SilkyNvg.Package.csproj'),           # ArtificialNecessity.SilkyNvg (umbrella)
    (Join-Path $projectRoot 'src\rendering\SilkyNvg.Rendering.OpenGL\SilkyNvg.Rendering.OpenGL.csproj'),   # ArtificialNecessity.SilkyNvg.Rendering.OpenGL
    (Join-Path $projectRoot 'src\rendering\SilkyNvg.Rendering.Veldrid\SilkyNvg.Rendering.Veldrid.csproj')  # ArtificialNecessity.SilkyNvg.Rendering.Veldrid
)

foreach ($packableProjectPath in $packableProjects) {
    $packableProjectName = [System.IO.Path]::GetFileNameWithoutExtension($packableProjectPath)
    Write-Host "  Packing $packableProjectName..." -ForegroundColor DarkGray
    dotnet pack $packableProjectPath -c Release @versionProps
    if ($LASTEXITCODE -ne 0) {
        $failedSteps += "dotnet pack ($packableProjectName) failed with exit code $LASTEXITCODE"
        Write-Host "ERROR: dotnet pack ($packableProjectName) failed with exit code $LASTEXITCODE" -ForegroundColor Red
    }
}

# ── Final status ───────────────────────────────────────────────────────
Write-Host ""
if ($failedSteps.Count -gt 0) {
    Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Red
    Write-Host "║                    PUBLISH FAILED                           ║" -ForegroundColor Red
    Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Red
    foreach ($failedStep in $failedSteps) {
        Write-Host "  ✗ $failedStep" -ForegroundColor Red
    }
    Write-Host ""
    exit 1
} else {
    # Show packages deployed during this run
    $deployedPackages = Get-ChildItem "$localNuGetFeedPath\*.nupkg" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $deployStartTime } |
        Sort-Object Name
    Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Green
    Write-Host "║                   PUBLISH SUCCEEDED                         ║" -ForegroundColor Green
    Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Green
    if ($deployedPackages) {
        Write-Host "`nDeployed packages:" -ForegroundColor Cyan
        foreach ($pkg in $deployedPackages) {
            $sizeKB = [math]::Round($pkg.Length / 1024, 1)
            Write-Host "  $($pkg.Name)  ($sizeKB KB)" -ForegroundColor Green
        }
    } else {
        Write-Host "  Version:  $packageVersion" -ForegroundColor Green
        Write-Host "  Feed:     $localNuGetFeedPath" -ForegroundColor Green
    }
    Write-Host ""
}