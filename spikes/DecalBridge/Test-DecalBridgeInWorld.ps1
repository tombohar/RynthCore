# Test-DecalBridgeInWorld.ps1 - the in-world half of the Decal-bridge spike, in one command.
# Needs Tom's LOCAL server running and the bridge registered (Register-DecalBridge.ps1, or
# RynthCore.Injector --decal-bridge register --dir <folder with RynthCore.DecalBridge.dll>).
# TEST CLIENTS ONLY: it starts its own clients from C:\Games\RynthCore\AcClient and kills only
# the PIDs it started. Three runs, one test client at a time:
#   A  Decal only (opt-in: -Runs A,B,C; nothing auto-selects the character without the
#      engine, so pick it by hand) -> which code Decal + its plugins patch in world
#   B  Decal + RynthCore, bridge (DecalBridge=Auto) -> bridge traffic, a plugin receiving it,
#                                    a TYPED chat-bar command, a line sent to AC through
#                                    Decal (/loc), 2 hot reloads, VTank loaded, the own-device
#                                    ImGui pop-out (snapshot BMP)
#   C  Decal + RynthCore, DecalBridge=Off (the old coexistence mode) -> who owns the contested hooks
#   D  RynthCore only, no Decal (normal hook mode must be unchanged)
# Everything lands in C:\Games\RynthCore\Test\decal-bridge\inworld-<time>\.
#
#   .\spikes\DecalBridge\Test-DecalBridgeInWorld.ps1 -Account Buffi -Server ACEmulator -Character "+Buffi"
param(
    [string]$Account = "Buffi",
    [string]$Server = "ACEmulator",
    [string]$Character = "+Buffi",
    [string]$HostName = "",
    [int]$Port = 0,
    [string[]]$Runs = @("B", "C", "D"),
    [int]$LoginTimeout = 150,
    [string]$Runtime = "C:\Games\RynthCore\Test\rt-decal",
    [string]$Settings = "C:\Games\RynthCore\Test\engine-decal.json",
    [int]$Settle = 30
)
$ErrorActionPreference = "Stop"
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$start = "$PSScriptRoot\Start-DecalTestClient.ps1"
$diff = "$repo\tools\ProcPatchDiff.py"
$out = "C:\Games\RynthCore\Test\decal-bridge\inworld-$(Get-Date -Format yyyyMMdd-HHmmss)"
New-Item -ItemType Directory -Force $out | Out-Null
$report = "$out\report.txt"
function Say([string]$s) { Write-Host $s; Add-Content $report $s }

$checkPort = if ($Port -gt 0) { $Port } else { 9000 }
if (-not (Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object LocalPort -eq $checkPort)) {
    throw "Nothing listens on UDP ${checkPort}: start the local ACE server first."
}
$vs = "HKCU:\Software\Classes\VirtualStore\MACHINE\SOFTWARE\WOW6432Node\Decal\NetworkFilters"
if (-not (Get-ChildItem $vs -ErrorAction SilentlyContinue | Where-Object { (Get-ItemProperty $_.PSPath).Object -eq "RynthCore.DecalBridge.BridgeFilter" })) {
    throw "The bridge is not registered: run Register-DecalBridge.ps1 first."
}

function Start-One([string]$mode, [string[]]$extra) {
    $opts = @{ Mode = $mode; Account = $Account; Server = $Server; Character = $Character; HostName = $HostName; Port = $Port; Runtime = $Runtime; Settings = $Settings }
    foreach ($e in $extra) { $opts[$e] = $true }
    & $start @opts *> $null
    return [int]((Get-Content C:\Games\RynthCore\Test\decal-spike-pids.txt -Tail 1).Split(' ')[2])
}
function Wait-Match([string]$path, [string]$pattern, [int]$seconds) {
    $t0 = Get-Date
    while (((Get-Date) - $t0).TotalSeconds -lt $seconds) {
        if ((Test-Path $path) -and (Select-String -Path $path -Pattern $pattern -Quiet)) { return $true }
        Start-Sleep 2
    }
    return $false
}
function Stop-One([int]$tp) {
    Stop-Process -Id $tp -Force -ErrorAction SilentlyContinue
    Say "stopped $tp; waiting 65 s for the server to drop the session"
    Start-Sleep 65
}
function Grab([string]$path, [string]$pattern, [int]$max = 60) {
    if (Test-Path $path) {
        Select-String -Path $path -Pattern $pattern | Select-Object -Last $max |
            ForEach-Object { Say ("  " + $_.Line.Substring(0, [Math]::Min(300, $_.Line.Length))) }
    }
}
function Send([string]$cmd) {
    # Through the test client's dispatch file -> AC's main thread -> plugins' OnChatBarEnter.
    Set-Content "C:\Games\RynthCore\Test\dispatch-decal.txt" "force:$cmd"
    Start-Sleep 4
}
function Typed([int]$tp, [string]$cmd) {
    # Into the test client's own window (PostMessage by pid): Enter, the text, Enter.
    python "$PSScriptRoot\AcWindow.py" key $tp 0x0D | Out-Null
    Start-Sleep -Milliseconds 400
    python "$PSScriptRoot\AcWindow.py" type $tp $cmd | Out-Null
    python "$PSScriptRoot\AcWindow.py" key $tp 0x0D | Out-Null
    Start-Sleep 4
}
function HasModule([int]$tp, [string]$name) {
    $p = Get-Process -Id $tp -ErrorAction SilentlyContinue
    return [bool]($p -and ($p.Modules | Where-Object { $_.ModuleName -ieq $name }))
}
function Reload([int]$tp) {
    $ev = [System.Threading.EventWaitHandle]::OpenExisting("Local\RynthCore.Engine.RequestReload.p$tp")
    [void]$ev.Set(); $ev.Dispose()
}

if ($Runs -contains "A") {
    Say "==== A: Decal only"
    $tp = Start-One "DecalOnly" @()
    $blog = "C:\Games\RynthCore\Logs\DecalBridge.$tp.log"
    Say "pid $tp; in world: $(Wait-Match $blog 'LoginComplete' $LoginTimeout)"
    Start-Sleep $Settle
    python $diff $tp --json "$out\A-decal-only.json" *> "$out\A-decal-only.txt"
    Say "patch diff -> $out\A-decal-only.txt"
    Grab $blog "." 30
    Stop-One $tp
}

if ($Runs -contains "B") {
    Say "==== B: Decal + RynthCore, bridge mode Auto (in-game ImGui: engine.json default)"
    $tp = Start-One "Both" @()
    $log = "C:\Games\RynthCore\Logs\RynthCore.$tp.log"
    Say "pid $tp; in world: $(Wait-Match $log 'starting plugin tick pump|BridgeProbe: OnLoginComplete' $LoginTimeout)"
    Start-Sleep $Settle
    python $diff $tp --names $log --json "$out\B-bridge.json" *> "$out\B-bridge.txt"
    Say "  VTank loaded (uTank2.dll): $(HasModule $tp 'uTank2.dll')"
    Send "/ra cache"
    Say "-- typed /ra cache (Decal CommandLineText -> bridge -> engine)"
    Typed $tp "/ra cache"
    Say "-- /loc through the engine's dispatcher -> bridge -> Decal InvokeChatParser"
    Send "/loc"
    for ($i = 1; $i -le 2; $i++) {
        Say "-- reload $i"
        Reload $tp
        Start-Sleep 30
        Send "/ra cache"
    }
    Grab $log "BRIDGE MODE|bridge is up|skipped - bridge|bridge fallback|DecalBridge:|BridgeProbe:|location|unloaded|Reload: complete|EndSceneHook: INSTALLED|tick pump|UseTime \(no EndScene|\[ERR\]" 140
    $fps = Select-String -Path $log -Pattern "hb #\d+ .*fps=(\d+)" | Select-Object -Last 10 | ForEach-Object { $_.Matches[0].Groups[1].Value }
    Say "  fps (last 10 heartbeats): $($fps -join ' ')"
    Grab "C:\Games\RynthCore\Logs\DecalBridge.$tp.log" "." 40
    $p = Get-Process -Id $tp -ErrorAction SilentlyContinue
    Say "  client alive=$([bool]$p) responding=$($p.Responding)"
    Stop-One $tp
}

if ($Runs -contains "C") {
    Say "==== C: Decal + RynthCore, DecalBridge=Off (old coexistence mode)"
    $tp = Start-One "Both" @("NoBridge")
    $log = "C:\Games\RynthCore\Logs\RynthCore.$tp.log"
    Say "pid $tp; in world: $(Wait-Match $log 'starting plugin tick pump' $LoginTimeout)"
    Start-Sleep $Settle
    python $diff $tp --names $log --json "$out\C-coexistence.json" *> "$out\C-coexistence.txt"
    Send "/ra cache"
    Say "-- reload"
    Reload $tp
    Start-Sleep 30
    Send "/ra cache"
    Grab $log "PATTERN MISS|Decal|unloaded|Reload: complete|tick pump|BridgeProbe:|\[ERR\]" 80
    $p = Get-Process -Id $tp -ErrorAction SilentlyContinue
    Say "  client alive=$([bool]$p) responding=$($p.Responding)"
    Stop-One $tp
}
if ($Runs -contains "D") {
    Say "==== D: RynthCore only, no Decal (normal hook mode)"
    $tp = Start-One "EngineOnly" @()
    $log = "C:\Games\RynthCore\Logs\RynthCore.$tp.log"
    Say "pid $tp; in world: $(Wait-Match $log 'OnLoginComplete observed' $LoginTimeout)"
    Start-Sleep $Settle
    Send "/ra cache"
    Say "-- reload"
    Reload $tp
    Start-Sleep 30
    Send "/ra cache"
    # Bridge code must stay silent here: no "DecalBridge:" line, no skipped step.
    Grab $log "DecalBridge|skipped - bridge|Decal coexistence|Cache:|unloaded|Reload: complete|\[ERR\]" 60
    $p = Get-Process -Id $tp -ErrorAction SilentlyContinue
    Say "  client alive=$([bool]$p) responding=$($p.Responding)"
    Stop-One $tp
}
Say "done: $out"
