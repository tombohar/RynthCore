# Measure-HookRace.ps1 - how often Decal's patch ends up on top of ours at the entry points
# both of us detour (old coexistence mode, no bridge), and what a hot reload then does.
# No server needed (the race is decided in the first seconds). TEST clients only.
param([int]$Launches = 6, [string]$Runtime = "C:\Games\RynthCore\Test\rt-decal")
$ErrorActionPreference = "Stop"
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$watch = "$repo\tools\WatchPatch.py"
$addrs = @("0x005649F0", "0x004CBF70", "0x005821A0")   # AddTextToScroll, gmSalvageUI::OpenSalvagePanel, outgoing chat
for ($i = 1; $i -le $Launches; $i++) {
    & "$PSScriptRoot\Start-DecalTestClient.ps1" -Mode Both -NoBridge -Runtime $Runtime *> $null
    $tp = [int]((Get-Content C:\Games\RynthCore\Test\decal-spike-pids.txt -Tail 1).Split(' ')[2])
    Write-Host "==== launch $i pid $tp"
    $lines = python $watch $tp 25 @addrs 2>&1
    $lines | ForEach-Object { Write-Host "  $_" }
    $final = @{}
    foreach ($l in $lines) { if ($l -match '^\S+ (0x[0-9A-F]{8}) \S+ -> (.*)$') { $final[$Matches[1]] = $Matches[2] } }
    $lost = $addrs[0..1] | Where-Object { $final[$_] -like "Decal*" }
    Write-Host "  final: $(($addrs | ForEach-Object { "$_=$($final[$_])" }) -join '; ')"
    if ($lost) {
        Write-Host "  Decal is on top at $($lost -join ', ') -> hot reload, watching"
        $job = Start-Job { python $using:watch $using:tp 14 @using:addrs 2>&1 }
        Start-Sleep 1
        $ev = [System.Threading.EventWaitHandle]::OpenExisting("Local\RynthCore.Engine.RequestReload.p$tp")
        [void]$ev.Set(); $ev.Dispose()
        Wait-Job $job -Timeout 30 | Out-Null
        Receive-Job $job | ForEach-Object { Write-Host "    $_" }
    }
    Stop-Process -Id $tp -Force -ErrorAction SilentlyContinue
    Start-Sleep 2
}
