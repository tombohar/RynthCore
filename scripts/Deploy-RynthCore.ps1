param(
    [string]$Destination = "C:\Games\RynthCore",
    [string]$PluginsDestination = "C:\Games\RynthSuite",
    [switch]$SkipPublish
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
# RynthAi's live source lives in a separate repo at C:\Projects\RynthSuite —
# the rynthcore\Plugins\RynthCore.Plugin.RynthAi tree is a stub (~1 MB
# published) that lacks Combat / Loot / Meta / Raycasting / LegacyUi.
# Always source from the RynthSuite tree so the real ~8 MB plugin ships.
$rynthAiSourceRoot = "C:\Projects\RynthSuite\Plugins\RynthCore.Plugin.RynthAi"
$pluginProjects = @(
    @{
        Project    = Join-Path $rynthAiSourceRoot "RynthCore.Plugin.RynthAi.csproj"
        Publish    = Join-Path $rynthAiSourceRoot "bin\Release\net10.0-windows\win-x86\publish"
        DllName    = "RynthCore.Plugin.RynthAi.dll"
        DestSubdir = "RynthAi"
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

if (-not $SkipPublish) {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet-home-deploy-clean"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

    dotnet publish $launcherProject -c Release -r win-x86
    dotnet publish $engineProject -c Release
    dotnet publish $loaderProject -c Release
    foreach ($plugin in $pluginProjects) {
        dotnet publish $plugin.Project -c Release
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

foreach ($name in $rootCleanup) {
    $target = Join-Path $Destination $name
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

if (Test-Path -LiteralPath $runtimeDir) {
    Remove-Item -LiteralPath $runtimeDir -Recurse -Force
}

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null

# Launcher payload to root, except the bootstrapper exe (renamed below).
Copy-FilteredChildren -Source $launcherPublish -Target $Destination -ExcludeNames @("RynthCore.App.Avalonia.exe") -ExcludeExtensions @(".pdb")

# Engine payload to Runtime\. Skip the bundled Plugins\ subfolder — plugins
# are deployed separately to $PluginsDestination so they have a single home.
Copy-FilteredChildren -Source $enginePublish -Target $runtimeDir -ExcludeNames @("Plugins") -ExcludeExtensions @(".pdb")

# Loader DLL — this is what RynthCore.Injector loads into acclient.exe; the
# Loader then maps RynthCore.Engine.dll and provides hot-reload support.
Copy-Item -LiteralPath (Join-Path $loaderPublish "RynthCore.Loader.dll") -Destination (Join-Path $runtimeDir "RynthCore.Loader.dll") -Force

Copy-Item -LiteralPath (Join-Path $launcherPublish "RynthCore.App.Avalonia.exe") -Destination (Join-Path $Destination "RynthCore.exe") -Force

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

    # Only the DLL — leave any user data (LootProfiles\, imgui.ini, etc.)
    # in place by not touching anything else under $pluginTargetDir.
    Copy-Item -LiteralPath $pluginSrc -Destination (Join-Path $pluginTargetDir $plugin.DllName) -Force
    Write-Host "Plugin $($plugin.DllName) deployed to $pluginTargetDir"
}

Get-ChildItem -Path $Destination -Recurse -Filter *.pdb -File | Remove-Item -Force

Write-Host "Launcher deployed to $Destination"
Write-Host "Engine runtime deployed to $runtimeDir"
