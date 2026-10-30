# Publish-PluginsCoreClr.ps1
# Builds RynthSuite plugins as MANAGED assemblies for the CoreCLR engine (plan Phase 4): the
# same source as the NativeAOT build, with PublishAot off (command-line properties override
# the csproj). Each plugin lands in its own folder with its dependencies (RynthCore.PluginSdk,
# PluginCore, MoonSharp, ...), which the engine loads into the plugin's collectible context.
#
# For a test build into a separate folder; scripts\Deploy-RynthCore.ps1 is what deploys the
# managed plugins to C:\Games\RynthSuite. The plugins reference ..\..\..\RynthCore\src\... (the
# SDK), so they build against whatever RynthCore checkout sits next to -RynthSuite.
#   .\scripts\Publish-PluginsCoreClr.ps1 [-Plugins RynthAi,RynthChat] [-Out <dir>]
param(
    [string]$RynthSuite = "C:\Projects\RynthSuite",
    [string]$Out = "C:\Games\RynthCore\Test\plugins-clr",
    [string[]]$Plugins = @("RynthAi", "RynthChat", "RynthNav", "RynthTracker", "RynthLua", "RynthVision")
)

$ErrorActionPreference = "Stop"
foreach ($name in $Plugins) {
    $proj = Join-Path $RynthSuite "Plugins\RynthCore.Plugin.$name\RynthCore.Plugin.$name.csproj"
    if (-not (Test-Path $proj)) { Write-Warning "${name}: no project at $proj"; continue }
    $dest = Join-Path $Out $name
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Write-Host "Building $name (managed)..."
    dotnet publish $proj -c Release -p:PublishAot=false -p:NativeLib= -p:SelfContained=false `
        -p:RuntimeIdentifier=win-x86 -p:GenerateRuntimeConfigurationFiles=false -o $dest -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "$name build failed" }
    Write-Host "  -> $dest\RynthCore.Plugin.$name.dll"
}
