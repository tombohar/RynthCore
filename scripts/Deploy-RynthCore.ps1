param(
    [string]$Destination = "C:\Games\RynthCore",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$launcherProject = Join-Path $repoRoot "src\RynthCore.App.Avalonia\RynthCore.App.Avalonia.csproj"
$engineProject = Join-Path $repoRoot "src\RynthCore.Engine\RynthCore.Engine.csproj"
$pluginProjects = @(
    @{
        Project = Join-Path $repoRoot "Plugins\RynthCore.Plugin.HelloBox\RynthCore.Plugin.HelloBox.csproj"
        Publish = Join-Path $repoRoot "Plugins\RynthCore.Plugin.HelloBox\bin\Release\net9.0-windows\win-x86\publish"
        DllName = "RynthCore.Plugin.HelloBox.dll"
    },
    @{
        Project = Join-Path $repoRoot "Plugins\RynthCore.Plugin.RynthAi\RynthCore.Plugin.RynthAi.csproj"
        Publish = Join-Path $repoRoot "Plugins\RynthCore.Plugin.RynthAi\bin\Release\net9.0-windows\win-x86\publish"
        DllName = "RynthCore.Plugin.RynthAi.dll"
    }
)

$launcherPublish = Join-Path $repoRoot "src\RynthCore.App.Avalonia\bin\Release\net9.0-windows7.0\win-x86\publish"
$enginePublish = Join-Path $repoRoot "src\RynthCore.Engine\bin\Release\net9.0-windows\win-x86\publish"

$runtimeDir = Join-Path $Destination "Runtime"
$runtimePluginsDir = Join-Path $runtimeDir "Plugins"

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
New-Item -ItemType Directory -Path $runtimePluginsDir -Force | Out-Null

Copy-FilteredChildren -Source $launcherPublish -Target $Destination -ExcludeNames @("RynthCore.App.Avalonia.exe") -ExcludeExtensions @(".pdb")
Copy-FilteredChildren -Source $enginePublish -Target $runtimeDir -ExcludeExtensions @(".pdb")
foreach ($plugin in $pluginProjects) {
    Copy-FilteredChildren -Source $plugin.Publish -Target $runtimePluginsDir -ExcludeNames @("cimgui.dll") -ExcludeExtensions @(".pdb")
}

Copy-Item -LiteralPath (Join-Path $launcherPublish "RynthCore.App.Avalonia.exe") -Destination (Join-Path $Destination "RynthCore.exe") -Force
foreach ($plugin in $pluginProjects) {
    Copy-Item -LiteralPath (Join-Path $plugin.Publish $plugin.DllName) -Destination (Join-Path $runtimePluginsDir $plugin.DllName) -Force
}
Get-ChildItem -Path $Destination -Recurse -Filter *.pdb -File | Remove-Item -Force

Write-Host "Launcher deployed to $Destination"
Write-Host "Engine runtime deployed to $runtimeDir"
