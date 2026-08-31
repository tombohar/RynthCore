param(
    [string]$Destination = "C:\Games\RynthCore",
    [string]$PluginsDestination = "C:\Games\RynthSuite",
    [switch]$SkipPublish,
    # When set, skip both the launcher publish AND the launcher payload copy
    # to $Destination root. Use this for engine-only / plugin-only iteration
    # while the launcher (RynthCore.exe) is running — without it, the launcher's
    # held Avalonia.Base.dll causes a Copy-Item lock failure that wipes the
    # Runtime\ folder. Engine still publishes/deploys to Runtime\ and plugins
    # still deploy under $PluginsDestination.
    [switch]$SkipLauncher,
    # Pre-deploy pattern gate (tools\check-patterns.ps1): verify every acclient.exe
    # signature embedded in the engine source still resolves uniquely + correctly
    # against the live client BEFORE shipping. -SkipPatternCheck bypasses it;
    # -AcClient overrides the binary it checks (default: auto-detect the private
    # copy under $Destination\AcClient, else C:\Turbine\Asheron's Call).
    [switch]$SkipPatternCheck,
    [string]$AcClient = "",
    # Root of the sibling RynthSuite checkout that holds the real plugin
    # sources. Left empty, it auto-resolves: a RynthSuite folder next to this
    # repo first, then the legacy C:\Projects\RynthSuite. Pass it explicitly to
    # deploy from a checkout in some other location.
    [string]$RynthSuiteRoot = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$launcherProject = Join-Path $repoRoot "src\RynthCore.App.Avalonia\RynthCore.App.Avalonia.csproj"
$engineProject = Join-Path $repoRoot "src\RynthCore.Engine\RynthCore.Engine.csproj"
$loaderProject = Join-Path $repoRoot "src\RynthCore.Loader\RynthCore.Loader.csproj"

# Plugins are deployed to per-plugin folders under $PluginsDestination
# (e.g. C:\Games\RynthSuite\RynthAi\). Users register plugin DLLs through
# the launcher's "Add Plugin DLL" UI; the engine no longer relies on a
# default Runtime\Plugins\ scan being populated by deploy.
#
# RynthAi's live source lives in the sibling RynthSuite repo - the
# rynthcore\Plugins\RynthCore.Plugin.RynthAi tree is a stub (~1 MB published)
# that lacks Combat / Loot / Meta / Raycasting / LegacyUi. Always source from
# the RynthSuite tree so the real ~8 MB plugin ships.
#
# Resolution order: -RynthSuiteRoot, then a sibling checkout, then the legacy
# C:\Projects\RynthSuite. We hard-fail on a miss rather than falling through to
# the stub, because a silent stub deploy looks like a successful deploy and
# then behaves like a plugin with most of its features mysteriously absent.
if ([string]::IsNullOrWhiteSpace($RynthSuiteRoot)) {
    $candidates = @(
        (Join-Path (Split-Path -Parent $repoRoot) "RynthSuite"),
        "C:\Projects\RynthSuite"
    )
    $RynthSuiteRoot = $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ "Plugins") } | Select-Object -First 1
    if (-not $RynthSuiteRoot) {
        throw @"
Could not locate the RynthSuite checkout that holds the plugin sources.
Looked in:
$($candidates | ForEach-Object { "  $_" } | Out-String)
Clone RynthSuite beside this repo, or pass -RynthSuiteRoot <path>.
"@
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $RynthSuiteRoot "Plugins"))) {
    throw "RynthSuiteRoot '$RynthSuiteRoot' has no Plugins folder - wrong path?"
}
Write-Host "RynthSuite source root: $RynthSuiteRoot"

$pluginSourceRoot      = Join-Path $RynthSuiteRoot "Plugins"
$rynthAiSourceRoot     = Join-Path $pluginSourceRoot "RynthCore.Plugin.RynthAi"
$rynthChatSourceRoot   = Join-Path $pluginSourceRoot "RynthCore.Plugin.RynthChat"
$rynthVisionSourceRoot = Join-Path $pluginSourceRoot "RynthCore.Plugin.RynthVision"
$rynthJuiceSourceRoot  = Join-Path $pluginSourceRoot "RynthCore.Plugin.RynthJuice"
$rynthTrackerSourceRoot = Join-Path $pluginSourceRoot "RynthCore.Plugin.RynthTracker"
$rynthNavSourceRoot    = Join-Path $pluginSourceRoot "RynthCore.Plugin.RynthNav"
$pluginProjects = @(
    @{
        Project    = Join-Path $rynthAiSourceRoot "RynthCore.Plugin.RynthAi.csproj"
        Publish    = Join-Path $rynthAiSourceRoot "bin\Release\net10.0-windows\win-x86\publish"
        DllName    = "RynthCore.Plugin.RynthAi.dll"
        DestSubdir = "RynthAi"
    },
    @{
        Project    = Join-Path $rynthChatSourceRoot "RynthCore.Plugin.RynthChat.csproj"
        Publish    = Join-Path $rynthChatSourceRoot "bin\Release\net10.0-windows\win-x86\publish"
        DllName    = "RynthCore.Plugin.RynthChat.dll"
        DestSubdir = "RynthChat"
    },
    # RynthVision sets <PublishDir> to its deploy home, so publish lands
    # directly in $PluginsDestination\RynthVision. The copy step below detects
    # source == dest and skips the redundant self-copy.
    @{
        Project    = Join-Path $rynthVisionSourceRoot "RynthCore.Plugin.RynthVision.csproj"
        Publish    = Join-Path $PluginsDestination "RynthVision"
        DllName    = "RynthCore.Plugin.RynthVision.dll"
        DestSubdir = "RynthVision"
    },
    @{
        Project    = Join-Path $rynthTrackerSourceRoot "RynthCore.Plugin.RynthTracker.csproj"
        Publish    = Join-Path $rynthTrackerSourceRoot "bin\Release\net10.0-windows\win-x86\publish"
        DllName    = "RynthCore.Plugin.RynthTracker.dll"
        DestSubdir = "RynthTracker"
    },
    # RynthJuice sets <PublishDir> to its deploy home (like RynthVision), so
    # publish lands directly in $PluginsDestination\RynthJuice and the copy step
    # below detects source == dest and skips the redundant self-copy.
    @{
        Project    = Join-Path $rynthJuiceSourceRoot "RynthCore.Plugin.RynthJuice.csproj"
        Publish    = Join-Path $PluginsDestination "RynthJuice"
        DllName    = "RynthCore.Plugin.RynthJuice.dll"
        DestSubdir = "RynthJuice"
    },
    # RynthNav also publishes straight to its deploy home via <PublishDir>.
    # It was missing from this list entirely, so full deploys never refreshed it.
    @{
        Project    = Join-Path $rynthNavSourceRoot "RynthCore.Plugin.RynthNav.csproj"
        Publish    = Join-Path $PluginsDestination "RynthNav"
        DllName    = "RynthCore.Plugin.RynthNav.dll"
        DestSubdir = "RynthNav"
    }
)

$launcherPublish = Join-Path $repoRoot "src\RynthCore.App.Avalonia\bin\Release\net10.0-windows7.0\win-x86\publish"
$enginePublish   = Join-Path $repoRoot "src\RynthCore.Engine\bin\Release\net10.0-windows\win-x86\publish"
$loaderPublish   = Join-Path $repoRoot "src\RynthCore.Loader\bin\Release\net10.0-windows\win-x86\publish"

$runtimeDir = Join-Path $Destination "Runtime"

function Copy-FilteredChildren {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Target,
        [string[]]$ExcludeNames = @(),
        [string[]]$ExcludeExtensions = @()
    )

    Get-ChildItem -LiteralPath $Source -Force | Where-Object {
        $ExcludeNames -notcontains $_.Name -and $ExcludeExtensions -notcontains $_.Extension
    } | Copy-Item -Destination $Target -Recurse -Force
}

# ---------------------------------------------------------------------------
# Pre-deploy pattern gate: verify every acclient.exe signature embedded in the
# engine source still resolves uniquely + correctly against the live client
# binary BEFORE we publish/ship. A future AC patch / ACE rebuild that shifts
# code or a data global fails HERE instead of silently shipping a hook that
# falls back to a stale VA at runtime. (tools\pe_pattern.py CHECK, wrapped by
# tools\check-patterns.ps1.) Bypass: -SkipPatternCheck. Target: -AcClient.
# ---------------------------------------------------------------------------
if (-not $SkipPatternCheck) {
    $checkScript = Join-Path $repoRoot "tools\check-patterns.ps1"
    $acClientPath = $AcClient
    if ([string]::IsNullOrWhiteSpace($acClientPath)) {
        foreach ($cand in @((Join-Path $Destination "AcClient\acclient.exe"), "C:\Turbine\Asheron's Call\acclient.exe")) {
            if (Test-Path -LiteralPath $cand) { $acClientPath = $cand; break }
        }
    }

    # Fail CLOSED: a gate that silently downgrades to a warning when python /
    # the script / the binary is missing is no gate at all — the explicit
    # bypass is -SkipPatternCheck.
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
        throw "Pattern gate cannot run: 'python' is not on PATH. Install Python, or pass -SkipPatternCheck to bypass deliberately."
    }
    elseif (-not (Test-Path -LiteralPath $checkScript)) {
        throw "Pattern gate cannot run: $checkScript not found. Pass -SkipPatternCheck to bypass deliberately."
    }
    elseif ([string]::IsNullOrWhiteSpace($acClientPath) -or -not (Test-Path -LiteralPath $acClientPath)) {
        throw "Pattern gate cannot run: no acclient.exe found (tried '$Destination\AcClient' and 'C:\Turbine\Asheron''s Call'). Pass -AcClient <path>, or -SkipPatternCheck to bypass deliberately."
    }
    else {
        Write-Host "Pattern gate: verifying engine signatures against $acClientPath ..."
        & $checkScript -AcClient $acClientPath
        if ($LASTEXITCODE -ne 0) {
            throw "Pattern gate FAILED (exit $LASTEXITCODE) - aborting deploy. A signature drifted or is wrong; re-cut it with tools\pe_pattern.py (GEN / ALL / DATA), fix the source, rebuild, and retry. Override with -SkipPatternCheck only if you understand the risk."
        }
        Write-Host "Pattern gate PASSED - all engine signatures unique & correct."
    }
}

function Invoke-Publish {
    param(
        [Parameter(Mandatory = $true)][string]$What,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    dotnet @Arguments
    # PS 5.1's $ErrorActionPreference=Stop does NOT throw on a native exe's
    # nonzero exit — without this check a failed publish silently deployed the
    # PREVIOUS publish output (the root staleness trap).
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish FAILED for $What (exit $LASTEXITCODE) - aborting deploy so stale output cannot ship."
    }
}

if (-not $SkipPublish) {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet-home-deploy-clean"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

    if (-not $SkipLauncher) {
        # --self-contained false is required: omitting it with the launcher's IncludeNativeLibrariesForSelfExtract=true produces a broken half-payload (apphost + coreclr.dll, no framework) that reports ".NET is not installed".
        Invoke-Publish -What "launcher" -Arguments @('publish', $launcherProject, '-c', 'Release', '-r', 'win-x86', '--self-contained', 'false')
    }
    Invoke-Publish -What "engine" -Arguments @('publish', $engineProject, '-c', 'Release')
    Invoke-Publish -What "loader" -Arguments @('publish', $loaderProject, '-c', 'Release')
    foreach ($plugin in $pluginProjects) {
        Invoke-Publish -What $plugin.DllName -Arguments @('publish', $plugin.Project, '-c', 'Release')
    }
}

$rootCleanup = @(
    "HelloBoxPublish",
    "Native",
    "NativeAotOut",
    "Plugins",
    "RynthCore.App.exe",
    "RynthCore.App.dll",
    "RynthCore.App.deps.json",
    "RynthCore.App.runtimeconfig.json",
    "RynthCore.App.pdb",
    "RynthCore.App.Avalonia.exe",
    "RynthCore.App.Avalonia.pdb",
    "RynthCore.Engine.dll",
    "RynthCore.Engine.pdb",
    "RynthCore.Injector.exe",
    "RynthCore.Injector.dll",
    "RynthCore.Injector.deps.json",
    "RynthCore.Injector.runtimeconfig.json",
    "RynthCore.Injector.pdb",
    "RynthCore.cimgui.dll",
    "cimgui.dll",
    "minhook.x86.dll",
    "RynthCore.exe.pre-avalonia-redeploy-20260331.bak"
)

if (-not $SkipLauncher) {
    foreach ($name in $rootCleanup) {
        $target = Join-Path $Destination $name
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    }
}

if (Test-Path -LiteralPath $runtimeDir) {
    # Lock pre-flight BEFORE the destructive wipe. A running client holds
    # Runtime\RynthCore.Loader.dll (loaded UNshadowed into acclient.exe) and
    # its .engine_loads shadow DLLs; the old behavior wiped Runtime\ first and
    # then died on the locked file, leaving a half-destroyed deploy. Probe
    # every file for an exclusive open and abort cleanly while the old deploy
    # is still intact.
    $locked = @()
    foreach ($f in Get-ChildItem -LiteralPath $runtimeDir -Recurse -File) {
        try {
            $s = [System.IO.File]::Open($f.FullName, 'Open', 'ReadWrite', 'None')
            $s.Close()
        } catch {
            $locked += $f.FullName
        }
    }
    if ($locked.Count -gt 0) {
        throw ("Deploy aborted BEFORE wiping Runtime\ - $($locked.Count) file(s) are locked by running processes (close all AC clients and the launcher, then retry):`n  " + ($locked -join "`n  "))
    }
    Remove-Item -LiteralPath $runtimeDir -Recurse -Force
}

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null

if (-not $SkipLauncher) {
    # Launcher payload to root, except the bootstrapper exe (renamed below).
    Copy-FilteredChildren -Source $launcherPublish -Target $Destination -ExcludeNames @("RynthCore.App.Avalonia.exe") -ExcludeExtensions @(".pdb")
}

# Engine payload to Runtime\. Skip the bundled Plugins\ subfolder - plugins
# are deployed separately to $PluginsDestination so they have a single home.
Copy-FilteredChildren -Source $enginePublish -Target $runtimeDir -ExcludeNames @("Plugins") -ExcludeExtensions @(".pdb")

# Loader DLL - this is what RynthCore.Injector loads into acclient.exe; the
# Loader then maps RynthCore.Engine.dll and provides hot-reload support.
Copy-Item -LiteralPath (Join-Path $loaderPublish "RynthCore.Loader.dll") -Destination (Join-Path $runtimeDir "RynthCore.Loader.dll") -Force

# SEH trampoline - small native x86 MSVC DLL providing __try/__except wrappers
# around dangerous AC API calls so object-teardown AVs are caught instead of
# crashing acclient.exe. Built by native\SehTrampoline\Build-SehTrampoline.ps1.
$sehTrampolineSrc = Join-Path $repoRoot "native\SehTrampoline\bin\RynthCore.SehTrampoline.dll"
$sehTrampolineC   = Join-Path $repoRoot "native\SehTrampoline\SehTrampoline.c"
if (-not (Test-Path -LiteralPath $sehTrampolineSrc)) {
    # Fail closed: the trampoline is load-bearing (SEH-guarded AC reads) and a
    # missing DLL silently shipped an engine whose guarded calls all throw.
    throw "RynthCore.SehTrampoline.dll not found at $sehTrampolineSrc - build it with native\SehTrampoline\Build-SehTrampoline.ps1, then re-run deploy."
}
if (Test-Path -LiteralPath $sehTrampolineC) {
    # The trampoline is hand-built (cl.exe), not part of dotnet publish — a
    # newer .c than the built DLL means the binary about to ship is stale.
    $srcTime = (Get-Item -LiteralPath $sehTrampolineC).LastWriteTime
    $dllTime = (Get-Item -LiteralPath $sehTrampolineSrc).LastWriteTime
    if ($srcTime -gt $dllTime) {
        throw "RynthCore.SehTrampoline.dll is STALE (SehTrampoline.c edited $srcTime > DLL built $dllTime). Rebuild with native\SehTrampoline\Build-SehTrampoline.ps1, then re-run deploy."
    }
}
Copy-Item -LiteralPath $sehTrampolineSrc -Destination (Join-Path $runtimeDir "RynthCore.SehTrampoline.dll") -Force
Write-Host "SEH trampoline deployed to $runtimeDir"

if (-not $SkipLauncher) {
    Copy-Item -LiteralPath (Join-Path $launcherPublish "RynthCore.App.Avalonia.exe") -Destination (Join-Path $Destination "RynthCore.exe") -Force
}

foreach ($plugin in $pluginProjects) {
    $pluginSrc = Join-Path $plugin.Publish $plugin.DllName
    if (-not (Test-Path -LiteralPath $pluginSrc)) {
        Write-Warning "Plugin DLL not found at $pluginSrc - skipping copy."
        continue
    }

    $pluginTargetDir = Join-Path $PluginsDestination $plugin.DestSubdir
    if (-not (Test-Path -LiteralPath $pluginTargetDir)) {
        New-Item -ItemType Directory -Path $pluginTargetDir -Force | Out-Null
    }

    # Only the DLL - leave any user data (LootProfiles\, imgui.ini, etc.)
    # in place by not touching anything else under $pluginTargetDir.
    $pluginDest = Join-Path $pluginTargetDir $plugin.DllName
    if ([System.IO.Path]::GetFullPath($pluginSrc) -ieq [System.IO.Path]::GetFullPath($pluginDest)) {
        # Plugin published straight into its deploy home (see RynthVision) -
        # nothing to copy, the publish already put the DLL in place.
        Write-Host "Plugin $($plugin.DllName) already in place at $pluginTargetDir; skipping copy."
    } else {
        Copy-Item -LiteralPath $pluginSrc -Destination $pluginDest -Force
        Write-Host "Plugin $($plugin.DllName) deployed to $pluginTargetDir"
    }
}

# Scoped pdb sweep: root + Runtime\ only. A $Destination-wide recurse walked
# the ~1.4 GB private AcClient copy on every deploy for nothing.
Get-ChildItem -Path $Destination -Filter *.pdb -File | Remove-Item -Force
Get-ChildItem -Path $runtimeDir -Recurse -Filter *.pdb -File | Remove-Item -Force

if (-not $SkipLauncher) {
    Write-Host "Launcher deployed to $Destination"
}
Write-Host "Engine runtime deployed to $runtimeDir"
