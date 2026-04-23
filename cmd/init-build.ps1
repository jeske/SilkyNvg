#!/usr/bin/env pwsh
# init-build.ps1 — One-time setup for building SilkyNvg from a clean machine
#
# Prerequisites already assumed:
#   - Windows 10/11
#   - .NET SDK 10+ installed (https://dot.net)
#   - PowerShell 7+ (pwsh)
#   - Git (for version computation during pack)
#   - winget (ships with Windows 11, or install from Microsoft Store on Windows 10)
#
# This script installs:
#   1. .NET Android workload (for net10.0-android targets)
#   2. OpenJDK 21 via winget (required by Android SDK manager)
#   3. Android SDK command-line tools + platform/build-tools (for Android compilation)
#   4. Registers the local NuGet feed (C:\PROJECTS\LocalNuGet)
#   5. Restores NuGet packages for the solution
#
# Usage:
#   .\cmd\init-build.ps1                          # full setup
#   .\cmd\init-build.ps1 -AndroidSdkPath D:\SDK   # custom Android SDK location
#   .\cmd\init-build.ps1 -SkipAndroid             # skip Android SDK setup (desktop-only)
#
param(
    [string]$AndroidSdkPath = 'C:\PROJECTS\AndroidSDK',
    [switch]$SkipAndroid
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $projectRoot 'SilkyNvg.sln'
$localNuGetFeedPath = 'C:\PROJECTS\LocalNuGet'

Write-Host "`n╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║            SilkyNvg Build Environment Setup                 ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan

# ── Step 0: Verify .NET SDK ──────────────────────────────────────────────
Write-Host "`n=== Step 0: Verifying .NET SDK ===" -ForegroundColor Cyan
$dotnetVersion = & dotnet --version 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: .NET SDK not found. Install from https://dot.net" -ForegroundColor Red
    exit 1
}
$majorVersion = [int]($dotnetVersion -split '\.')[0]
if ($majorVersion -lt 10) {
    Write-Host "WARNING: .NET SDK $dotnetVersion detected. This project targets net10.0; you may need .NET 10+ SDK." -ForegroundColor Yellow
} else {
    Write-Host "  .NET SDK $dotnetVersion ✓" -ForegroundColor Green
}

# ── Step 1: Install .NET Android workload ────────────────────────────────
if (-not $SkipAndroid) {
    Write-Host "`n=== Step 1: Installing .NET Android workload ===" -ForegroundColor Cyan
    Write-Host "  This may take several minutes on first install..." -ForegroundColor DarkGray
    dotnet workload install android
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Failed to install Android workload (exit code $LASTEXITCODE)" -ForegroundColor Red
        Write-Host "  Try running as Administrator, or run: dotnet workload install android" -ForegroundColor Yellow
        exit 1
    }
    Write-Host "  Android workload ✓" -ForegroundColor Green
} else {
    Write-Host "`n=== Step 1: Skipping Android workload (--SkipAndroid) ===" -ForegroundColor Yellow
}

# ── Step 2: Install OpenJDK 21 (required by Android SDK manager) ─────────
if (-not $SkipAndroid) {
    Write-Host "`n=== Step 2: Installing OpenJDK 21 ===" -ForegroundColor Cyan

    # Check if JDK 21 is already installed
    $jdkPath = "C:\Program Files\Microsoft\jdk-21*"
    $existingJdk = Get-ChildItem $jdkPath -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($existingJdk) {
        Write-Host "  OpenJDK 21 already installed at: $($existingJdk.FullName) ✓" -ForegroundColor Green
        $javaHomePath = $existingJdk.FullName
    } else {
        Write-Host "  Installing Microsoft OpenJDK 21 via winget..." -ForegroundColor DarkGray
        winget install --id Microsoft.OpenJDK.21 --accept-source-agreements --accept-package-agreements
        if ($LASTEXITCODE -ne 0) {
            Write-Host "ERROR: Failed to install OpenJDK 21 (exit code $LASTEXITCODE)" -ForegroundColor Red
            Write-Host "  Install manually: winget install Microsoft.OpenJDK.21" -ForegroundColor Yellow
            exit 1
        }
        $existingJdk = Get-ChildItem $jdkPath -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $existingJdk) {
            Write-Host "ERROR: OpenJDK 21 installed but not found at expected path." -ForegroundColor Red
            exit 1
        }
        $javaHomePath = $existingJdk.FullName
        Write-Host "  OpenJDK 21 installed at: $javaHomePath ✓" -ForegroundColor Green
    }
    $env:JAVA_HOME = $javaHomePath
    $env:Path = "$javaHomePath\bin;$env:Path"
} else {
    Write-Host "`n=== Step 2: Skipping OpenJDK (--SkipAndroid) ===" -ForegroundColor Yellow
}

# ── Step 3: Install Android SDK command-line tools + platform ────────────
if (-not $SkipAndroid) {
    Write-Host "`n=== Step 3: Installing Android SDK ===" -ForegroundColor Cyan
    Write-Host "  Target: $AndroidSdkPath" -ForegroundColor DarkGray

    $sdkManagerPath = Join-Path $AndroidSdkPath 'cmdline-tools\latest\bin\sdkmanager.bat'

    if (-not (Test-Path $sdkManagerPath)) {
        Write-Host "  Downloading Android command-line tools..." -ForegroundColor DarkGray
        New-Item -ItemType Directory -Path $AndroidSdkPath -Force | Out-Null
        $cmdLineToolsUrl = 'https://dl.google.com/android/repository/commandlinetools-win-11076708_latest.zip'
        $zipPath = Join-Path $env:TEMP 'android-cmdline-tools.zip'

        Invoke-WebRequest -Uri $cmdLineToolsUrl -OutFile $zipPath -UseBasicParsing
        Write-Host "  Extracting..." -ForegroundColor DarkGray
        $tempExtract = Join-Path $AndroidSdkPath 'cmdline-tools-temp'
        Expand-Archive -Path $zipPath -DestinationPath $tempExtract -Force

        $latestDir = Join-Path $AndroidSdkPath 'cmdline-tools\latest'
        New-Item -ItemType Directory -Path $latestDir -Force | Out-Null
        Move-Item -Path (Join-Path $tempExtract 'cmdline-tools\*') -Destination $latestDir -Force
        Remove-Item -Path $tempExtract -Recurse -Force
        Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
        Write-Host "  Command-line tools extracted ✓" -ForegroundColor Green
    } else {
        Write-Host "  Android command-line tools already present ✓" -ForegroundColor Green
    }

    # Install required SDK components (accept licenses automatically)
    Write-Host "  Installing Android platforms, build-tools, and platform-tools..." -ForegroundColor DarkGray
    echo "y" | & $sdkManagerPath --sdk_root="$AndroidSdkPath" "platforms;android-36" "build-tools;36.0.0" "platform-tools" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "WARNING: sdkmanager reported non-zero exit code ($LASTEXITCODE). Check Android SDK installation." -ForegroundColor Yellow
    } else {
        Write-Host "  Android SDK components installed ✓" -ForegroundColor Green
    }

    # Set environment variables (user-level, persists across sessions)
    [System.Environment]::SetEnvironmentVariable('ANDROID_HOME', $AndroidSdkPath, 'User')
    [System.Environment]::SetEnvironmentVariable('ANDROID_SDK_ROOT', $AndroidSdkPath, 'User')
    $env:ANDROID_HOME = $AndroidSdkPath
    $env:ANDROID_SDK_ROOT = $AndroidSdkPath
    Write-Host "  ANDROID_HOME=$AndroidSdkPath (set for user) ✓" -ForegroundColor Green
} else {
    Write-Host "`n=== Step 3: Skipping Android SDK (--SkipAndroid) ===" -ForegroundColor Yellow
}

# ── Step 4: Register local NuGet feed ────────────────────────────────────
Write-Host "`n=== Step 4: Registering local NuGet feed ===" -ForegroundColor Cyan

# Ensure the local NuGet feed directory exists
if (-not (Test-Path $localNuGetFeedPath)) {
    New-Item -ItemType Directory -Path $localNuGetFeedPath -Force | Out-Null
    Write-Host "  Created: $localNuGetFeedPath" -ForegroundColor DarkGray
}

# Check if LocalNuGet source is already registered
$existingSources = dotnet nuget list source 2>&1
if ($existingSources -match 'LocalNuGet') {
    Write-Host "  LocalNuGet source already registered ✓" -ForegroundColor Green
} else {
    dotnet nuget add source $localNuGetFeedPath --name LocalNuGet
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Failed to register local NuGet source" -ForegroundColor Red
        exit 1
    }
    Write-Host "  LocalNuGet source registered at: $localNuGetFeedPath ✓" -ForegroundColor Green
}

# ── Step 5: Restore NuGet packages ───────────────────────────────────────
Write-Host "`n=== Step 5: Restoring NuGet packages ===" -ForegroundColor Cyan
dotnet restore $solutionPath
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: NuGet restore failed (exit code $LASTEXITCODE)" -ForegroundColor Red
    Write-Host "  Check that all required packages are available in nuget.org and LocalNuGet." -ForegroundColor Yellow
    Write-Host "  The local feed must contain ArtificialNecessity.Veldrid packages." -ForegroundColor Yellow
    exit 1
}
Write-Host "  NuGet restore ✓" -ForegroundColor Green

# ── Done ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║               SETUP COMPLETE                                ║" -ForegroundColor Green
Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
Write-Host "  You can now build the solution:" -ForegroundColor White
Write-Host "    dotnet build SilkyNvg.sln -c Release" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  To publish local NuGet packages:" -ForegroundColor White
Write-Host "    .\cmd\publish-local.ps1" -ForegroundColor DarkGray
Write-Host ""
if (-not $SkipAndroid) {
    Write-Host "  NOTE: You may need to restart your terminal for ANDROID_HOME" -ForegroundColor Yellow
    Write-Host "  and JAVA_HOME environment variables to take effect." -ForegroundColor Yellow
    Write-Host ""
}
