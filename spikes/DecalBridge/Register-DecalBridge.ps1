# Register-DecalBridge.ps1 - registers (or removes) the RynthCore Decal bridge for this
# Windows user, for TEST clients. A thin wrapper over the real code
# (src\RynthCore.App\DecalBridgeRegistration.cs, the same class the launcher uses for
# "Decal + RynthCore" accounts), reached through RynthCore.Injector --decal-bridge.
#
# What it does (docs\DECAL_BRIDGE_PLAN.md, "Registration"): no admin rights, nothing under
# HKLM. The bridge's entry goes into the per-user registry VirtualStore copy of Decal's
# NetworkFilters key, which the virtualized 32-bit acclient.exe reads instead of HKLM's. If
# that per-user key has to be created, every HKLM filter entry is mirrored into it first
# (otherwise Decal would see only the bridge: no WorldFilter, VTank refuses to start).
# Everything added is recorded in %APPDATA%\RynthCore\decal-bridge-registration.json and
# -Remove takes away exactly that.
#
# The bridge idles in every Decal client of this user until RynthCore is in the same client.
#
#   .\Register-DecalBridge.ps1            build the bridge, copy it to Test\decal-bridge, register
#   .\Register-DecalBridge.ps1 -Status    show what a Decal client would load
#   .\Register-DecalBridge.ps1 -Remove    remove what the registration added
param([switch]$Remove, [switch]$Status)
$ErrorActionPreference = "Stop"
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dir = "C:\Games\RynthCore\Test\decal-bridge"
$injector = "$repo\out\injector-spike\RynthCore.Injector.exe"
dotnet publish "$repo\src\RynthCore.Injector\RynthCore.Injector.csproj" -c Release -o "$repo\out\injector-spike" -nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "injector build failed" }

if ($Status) { & $injector --decal-bridge status; return }
if ($Remove) { & $injector --decal-bridge unregister; return }

dotnet build "$repo\src\RynthCore.DecalBridge\RynthCore.DecalBridge.csproj" -c Release -nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "bridge build failed" }
$built = "$repo\src\RynthCore.DecalBridge\bin\Release\RynthCore.DecalBridge.dll"
if (-not (Test-Path $built)) { throw "bridge not built (is Decal installed?)" }
New-Item -ItemType Directory -Force $dir | Out-Null
# Decal clients that loaded the old copy keep it locked until they exit.
Copy-Item $built $dir -Force
& $injector --decal-bridge register --dir $dir
