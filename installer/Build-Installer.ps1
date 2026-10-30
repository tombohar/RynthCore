#Requires -Version 5.1
<#
.SYNOPSIS
    Builds all RynthCore components and produces RynthCore-Setup.exe via Inno Setup.

.DESCRIPTION
    1. Publishes the Avalonia launcher (self-contained, x86)
    2+3. The engine and the loader. By default the unloadable engine
       (docs/UNLOADABLE_ENGINE_PLAN.md): scripts\Publish-EngineCoreClr.ps1 stages the native
       loader (native\Loader), RynthCore.Shim, the CoreCLR engine (ReadyToRun) and the
       app-local .NET runtime (Runtime\dotnet) straight into staging\app\Runtime.
       -NativeAot builds the old NativeAOT engine + loader instead.
    4. Publishes RynthCore.Plugin.RynthAi (NativeAOT, x86: the release plugins stay NativeAOT
       - the CoreCLR engine loads them - until the update feed can carry a managed plugin's
       dependency DLLs)
    5. Publishes the Loot Editor
    6. Stages all output under installer\staging\app\, plus the hand-built
       RynthCore.SehTrampoline.dll and the Decal bridge (DecalBridge\RynthCore.DecalBridge.dll,
       built here against Decal's reference assemblies), and checks every required file is there
    7. Invokes ISCC.exe to produce installer\Output\RynthCore-Setup.exe

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
    [string]$IsccPath = "",
    # Explicit path to the RynthSuite repo root. When omitted, defaults to the
    # sibling directory of RynthCore (i.e. ..\RynthSuite relative to this repo).
    [string]$RynthSuiteRoot = "",
    # Version string injected into the installer (e.g. "0.3"). When omitted,
    # the version defined in RynthCore.iss is used as-is.
    [string]$Version = "",
    [switch]$SkipBuild,
    # The old NativeAOT engine + loader (a hot reload leaks every engine generation).
    [switch]$NativeAot,
    # The Decal bridge (src\RynthCore.DecalBridge, docs/DECAL_BRIDGE_PLAN.md) is staged into
    # {app}\DecalBridge by default: every release ships it (experimental, opt-in per account;
    # nothing registers it unless the player picks "Decal + RynthCore"). Building it needs
    # Decal installed (its public reference assemblies); without Decal this script fails.
    # -SkipDecalBridge leaves it out (dev builds on a machine without Decal; never a release).
    [switch]$SkipDecalBridge,
    # Old opt-in switch, kept so existing command lines still work; the bridge is the default now.
    [switch]$IncludeDecalBridge,
    # Decal's install folder, when not the default C:\Program Files (x86)\Decal 3.0.
    [string]$DecalDir = "",
    # Managed (collectible) plugins, built by Publish-Update.ps1 (engine API 88): the folder of the
    # plugins' common dependencies, staged into Runtime\PluginShared, and the managed RynthAi DLL,
    # used instead of the NativeAOT RynthAi build. NativeAOT plugins can never be unloaded, so each
    # reload kept a copy of every one (220-450 MB of address space per reload, 2026-10-19).
    [string]$PluginSharedDir = "",
    [string]$RynthAiDll = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Off

# Release number for release.txt, which the launcher's updater reads
# (RynthUpdater.InstalledCoreRelease). -Version yyyy.m.d.n → yyyymmddnn; a dev build
# without -Version records 0 ("unknown", so any published release reads as newer).
# Checked here, before the builds, so a typo fails in a second rather than at the end.
$release = 0
if ($Version) {
    $p = $Version.Split('.')
    if ($p.Count -ne 4 -or ($p | Where-Object { $_ -notmatch '^\d+$' }) -or [int]$p[1] -gt 12 -or [int]$p[2] -gt 31 -or [int]$p[3] -gt 99) {
        throw "-Version must be yyyy.m.d.n (e.g. 2026.9.26.3), got '$Version'"
    }
    $release = [int]$p[0] * 1000000 + [int]$p[1] * 10000 + [int]$p[2] * 100 + [int]$p[3]
}

# Stamp every build with the release, so the launcher, the engine log and the in-game
# Status panel show it (RynthCore.App.BuildVersion); the SDK appends the git commit.
$VersionArgs = @()
if ($Version) { $VersionArgs = @("-p:Version=$Version") }

$ScriptDir    = $PSScriptRoot
$RepoRoot     = Split-Path $ScriptDir -Parent          # e.g. C:\Projects\RynthCore
$ProjectsRoot = Split-Path $RepoRoot -Parent           # e.g. C:\Projects

# RynthSuite root: explicit param takes priority, otherwise sibling convention.
if (-not $RynthSuiteRoot) {
    $RynthSuiteRoot = "$ProjectsRoot\RynthSuite"
}

$LauncherProject   = "$RepoRoot\src\RynthCore.App.Avalonia\RynthCore.App.Avalonia.csproj"
$EngineProject     = "$RepoRoot\src\RynthCore.Engine\RynthCore.Engine.csproj"
$LoaderProject     = "$RepoRoot\src\RynthCore.Loader\RynthCore.Loader.csproj"
$PluginProject    = "$RynthSuiteRoot\Plugins\RynthCore.Plugin.RynthAi\RynthCore.Plugin.RynthAi.csproj"
$LootEditorProject = "$RynthSuiteRoot\Tools\RynthCore.LootEditor\RynthCore.LootEditor.csproj"

$LauncherPublish   = "$RepoRoot\src\RynthCore.App.Avalonia\bin\$Configuration\net10.0-windows7.0\win-x86\publish"
$EnginePublish     = "$RepoRoot\src\RynthCore.Engine\bin\$Configuration\net10.0-windows\win-x86\publish"
$LoaderPublish     = "$RepoRoot\src\RynthCore.Loader\bin\$Configuration\net10.0-windows\win-x86\publish"
$PluginPublish    = "$RynthSuiteRoot\Plugins\RynthCore.Plugin.RynthAi\bin\$Configuration\net10.0-windows\win-x86\publish"
$LootEditorPublish = "$RynthSuiteRoot\Tools\RynthCore.LootEditor\bin\$Configuration\net10.0\win-x86\publish"

$StagingDir  = "$ScriptDir\staging\app"

# ── Validate projects ───────────────────────────────────────────────────────
foreach ($p in @($LauncherProject, $EngineProject, $PluginProject, $LootEditorProject) + @(if ($NativeAot) { $LoaderProject })) {
    if (-not (Test-Path $p)) {
        throw "Project not found: $p`nUpdate paths in Build-Installer.ps1 if your repo layout differs."
    }
}

if (-not $SkipBuild) {
    # ── 1. Launcher (self-contained Avalonia WinExe) ─────────────────────────
    Write-Host ""
    Write-Host "[1/5] Publishing Launcher (self-contained, x86)..." -ForegroundColor Cyan
    dotnet publish $LauncherProject -c $Configuration -r win-x86 --self-contained true @VersionArgs
    if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed (exit $LASTEXITCODE)" }

    if ($NativeAot) {
        # ── 2. Engine (NativeAOT — the slow one) ──────────────────────────────
        Write-Host ""
        Write-Host "[2/5] Publishing Engine (NativeAOT, ~2 min)..." -ForegroundColor Cyan
        dotnet publish $EngineProject -c $Configuration @VersionArgs
        if ($LASTEXITCODE -ne 0) { throw "Engine publish failed (exit $LASTEXITCODE)" }

        # ── 3. Loader (NativeAOT, small) ──────────────────────────────────────
        Write-Host ""
        Write-Host "[3/5] Publishing Loader (NativeAOT)..." -ForegroundColor Cyan
        dotnet publish $LoaderProject -c $Configuration @VersionArgs
        if ($LASTEXITCODE -ne 0) { throw "Loader publish failed (exit $LASTEXITCODE)" }
    }
    # (Default: the CoreCLR engine and native loader are built straight into staging below.)

    # ── 4. Plugin (NativeAOT; a managed RynthAi from Publish-Update replaces it) ──
    if (-not $RynthAiDll) {
        Write-Host ""
        Write-Host "[4/5] Publishing Plugin (NativeAOT)..." -ForegroundColor Cyan
        dotnet publish $PluginProject -c $Configuration @VersionArgs
        if ($LASTEXITCODE -ne 0) { throw "Plugin publish failed (exit $LASTEXITCODE)" }
    }

    # ── 5. Loot Editor (self-contained Avalonia tool) ─────────────────────────
    Write-Host ""
    Write-Host "[5/5] Publishing Loot Editor (self-contained, x86)..." -ForegroundColor Cyan
    dotnet publish $LootEditorProject -c $Configuration -r win-x86 --self-contained true @VersionArgs
    if ($LASTEXITCODE -ne 0) { throw "Loot Editor publish failed (exit $LASTEXITCODE)" }
}

# ── Stage ────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Staging files..." -ForegroundColor Cyan

if (Test-Path "$ScriptDir\staging") {
    Remove-Item "$ScriptDir\staging" -Recurse -Force
}
New-Item -ItemType Directory -Path "$StagingDir\Runtime\Native"   -Force | Out-Null
New-Item -ItemType Directory -Path "$StagingDir\Tools\LootEditor" -Force | Out-Null
# Plugin DLLs are staged into a separate top-level folder (not under
# Runtime\) because the engine no longer auto-scans Runtime\Plugins\.
# The .iss file installs this folder to C:\Games\RynthSuite\<PluginName>\
# (an absolute destination outside {app}) so the plugin lives next to its
# data dirs and the user adds the path via the launcher's Plugins tab.
$pluginStagingDir = "$ScriptDir\staging\plugins"
if (Test-Path $pluginStagingDir) { Remove-Item $pluginStagingDir -Recurse -Force }
New-Item -ItemType Directory -Path "$pluginStagingDir\RynthAi" -Force | Out-Null

# Launcher root files (rename .exe → RynthCore.exe; drop .pdb)
foreach ($file in (Get-ChildItem "$LauncherPublish" -File)) {
    if ($file.Extension -eq '.pdb') { continue }
    $destName = if ($file.Name -eq 'RynthCore.App.Avalonia.exe') { 'RynthCore.exe' } else { $file.Name }
    Copy-Item $file.FullName "$StagingDir\$destName" -Force
}

if ($NativeAot) {
    # Engine runtime files (into Runtime\, skip .pdb)
    foreach ($file in (Get-ChildItem "$EnginePublish" -File)) {
        if ($file.Extension -eq '.pdb') { continue }
        Copy-Item $file.FullName "$StagingDir\Runtime\$($file.Name)" -Force
    }

    # Loader — the DLL the launcher injects (EngineInjectionService.EngineDllName); it
    # maps RynthCore.Engine.dll and provides hot-reload. Without it a fresh install's
    # launcher can't auto-find an engine to inject.
    $loaderDll = "$LoaderPublish\RynthCore.Loader.dll"
    if (-not (Test-Path $loaderDll)) { throw "Loader DLL not found at: $loaderDll" }
    Copy-Item $loaderDll "$StagingDir\Runtime\" -Force
} else {
    # The unloadable engine: native loader (same RynthCoreInit contract with the launcher's
    # injector), RynthCore.Shim, the CoreCLR engine (ReadyToRun) + its dependencies, and
    # Runtime\dotnet (the app-local x86 .NET runtime). Needs Visual Studio's C++ tools (cl.exe)
    # for the loader, like the SEH trampoline.
    Write-Host ""
    Write-Host "[2+3/5] Building the unloadable engine + native loader into staging..." -ForegroundColor Cyan
    & "$RepoRoot\scripts\Publish-EngineCoreClr.ps1" -Target "$StagingDir\Runtime" -Configuration $Configuration -MsBuildArgs $VersionArgs
    # Keep the engine's pdb (its stack traces get line numbers); drop the loader's and shim's.
    Remove-Item "$StagingDir\Runtime\RynthCore.Loader.pdb", "$StagingDir\Runtime\RynthCore.Shim.pdb" -Force -ErrorAction SilentlyContinue
}

# SEH trampoline — hand-built native DLL (native\SehTrampoline\Build-SehTrampoline.ps1),
# NOT part of the engine publish output. Without it SehTrampoline.IsAvailable stays
# false and CombatActionHooks.CastSpell fails closed on every targeted cast, which
# includes self-buffs (the plugin passes the player id): the bot says "Casting: X"
# and nothing happens. Same missing/stale checks as scripts\Deploy-RynthCore.ps1.
$sehDll = "$RepoRoot\native\SehTrampoline\bin\RynthCore.SehTrampoline.dll"
$sehSrc = "$RepoRoot\native\SehTrampoline\SehTrampoline.c"
if (-not (Test-Path $sehDll)) { throw "SEH trampoline not found at: $sehDll`nBuild it with native\SehTrampoline\Build-SehTrampoline.ps1" }
if ((Test-Path $sehSrc) -and (Get-Item $sehSrc).LastWriteTime -gt (Get-Item $sehDll).LastWriteTime) {
    throw "RynthCore.SehTrampoline.dll is STALE (SehTrampoline.c is newer). Rebuild with native\SehTrampoline\Build-SehTrampoline.ps1"
}
Copy-Item $sehDll "$StagingDir\Runtime\" -Force

# Engine Native subfolder (NativeAOT layout)
if ($NativeAot -and (Test-Path "$EnginePublish\Native")) {
    foreach ($file in (Get-ChildItem "$EnginePublish\Native" -File)) {
        if ($file.Extension -eq '.pdb') { continue }
        Copy-Item $file.FullName "$StagingDir\Runtime\Native\$($file.Name)" -Force
    }
}

# Plugin DLL only — staged separately so the .iss file can install it to an
# absolute path outside {app}. The engine no longer auto-scans Runtime\Plugins\;
# the user explicitly adds the plugin path via the launcher's Plugins tab.
$pluginDll = if ($RynthAiDll) { $RynthAiDll } else { "$PluginPublish\RynthCore.Plugin.RynthAi.dll" }
if (-not (Test-Path $pluginDll)) { throw "Plugin DLL not found at: $pluginDll" }
Copy-Item $pluginDll "$pluginStagingDir\RynthAi\" -Force

# The managed plugins' common dependencies (engine API 88: ManagedPlugins looks in Runtime\PluginShared).
if ($PluginSharedDir) {
    if (-not (Test-Path $PluginSharedDir)) { throw "PluginShared folder not found at: $PluginSharedDir" }
    New-Item -ItemType Directory -Path "$StagingDir\Runtime\PluginShared" -Force | Out-Null
    Copy-Item "$PluginSharedDir\*" "$StagingDir\Runtime\PluginShared\" -Force
    Write-Host "  Runtime\PluginShared: $((Get-ChildItem "$StagingDir\Runtime\PluginShared" -File).Count) file(s)"
}

# Loot Editor (self-contained — copy everything from publish, drop pdbs)
if (-not (Test-Path $LootEditorPublish)) { throw "Loot Editor publish not found at: $LootEditorPublish" }
foreach ($file in (Get-ChildItem "$LootEditorPublish" -File)) {
    if ($file.Extension -eq '.pdb') { continue }
    Copy-Item $file.FullName "$StagingDir\Tools\LootEditor\$($file.Name)" -Force
}
foreach ($dir in (Get-ChildItem "$LootEditorPublish" -Directory)) {
    Copy-Item $dir.FullName "$StagingDir\Tools\LootEditor\" -Recurse -Force
    # Drop pdbs from copied subdirs
    Get-ChildItem "$StagingDir\Tools\LootEditor\$($dir.Name)" -Recurse -File -Filter '*.pdb' | Remove-Item -Force
}

# Decal bridge (docs/DECAL_BRIDGE_PLAN.md, experimental): the net48 x86 Decal network filter
# goes to {app}\DecalBridge\, the folder the launcher registers for "Decal + RynthCore"
# accounts (and only for those). Built against Decal's public reference assemblies, so the
# build machine needs Decal; -p:RequireDecal=true turns "no Decal" into a build error rather
# than the empty assembly a plain solution build makes. Never ships any Decal file.
if ($SkipDecalBridge) {
    Write-Warning "-SkipDecalBridge: this build has no Decal bridge; 'Decal + RynthCore' accounts can't launch with it. Not for a release."
} else {
    Write-Host ""
    Write-Host "Building the Decal bridge (net48 x86, against Decal's reference assemblies)..." -ForegroundColor Cyan
    $bridgeProject = "$RepoRoot\src\RynthCore.DecalBridge\RynthCore.DecalBridge.csproj"
    $bridgeDll = "$RepoRoot\src\RynthCore.DecalBridge\bin\$Configuration\RynthCore.DecalBridge.dll"
    $decalArgs = @("-p:RequireDecal=true")
    if ($DecalDir) { $decalArgs += "-p:DecalDir=$DecalDir" }
    # A DLL left over from an earlier build must never be shipped in place of this one.
    Remove-Item $bridgeDll -Force -ErrorAction SilentlyContinue
    if (Test-Path $bridgeDll) { throw "Could not remove the old $bridgeDll (in use by a Decal client?)" }
    dotnet build $bridgeProject -c $Configuration -nologo -v q @decalArgs @VersionArgs
    if ($LASTEXITCODE -ne 0) { throw "Decal bridge build failed (exit $LASTEXITCODE). Is Decal 3.0 installed? (-DecalDir for another folder, -SkipDecalBridge for a dev build)" }
    if (-not (Test-Path $bridgeDll)) { throw "Decal bridge not built at $bridgeDll" }
    # The real bridge has its filter type; the no-Decal build is an empty assembly.
    $bridgeBytes = [System.IO.File]::ReadAllBytes($bridgeDll)
    if ([System.Text.Encoding]::ASCII.GetString($bridgeBytes).IndexOf("BridgeFilter") -lt 0) {
        throw "$bridgeDll has no BridgeFilter type - it was built without Decal's reference assemblies."
    }
    New-Item -ItemType Directory -Path "$StagingDir\DecalBridge" -Force | Out-Null
    Copy-Item $bridgeDll "$StagingDir\DecalBridge\" -Force
    $staleDecal = @(Get-ChildItem "$StagingDir\DecalBridge" -File | Where-Object { $_.Name -ne "RynthCore.DecalBridge.dll" })
    if ($staleDecal.Count -gt 0) { throw "Unexpected files in the staged DecalBridge folder: $($staleDecal.Name -join ', ')" }
    Write-Host "  RynthCore.DecalBridge.dll $((Get-Item $bridgeDll).VersionInfo.FileVersion)"
}

# release.txt — which core release this install is ($release, from -Version at the top).
Set-Content -Path "$StagingDir\release.txt" -Value @($release, $Version) -Encoding ASCII

# Everything a working install needs. The 2026-09-25 public installer went out
# without the Loader and the SEH trampoline because nothing checked.
$required = @(
    "$StagingDir\RynthCore.exe",
    "$StagingDir\Runtime\RynthCore.Loader.dll",
    "$StagingDir\Runtime\RynthCore.Engine.dll",
    "$StagingDir\Runtime\RynthCore.SehTrampoline.dll",
    "$StagingDir\Runtime\minhook.x86.dll",
    "$StagingDir\Runtime\cimgui.dll",
    "$pluginStagingDir\RynthAi\RynthCore.Plugin.RynthAi.dll"
)
if (-not $NativeAot) {
    $required += @(
        "$StagingDir\Runtime\RynthCore.Shim.dll",
        "$StagingDir\Runtime\dotnet\coreclr.dll",
        "$StagingDir\Runtime\dotnet\System.Private.CoreLib.dll"
    )
}
if (-not $SkipDecalBridge) {
    $required += "$StagingDir\DecalBridge\RynthCore.DecalBridge.dll"
}
$missing = @($required | Where-Object { -not (Test-Path $_) })
if ($missing.Count -gt 0) { throw "Staging is missing required files:`n  $($missing -join "`n  ")" }

# Report staged sizes
$engineDll = "$StagingDir\Runtime\RynthCore.Engine.dll"
if (Test-Path $engineDll) {
    $sizeMb = [math]::Round((Get-Item $engineDll).Length / 1MB, 1)
    Write-Host "  Engine.dll: ${sizeMb} MB"
}
$totalMb = [math]::Round((Get-ChildItem "$StagingDir" -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "  Total staged: ${totalMb} MB"

Write-Host "Staging complete: $StagingDir" -ForegroundColor Green

# No file we ship may carry the ReadOnly attribute (a read-only source - a git checkout on a
# read-only share, a file copied off an ISO - would otherwise land read-only on the player's PC,
# where the launcher's updater and RynthAi can't overwrite it). 2026-10-13.
$readOnly = @(Get-ChildItem "$ScriptDir\staging" -Recurse -File -Force | Where-Object { $_.IsReadOnly })
foreach ($f in $readOnly) { $f.IsReadOnly = $false }
if ($readOnly.Count -gt 0) { Write-Host "  Cleared read-only on $($readOnly.Count) staged file(s)." -ForegroundColor Yellow }

# ── Inno Setup ───────────────────────────────────────────────────────────────
# Auto-detect ISCC if not specified: try standard install location then user-local.
if (-not $IsccPath) {
    $candidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { $IsccPath = $c; break } }
}
if (-not $IsccPath -or -not (Test-Path $IsccPath)) {
    Write-Host ""
    Write-Warning "Inno Setup compiler not found. Install Inno Setup 6 from: https://jrsoftware.org/isdl.php"
    Write-Warning "Or pass -IsccPath to this script explicitly."
    exit 0
}

Write-Host ""
Write-Host "Building installer..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path "$ScriptDir\Output" -Force | Out-Null

# Substitute version into a temp copy of the .iss file so we never pass
# /D flags to ISPP (the preprocessor chokes on them in some Inno Setup 6
# builds when the .iss file has specific content patterns).
$issSource = "$ScriptDir\RynthCore.iss"
# Write temp file alongside the original so all relative paths (SetupIconFile,
# Source, staging\) still resolve from the installer\ directory.
$issTmp    = "$ScriptDir\RynthCore.tmp.iss"
$issContent = Get-Content $issSource -Raw
if ($Version) {
    $issContent = $issContent -replace 'AppVersion=0\.0\.0', "AppVersion=$Version"
    Write-Host "  Version override: $Version"
}
Set-Content -Path $issTmp -Value $issContent -Encoding UTF8

try {
    & $IsccPath $issTmp
    if ($LASTEXITCODE -ne 0) { throw "ISCC build failed (exit $LASTEXITCODE)" }
} finally {
    Remove-Item $issTmp -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "SUCCESS" -ForegroundColor Green
Write-Host "Installer: $ScriptDir\Output\RynthCore-Setup.exe"
