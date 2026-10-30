# Start-DecalTestClient.ps1 - Decal-bridge spike: start ONE test client with Decal and/or
# the CoreCLR RynthCore engine from a TEST runtime folder, against Tom's local server.
#
#   -Mode DecalOnly   Decal alone (Inject.dll + DecalStartup)          - baseline
#   -Mode EngineOnly  RynthCore alone                                  - baseline
#   -Mode Both        Decal first, then RynthCore (the coexistence case)
#
# Same isolation as scripts\Test-ClrWorld.ps1: its own engine settings, LOCALAPPDATA and
# dispatch file; RYNTHCORE_TEST_CLIENT marks it. -Bridge sets the engine's bridge mode for this
# client only (RYNTHCORE_DECAL_BRIDGE: Auto = unset, the engine.json default; On = 1; Off = 0,
# which also keeps the bridge filter itself idle); -InGameUi does the same for DecalInGameImGui.
# The bridge arms by itself when the RynthCore loader is in the client, so it must be registered
# (RynthCore.Injector --decal-bridge register).
# Never point -Runtime at C:\Games\RynthCore\Runtime (the live runtime every client shares).
param(
    [ValidateSet("DecalOnly", "EngineOnly", "Both")] [string]$Mode = "Both",
    [string]$Account = "Buffi",
    [string]$Server = "ACEmulator",
    [string]$Character = "+Buffi",
    [string]$Runtime = "C:\Games\RynthCore\Test\rt-decal",
    [string]$Settings = "C:\Games\RynthCore\Test\engine-decal.json",
    # Override the server profile's host/port for this launch (the profile is not changed).
    [string]$HostName = "",
    [int]$Port = 0,
    [ValidateSet("Auto", "On", "Off")] [string]$Bridge = "Auto",
    [switch]$NoBridge,   # = -Bridge Off
    # In-game ImGui under Decal (engine.json DecalInGameImGui, default on): Default leaves
    # RYNTHCORE_DECAL_IMGUI unset; On = 1; Off = 0 (the old coexistence path, no D3D9).
    [ValidateSet("Default", "On", "Off")] [string]$InGameUi = "Default"
)
$ErrorActionPreference = "Stop"
if ([IO.Path]::GetFullPath($Runtime).TrimEnd('\') -ieq "C:\Games\RynthCore\Runtime") { throw "refusing the live runtime" }
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$injector = "$repo\out\injector-spike\RynthCore.Injector.exe"
if (-not (Test-Path $injector)) {
    dotnet publish "$repo\src\RynthCore.Injector\RynthCore.Injector.csproj" -c Release -o "$repo\out\injector-spike" -nologo -v q | Out-Null
}
$env:RYNTHCORE_ENGINE_SETTINGS = $Settings
$env:RYNTHCORE_TEST_CLIENT = "DecalBridgeSpike"
$env:LOCALAPPDATA = "C:\Games\RynthCore\Test\localappdata"
New-Item -ItemType Directory -Force $env:LOCALAPPDATA | Out-Null
$env:RYNTHCORE_DISPATCH_FILE = "C:\Games\RynthCore\Test\dispatch-decal.txt"
Set-Content $env:RYNTHCORE_DISPATCH_FILE "# decal spike dispatch file"
# Set or clear both every time: a script run in the same session must not inherit the last run's.
if ($NoBridge) { $Bridge = "Off" }
$env:RYNTHCORE_DECAL_BRIDGE = switch ($Bridge) { "On" { "1" } "Off" { "0" } default { $null } }
$env:RYNTHCORE_DECAL_IMGUI = switch ($InGameUi) { "On" { "1" } "Off" { "0" } default { $null } }

$launchArgs = @("--launch", "--account", $Account, "--server", $Server, "--engine", "$Runtime\RynthCore.Loader.dll", "--character", $Character)
if ($Mode -eq "DecalOnly") { $launchArgs += "--decal-only" } elseif ($Mode -eq "Both") { $launchArgs += "--decal" }
if ($HostName) { $launchArgs += @("--host", $HostName) }
if ($Port -gt 0) { $launchArgs += @("--port", "$Port") }
$out = & $injector $launchArgs 2>&1
$out | ForEach-Object { Write-Host "  $_" }
$m = $out | Select-String "LAUNCHED_PID=(\d+)"
if (-not $m) { throw "launch failed" }
$clientPid = [int]$m.Matches[0].Groups[1].Value
Add-Content "C:\Games\RynthCore\Test\decal-spike-pids.txt" "$(Get-Date -Format s) $Mode $clientPid"
Write-Host "TEST_PID=$clientPid"
