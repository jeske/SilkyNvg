#!/usr/bin/env pwsh
# publish-local.ps1 — Build release packages and deploy to local NuGet feed
#
# Increments buildNumberOffset in version.jsonc using the JsonPeek CLI tool
# from the ArtificialNecessity.CodeAnalyzers package, then builds all
# packable projects with clean (non-prerelease) version numbers.
#
# Usage:
#   .\cmd\publish-local.ps1              # increment + build + deploy
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
$versionJsoncPath = Join-Path $projectRoot 'version.jsonc'
$sharedBuildPropsPath = Join-Path $projectRoot 'Silky.shared.Build.props'

# Use LOCAL_NUGET_FEED environment variable, fallback to default if not set
$localNuGetFeedPath = $env:LOCAL_NUGET_FEED
if (-not $localNuGetFeedPath) {
    $localNuGetFeedPath = 'C:\PROJECTS\LocalNuGet'
    Write-Host "WARNING: LOCAL_NUGET_FEED environment variable not set, using default: $localNuGetFeedPath" -ForegroundColor Yellow
}


# ── Resolve JsonPeek CLI tool from NuGet cache ──────────────────────────
# Read the AN.CodeAnalyzers version from Silky.shared.Build.props
$sharedBuildPropsContent = Get-Content $sharedBuildPropsPath -Raw
$codeAnalyzersVersionMatch = [regex]::Match($sharedBuildPropsContent, 'ANCodeAnalyzersVersion>([^<]+)<')
if (-not $codeAnalyzersVersionMatch.Success) {
    Write-Host "ERROR: Could not find ANCodeAnalyzersVersion in $sharedBuildPropsPath" -ForegroundColor Red
    exit 1
}
$codeAnalyzersVersion = $codeAnalyzersVersionMatch.Groups[1].Value
$jsonPeekExePath = Join-Path $env:USERPROFILE ".nuget\packages\artificialnecessity.codeanalyzers\$codeAnalyzersVersion\tools\net8.0\any\JsonPeek.exe"

if (-not (Test-Path $jsonPeekExePath)) {
    Write-Host "ERROR: JsonPeek CLI tool not found at: $jsonPeekExePath" -ForegroundColor Red
    Write-Host "Run 'dotnet restore SilkyNvg.sln' to download the package." -ForegroundColor Yellow
    exit 1
}

# ── Read current version info ────────────────────────────────────────────
$baseVersion = & $jsonPeekExePath $versionJsoncPath version
$currentBuildNumberOffset = & $jsonPeekExePath $versionJsoncPath buildNumberOffset
$currentVersion = "$baseVersion.$currentBuildNumberOffset"

Write-Host "`n=== Publishing SilkyNvg release to local NuGet feed ===" -ForegroundColor Cyan
Write-Host "Current version: $currentVersion" -ForegroundColor DarkGray
Write-Host "JsonPeek tool:   $jsonPeekExePath" -ForegroundColor DarkGray

# ── Increment buildNumberOffset in version.jsonc ─────────────────────────
$newBuildNumberOffset = & $jsonPeekExePath --inc-integer $versionJsoncPath buildNumberOffset
$newVersion = "$baseVersion.$newBuildNumberOffset"

Write-Host "New version:     $newVersion" -ForegroundColor Green
Write-Host "Local feed:      $localNuGetFeedPath" -ForegroundColor DarkGray

if ($DryRun) {
    Write-Host "`n[DRY RUN] Would build release version $newVersion and deploy to $localNuGetFeedPath" -ForegroundColor Yellow
    # Revert the increment since this is a dry run
    & $jsonPeekExePath --inc-integer $versionJsoncPath buildNumberOffset -1 | Out-Null
    Write-Host "[DRY RUN] Reverted buildNumberOffset back to $currentBuildNumberOffset" -ForegroundColor Yellow
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
# Pass version explicitly + NewRelease=true to skip ComputeGitVersion (which adds git-height suffix)
$packVersionArgs = @('-c', 'Release', "/p:Version=$newVersion", "/p:PackageVersion=$newVersion", '/p:NewRelease=true')

# Pack only the 3 public packages (not the whole solution, which would produce unwanted granular packages)
$packableProjects = @(
    (Join-Path $projectRoot 'src\SilkyNvg.Package\SilkyNvg.Package.csproj'),           # ArtificialNecessity.SilkyNvg (umbrella)
    (Join-Path $projectRoot 'src\rendering\SilkyNvg.Rendering.OpenGL\SilkyNvg.Rendering.OpenGL.csproj'),   # ArtificialNecessity.SilkyNvg.Rendering.OpenGL
    (Join-Path $projectRoot 'src\rendering\SilkyNvg.Rendering.Veldrid\SilkyNvg.Rendering.Veldrid.csproj')  # ArtificialNecessity.SilkyNvg.Rendering.Veldrid
)

foreach ($packableProjectPath in $packableProjects) {
    $packableProjectName = [System.IO.Path]::GetFileNameWithoutExtension($packableProjectPath)
    Write-Host "  Packing $packableProjectName..." -ForegroundColor DarkGray
    dotnet pack $packableProjectPath @packVersionArgs
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
    Write-Host "  Version:  $newVersion" -ForegroundColor Green
    Write-Host "  Feed:     $localNuGetFeedPath" -ForegroundColor Green
    Write-Host ""
}