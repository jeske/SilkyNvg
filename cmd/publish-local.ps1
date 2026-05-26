#!/usr/bin/env pwsh
# publish-local.ps1 — Build release packages and deploy to local NuGet feed
#
# Uses timestamp-based versioning (v2): every build automatically gets a unique
# version based on the current date/time. No version file manipulation needed.
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
    Write-Host "ERROR: LOCAL_NUGET_REPO environment variable is not set!" -ForegroundColor Red
    exit 1
}
$localNuGetFeedPath = $env:LOCAL_NUGET_REPO

Write-Host "`n=== Publishing SilkyNvg release to local NuGet feed ===" -ForegroundColor Cyan
Write-Host 'Versioning:  timestamp-based (v2) — unique per build' -ForegroundColor DarkGray
Write-Host "Local feed:  $localNuGetFeedPath" -ForegroundColor DarkGray

if ($DryRun) {
    Write-Host "`n[DRY RUN] Would build release packages and deploy to $localNuGetFeedPath" -ForegroundColor Yellow
    Write-Host '[DRY RUN] Version will be determined at build time (timestamp-based)' -ForegroundColor Yellow
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
    dotnet pack $packableProjectPath -c Release
    if ($LASTEXITCODE -ne 0) {
        $failedSteps += "dotnet pack ($packableProjectName) failed with exit code $LASTEXITCODE"
        Write-Host "ERROR: dotnet pack ($packableProjectName) failed with exit code $LASTEXITCODE" -ForegroundColor Red
    }
}

# ── Deploy packages to local NuGet feed ─────────────────────────────────
if ($failedSteps.Count -eq 0) {
    Write-Host "`n=== Deploying to local NuGet feed ===" -ForegroundColor Cyan
    if (-not (Test-Path $localNuGetFeedPath)) {
        New-Item -ItemType Directory -Path $localNuGetFeedPath -Force | Out-Null
    }

    $deployedPackageFiles = Get-ChildItem -Path $packageOutputDir -Filter "ArtificialNecessity.SilkyNvg*.nupkg" -ErrorAction SilentlyContinue
    if ($deployedPackageFiles) {
        foreach ($packageFile in $deployedPackageFiles) {
            Copy-Item -Path $packageFile.FullName -Destination $localNuGetFeedPath -Force
            Write-Host "  + $($packageFile.Name)" -ForegroundColor Green
        }
    } else {
        $failedSteps += "No ArtificialNecessity.SilkyNvg packages found in $packageOutputDir after successful build"
        Write-Host "ERROR: No packages produced!" -ForegroundColor Red
    }
} else {
    Write-Host "`n=== Skipping deploy (build failed) ===" -ForegroundColor Yellow
}

# ── Final status banner ──────────────────────────────────────────────────
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
    Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Green
    Write-Host "║                   PUBLISH SUCCEEDED                         ║" -ForegroundColor Green
    Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Green
    # Show the version of the first package found
    $firstPkg = Get-ChildItem -Path $packageOutputDir -Filter "ArtificialNecessity.SilkyNvg*.nupkg" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($firstPkg) {
        Write-Host "  Package:  $($firstPkg.Name)" -ForegroundColor Green
    }
    Write-Host "  Feed:     $localNuGetFeedPath" -ForegroundColor Green
    Write-Host ""
}