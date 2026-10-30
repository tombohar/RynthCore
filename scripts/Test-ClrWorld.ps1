# Test-ClrWorld.ps1 - logs a test character into a LOCAL server with the CoreCLR engine and
# hot-reloads it N times, reporting per generation: unloaded or not, pattern misses, hangs,
# and the frame rate afterwards. Runs tools\AlcRoots when a generation doesn't unload.
#
# Uses the injector's headless --launch with a saved account profile (the password stays in
# the launcher's settings). Only point it at a server you run yourself.
#   .\scripts\Test-ClrWorld.ps1 -Account Buffi -Server ACEmulator -Character "+Buffi" -Reloads 5
param(
    [string]$Account = "Buffi",
    [string]$Server = "ACEmulator",
    [string]$Character = "+Buffi",
    [string]$Runtime = "C:\Games\RynthCore\Runtime",
    [string]$Settings = "C:\Games\RynthCore\Test\engine-plugins.json",
    [int]$Reloads = 3,
    [int]$WarmupSeconds = 45,
    [int]$SettleSeconds = 25,
    # Chat commands to run after login and again after every reload (through the test
    # client's own dispatch file), e.g. "/rc ui popout Radar".
    [string[]]$Commands = @(),
    [switch]$Keep
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$injector = "$repo\out\injector\RynthCore.Injector.exe"
if (-not (Test-Path $injector)) {
    dotnet publish "$repo\src\RynthCore.Injector\RynthCore.Injector.csproj" -c Release -o "$repo\out\injector" -nologo -v q | Out-Null
    if (-not (Test-Path $injector)) { throw "could not build the injector" }
}
$alcRoots = "$repo\tools\AlcRoots\bin\Release\net10.0\win-x86\AlcRoots.exe"
$pidFile = "C:\Games\RynthCore\Test\current-pid.txt"

# A client killed without logging off keeps its session on the server for about a minute.
if (Test-Path $pidFile) {
    $old = [int](Get-Content $pidFile)
    if (Get-Process -Id $old -ErrorAction SilentlyContinue) {
        Stop-Process -Id $old -Force
        Write-Host "stopped previous test client $old; waiting 65 s for the server to drop its session"
        Start-Sleep 65
    }
}

# The loader refuses hot reloads past engine.json MaxHotReloads (default 20 since 2026-10-15): a soak
# of more reloads needs "MaxHotReloads": -1 in the -Settings file.
if ($Reloads -gt 20 -and -not ((Test-Path $Settings) -and (Select-String -Path $Settings -Pattern '"MaxHotReloads"' -Quiet))) {
    Write-Warning "-Reloads $Reloads is over the default cap of 20 hot reloads per client: add ""MaxHotReloads"": -1 to $Settings, or reloads past the 20th are refused."
}
$env:RYNTHCORE_ENGINE_SETTINGS = $Settings
$env:RYNTHCORE_TEST_CLIENT = "Test-ClrWorld"   # the loader logs "TestClient: ..." (monitors skip test clients)
# Panel layout (panel_state.txt) lives in %LOCALAPPDATA%\RynthCore and is shared with real
# clients: a test client gets its own so it never moves the player's bar or panels.
$env:LOCALAPPDATA = "C:\Games\RynthCore\Test\localappdata"
New-Item -ItemType Directory -Force $env:LOCALAPPDATA | Out-Null
# The engine finds %LOCALAPPDATA% through the known-folder API, which ignores the variable above:
# the hotkeys (shared by every client, and changed by the item bar's keys) get their own file here.
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\RynthCore" | Out-Null
$env:RYNTHCORE_HOTKEYS_FILE = "$env:LOCALAPPDATA\RynthCore\hotkeys.txt"
$dispatch = "C:\Games\RynthCore\Test\dispatch.txt"
$env:RYNTHCORE_DISPATCH_FILE = $dispatch
Set-Content $dispatch "# test dispatch file"
function Send-Commands {
    foreach ($c in $Commands) {
        Set-Content $dispatch "force:$c"
        Start-Sleep 2
    }
}
$out = & $injector --launch --account $Account --server $Server --engine "$Runtime\RynthCore.Loader.dll" --character $Character 2>&1
$clientPid = [int](($out | Select-String "LAUNCHED_PID=(\d+)").Matches[0].Groups[1].Value)
Set-Content $pidFile $clientPid
$log = "C:\Games\RynthCore\Logs\RynthCore.$clientPid.log"
Write-Host "client $clientPid, log $log"
Start-Sleep $WarmupSeconds

function Summarize([int]$from) {
    $lines = @(Get-Content $log | Select-Object -Skip $from)
    $lines | Select-String "unloaded in|NOT unloaded|unloaded since|Reload: |PATTERN MISS|HANG DETECTED|prologue not found|\[ERR\]|still running|ChatFileDispatcher: dispatching|PopOut|pop-out" |
        ForEach-Object { Write-Host ("  " + ($_.Line -replace '^\[(\S+)\] \[pid:\d+\] ', '$1 ')) }
    $fps = $lines | Select-String "hb #\d+ .*fps=(\d+).*login=(\d)" | Select-Object -Last 8 |
        ForEach-Object { $_.Matches[0].Groups[1].Value }
    Write-Host "  fps (last 8 heartbeats): $($fps -join ' ')"
    return (Get-Content $log).Count
}

Send-Commands
Start-Sleep 5
$mark = 0
Write-Host "---- gen 1"
$mark = Summarize $mark
for ($i = 1; $i -le $Reloads; $i++) {
    if (-not (Get-Process -Id $clientPid -ErrorAction SilentlyContinue)) { Write-Host "CLIENT DIED"; break }
    Write-Host "---- reload $i"
    $ev = [System.Threading.EventWaitHandle]::OpenExisting("Local\RynthCore.Engine.RequestReload.p$clientPid")
    [void]$ev.Set(); $ev.Dispose()
    Start-Sleep $SettleSeconds
    Send-Commands
    Start-Sleep 5
    $before = $mark
    $mark = Summarize $mark
    if (Get-Content $log | Select-Object -Skip $before | Select-String "NOT unloaded" -Quiet) {
        Write-Host "---- AlcRoots"
        & $alcRoots $clientPid 15
    }
}
$p = Get-Process -Id $clientPid -ErrorAction SilentlyContinue
Write-Host ("client " + $(if ($p) { "alive, responding=$($p.Responding)" } else { "DEAD" }))
if (-not $Keep -and $p) { Stop-Process -Id $clientPid -Force; Write-Host "client stopped" }
