# Test-ClrReload.ps1 - hot-reload test for the CoreCLR-hosted engine in a throwaway client.
#
# Starts acclient from the private copy against -Server (default: a closed local port, so
# nothing logs in), with its own engine.json (-Settings), reloads it -Reloads times,
# and after each reload prints the loader/shim lines. If a generation did not unload,
# runs tools\AlcRoots against the client to show what still holds it. Kills the client at
# the end unless -Keep.
param(
    [string]$Runtime = "C:\Games\RynthCore\Runtime",
    [string]$Settings = "C:\Games\RynthCore\Test\engine-noplugins.json",
    [int]$Reloads = 1,
    [int]$WarmupSeconds = 20,
    [int]$SettleSeconds = 15,
    [string[]]$ClientArgs = @("-h", "127.0.0.1", "-p", "9", "-a", "spike", "-v", "spike"),
    [switch]$Keep,
    [switch]$AlwaysRoots
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$testClient = "$repo\tools\TestClient\bin\TestClient.exe"
$alcRoots = "$repo\tools\AlcRoots\bin\Release\net10.0\win-x86\AlcRoots.exe"

# The loader refuses hot reloads past engine.json MaxHotReloads (default 20 since 2026-10-15): a soak
# of more reloads needs "MaxHotReloads": -1 in the -Settings file.
if ($Reloads -gt 20 -and -not ((Test-Path $Settings) -and (Select-String -Path $Settings -Pattern '"MaxHotReloads"' -Quiet))) {
    Write-Warning "-Reloads $Reloads is over the default cap of 20 hot reloads per client: add ""MaxHotReloads"": -1 to $Settings, or reloads past the 20th are refused."
}
$env:RYNTHCORE_ENGINE_SETTINGS = $Settings
$env:RYNTHCORE_TEST_CLIENT = "Test-ClrReload"   # the loader logs "TestClient: ..." (monitors skip test clients)
# Panel layout (panel_state.txt) lives in %LOCALAPPDATA%\RynthCore and is shared with real
# clients: a test client gets its own so it never moves the player's bar or panels.
$env:LOCALAPPDATA = "C:\Games\RynthCore\Test\localappdata"
New-Item -ItemType Directory -Force $env:LOCALAPPDATA | Out-Null
$out = & $testClient "C:\Games\RynthCore\AcClient\acclient.exe" "$Runtime\RynthCore.Loader.dll" RynthCoreInit 0 @ClientArgs
$clientPid = [int](($out | Select-String "TEST_PID=(\d+)").Matches[0].Groups[1].Value)
$log = "C:\Games\RynthCore\Logs\RynthCore.$clientPid.log"
Write-Host "client pid $clientPid, log $log"
Start-Sleep $WarmupSeconds

$seen = 0
function Show-NewLoaderLines {
    $lines = @(Get-Content $log | Select-String "\[loader\]|\[ERR\]|\[WRN\]")
    $lines[$script:seen..($lines.Count - 1)] | ForEach-Object { $_.Line }
    $script:seen = $lines.Count
}
Show-NewLoaderLines

for ($i = 1; $i -le $Reloads; $i++) {
    if (-not (Get-Process -Id $clientPid -ErrorAction SilentlyContinue)) { Write-Host "client died"; break }
    Write-Host "---- reload $i"
    $ev = [System.Threading.EventWaitHandle]::OpenExisting("Local\RynthCore.Engine.RequestReload.p$clientPid")
    [void]$ev.Set()
    $ev.Dispose()
    Start-Sleep $SettleSeconds
    Show-NewLoaderLines
    $notFreed = Select-String -Path $log -Pattern "NOT unloaded" -Quiet
    if ($AlwaysRoots -or ($notFreed -and $i -eq 1)) {
        Write-Host "---- AlcRoots"
        & $alcRoots $clientPid 30
    }
}

if (-not $Keep) {
    Stop-Process -Id $clientPid -Force -ErrorAction SilentlyContinue
    Write-Host "client $clientPid stopped"
}
