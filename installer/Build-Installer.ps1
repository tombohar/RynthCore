#Requires -Version 5.1
<#
.SYNOPSIS
    Builds all RynthCore components and produces RynthCore-Setup.exe via Inno Setup.

.DESCRIPTION
    1. Publishes the Avalonia launcher (self-contained, x86)
    2. Publishes RynthCore.Engine (NativeAOT, x86 -- ~2 min)
    3. Publishes RynthCore.Plugin.RynthAi (NativeAOT, x86)
    4. Stages all output under installer\staging\app\
    5. Invokes ISCC.exe to produce installer\Output\RynthCore-Setup.exe

.PARAMETER Configuration
    Build configuration. Default: Release

.PARAMETER IsccPath
    Path to ISCC.exe (Inno Setup compiler). Default looks in the standard Inno Setup 6 location.
    Install Inno Setup from: https://jrsoftware.org/isdl.php

.PARAMETER SkipBuild
    Skip dotnet publish steps and just re-run ISCC against the existing staging directory.
#>
param(
    [string]$Configuration = "Release",
    [string]$IsccPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Off

$ScriptDir    = $PSScriptRoot
$RepoRoot     = Split-Path $ScriptDir -Parent          # C:\Projects\RynthCore
$ProjectsRoot = Split-Path $RepoRoot -Parent           # C:\Projects

$LauncherProject = "$RepoRoot\src\RynthCore.App.Avalonia\RynthCore.App.Avalonia.csproj"
$EngineProject   = "$RepoRoot\src\RynthCore.Engine\RynthCore.Engine.csproj"
$PluginProject   = "$ProjectsRoot\RynthSuite\Plugins\RynthCore.Plugin.RynthAi\RynthCore.Plugin.RynthAi.csproj"

$LauncherPublish = "$RepoRoot\src\RynthCore.App.Avalonia\bin\$Configuration\net9.0-windows7.0\win-x86\publish"
$EnginePublish   = "$RepoRoot\src\RynthCore.Engine\bin\$Configuration\net9.0-windows\win-x86\publish"
$PluginPublish   = "$ProjectsRoot\RynthSuite\Plugins\RynthCore.Plugin.RynthAi\bin\$Configuration\net9.0-windows\win-x86\publish"

$StagingDir  = "$ScriptDir\staging\app"

# ── Validate projects ───────────────────────────────────────────────────────
foreach ($p in @($LauncherProject, $EngineProject, $PluginProject)) {
    if (-not (Test-Path $p)) {
        throw "Project not found: $p`nUpdate paths in Build-Installer.ps1 if your repo layout differs."
    }
}

if (-not $SkipBuild) {
    # ── 1. Launcher (self-contained Avalonia WinExe) ─────────────────────────
    Write-Host ""
    Write-Host "[1/3] Publishing Launcher (self-contained, x86)..." -ForegroundColor Cyan
    dotnet publish $LauncherProject -c $Configuration -r win-x86 --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed (exit $LASTEXITCODE)" }

    # ── 2. Engine (NativeAOT — the slow one) ──────────────────────────────────
    Write-Host ""
    Write-Host "[2/3] Publishing Engine (NativeAOT, ~2 min)..." -ForegroundColor Cyan
    dotnet publish $EngineProject -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Engine publish failed (exit $LASTEXITCODE)" }

    # ── 3. Plugin (NativeAOT) ─────────────────────────────────────────────────
    Write-Host ""
    Write-Host "[3/3] Publishing Plugin (NativeAOT)..." -ForegroundColor Cyan
    dotnet publish $PluginProject -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Plugin publish failed (exit $LASTEXITCODE)" }
}

# ── Stage ────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Staging files..." -ForegroundColor Cyan

if (Test-Path "$ScriptDir\staging") {
    Remove-Item "$ScriptDir\staging" -Recurse -Force
}
New-Item -ItemType Directory -Path "$StagingDir\Runtime\Native"   -Force | Out-Null
New-Item -ItemType Directory -Path "$StagingDir\Runtime\Plugins"  -Force | Out-Null

# Launcher root files (rename .exe → RynthCore.exe; drop .pdb)
foreach ($file in (Get-ChildItem "$LauncherPublish" -File)) {
    if ($file.Extension -eq '.pdb') { continue }
    $destName = if ($file.Name -eq 'RynthCore.App.Avalonia.exe') { 'RynthCore.exe' } else { $file.Name }
    Copy-Item $file.FullName "$StagingDir\$destName" -Force
}

# Engine runtime files (into Runtime\, skip .pdb)
foreach ($file in (Get-ChildItem "$EnginePublish" -File)) {
    if ($file.Extension -eq '.pdb') { continue }
    Copy-Item $file.FullName "$StagingDir\Runtime\$($file.Name)" -Force
}

# Engine Native subfolder
if (Test-Path "$EnginePublish\Native") {
    foreach ($file in (Get-ChildItem "$EnginePublish\Native" -File)) {
        if ($file.Extension -eq '.pdb') { continue }
        Copy-Item $file.FullName "$StagingDir\Runtime\Native\$($file.Name)" -Force
    }
}

# Plugin DLL only (cimgui.dll excluded — already in Runtime from Engine)
$pluginDll = "$PluginPublish\RynthCore.Plugin.RynthAi.dll"
if (-not (Test-Path $pluginDll)) { throw "Plugin DLL not found at: $pluginDll" }
Copy-Item $pluginDll "$StagingDir\Runtime\Plugins\" -Force

# Report staged sizes
$engineDll = "$StagingDir\Runtime\RynthCore.Engine.dll"
if (Test-Path $engineDll) {
    $sizeMb = [math]::Round((Get-Item $engineDll).Length / 1MB, 1)
    Write-Host "  Engine.dll: ${sizeMb} MB"
}
$totalMb = [math]::Round((Get-ChildItem "$StagingDir" -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "  Total staged: ${totalMb} MB"

Write-Host "Staging complete: $StagingDir" -ForegroundColor Green

# ── Inno Setup ───────────────────────────────────────────────────────────────
if (-not (Test-Path $IsccPath)) {
    Write-Host ""
    Write-Warning "Inno Setup compiler not found at:"
    Write-Warning "  $IsccPath"
    Write-Warning "Install Inno Setup 6 from: https://jrsoftware.org/isdl.php"
    Write-Warning "Then re-run this script, or invoke ISCC manually:"
    Write-Warning "  `"$IsccPath`" `"$ScriptDir\RynthCore.iss`""
    exit 0
}

Write-Host ""
Write-Host "Building installer..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path "$ScriptDir\Output" -Force | Out-Null
& $IsccPath "$ScriptDir\RynthCore.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC build failed (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "SUCCESS" -ForegroundColor Green
Write-Host "Installer: $ScriptDir\Output\RynthCore-Setup.exe"
