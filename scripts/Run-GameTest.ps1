#Requires -Version 7
<#
.SYNOPSIS
    Logs a test character in, has RynthAi run the self-test meta (RynthSuite GameTests\RynthSelfTest.af)
    and prints what passed. Exit 0 when nothing failed.

.DESCRIPTION
    The meta tests RynthAi in game, one check after another: a force rebuff, a kill, looting the corpse,
    and missile crafting from bundles with the ammo taken off. It saves rt_<check> = PASS / FAIL / SKIP
    and the reason in the character's RynthAi\pvars\<name>.txt, which this script reads. A check the
    character can't do (no bow, no monster in view) is a SKIP, not a FAIL.

    The client is launched the way Test-ClrWorld.ps1 launches one: the injector's headless --launch with
    a saved account profile (the password stays in the launcher's settings), the test engine settings,
    its own panel layout, and its OWN dispatch file. Commands go only to that client; the shared
    %APPDATA%\RynthCore\dispatch.txt would reach every running client. Plugins are the installed ones
    (C:\Games\RynthSuite), so this tests what is deployed on this PC.

    -ClientPid runs it in a client that is already in the world instead (logging the same account in
    again would drop that one). Commands then go through RynthRemote's per-client command files
    (C:\Games\RynthCore\Logs\status\commands\RynthCore.<pid>.*.cmd.json, the phone remote's path), which
    only that client reads, and the client is left running.

    The meta switches Combat, Buffing, Looting and Nav on and off and puts them back when it finishes.
    At the end a client this script launched is closed normally (as the window's close button does)
    unless -Keep.

    Only point it at a character that may fight and loot where it stands, on a server you run or
    may test on. The character should stand near easy monsters; for the crafting check it needs a
    bow, crossbow or atlatl and arrowhead and shaft bundles, and no finished ammo to test making it.

.EXAMPLE
    .\scripts\Run-GameTest.ps1                                   # +Buffi on the local ACEmulator server
.EXAMPLE
    .\scripts\Run-GameTest.ps1 -Account <account> -Server Aelrynth -Character Freya -Keep
.EXAMPLE
    .\scripts\Run-GameTest.ps1 -ClientPid 52000                  # Freya, already logged in
#>
param(
    [string]$Account = "Buffi",
    [string]$Server = "ACEmulator",
    [string]$Character = "+Buffi",
    [string]$RynthSuiteRoot = "",        # defaults to ..\RynthSuite beside this RynthCore
    [string]$Runtime = "C:\Games\RynthCore\Runtime",
    [string]$Settings = "C:\Games\RynthCore\Test\engine-plugins.json",
    [string]$SuiteDir = "C:\Games\RynthSuite",
    [int]$LoginTimeoutSeconds = 120,
    [int]$TimeoutMinutes = 15,           # the meta itself gives up on each check well before this
    [int]$ClientPid = 0,                 # a client already in the world: test in it (no launch, left running)
    [switch]$Keep                        # leave the client running afterwards
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
if (-not $RynthSuiteRoot) { $RynthSuiteRoot = Join-Path (Split-Path $repo -Parent) "RynthSuite" }
$metaSource = Join-Path $RynthSuiteRoot "GameTests\RynthSelfTest.af"
if (-not (Test-Path $metaSource)) { throw "No self-test meta at $metaSource (pass -RynthSuiteRoot)" }
$metaDir = Join-Path $SuiteDir "RynthAi\MetaFiles"
$pvarsDir = Join-Path $SuiteDir "RynthAi\pvars"
if (-not (Test-Path $metaDir)) { throw "No RynthAi meta folder at $metaDir" }

if ($ClientPid) {
    # A running client: its character from the engine's status file, its commands through RynthRemote.
    if (-not (Get-Process -Id $ClientPid -ErrorAction SilentlyContinue)) { throw "no process $ClientPid" }
    $statusFile = "C:\Games\RynthCore\Logs\status\RynthCore.$ClientPid.status.json"
    if (-not (Test-Path $statusFile)) { throw "no engine status for client $ClientPid ($statusFile)" }
    $status = Get-Content $statusFile -Raw | ConvertFrom-Json
    if (-not $status.inWorld) { throw "client $ClientPid isn't in the world" }
    if (-not $PSBoundParameters.ContainsKey("Character")) { $Character = $status.character }
    elseif ($Character -ne $status.character) { throw "client $ClientPid is $($status.character), not $Character" }
    $log = "C:\Games\RynthCore\Logs\RynthCore.$ClientPid.log"
    if (-not (Select-String -Path $log -Pattern "loaded RynthCore\.Plugin\.RynthRemote\.dll" -Quiet)) {
        throw "client $ClientPid hasn't loaded RynthRemote, which takes the commands"
    }
} else {
    $injector = "$repo\out\injector\RynthCore.Injector.exe"
    if (-not (Test-Path $injector)) {
        dotnet publish "$repo\src\RynthCore.Injector\RynthCore.Injector.csproj" -c Release -o "$repo\out\injector" -nologo -v q | Out-Null
        if (-not (Test-Path $injector)) { throw "could not build the injector" }
    }
}

# The meta goes in as RynthSelfTest.af. /ra meta load of a name that isn't there would create and
# load a BLANK meta, so the copy is checked first, and the run is checked to have started below.
Copy-Item $metaSource (Join-Path $metaDir "RynthSelfTest.af") -Force
if ((Get-FileHash $metaSource).Hash -ne (Get-FileHash (Join-Path $metaDir "RynthSelfTest.af")).Hash) { throw "the meta did not copy to $metaDir" }

# The character's pvars file: its name with the characters Windows won't take in a file name as '_'
# (ExpressionEngine.SanitizeFileName).
$invalid = [IO.Path]::GetInvalidFileNameChars()
$fileName = -join ($Character.ToCharArray() | ForEach-Object { if ($invalid -contains $_) { '_' } else { $_ } })
$pvars = Join-Path $pvarsDir "$fileName.txt"
# True when the log has a heartbeat written since $since (by its time of day, past midnight too)
# that says the character is in the world (login=1) with plugins ticking (plug=N/s, N > 0).
function Test-ReadySince([string]$logPath, [datetime]$since) {
    if (-not (Test-Path $logPath)) { return $false }
    $window = ((Get-Date) - $since).TotalSeconds + 5
    foreach ($m in Select-String -Path $logPath -Pattern '^\[(\d\d):(\d\d):(\d\d)\.\d+\] .*hb #\d+ .*plug=([1-9]\d*)/s.*login=1') {
        $g = $m.Matches[0].Groups
        $at = New-TimeSpan -Hours ([int]$g[1].Value) -Minutes ([int]$g[2].Value) -Seconds ([int]$g[3].Value)
        $d = ($at - $since.TimeOfDay).TotalSeconds
        if ($d -lt -2) { $d += 86400 }
        if ($d -ge -2 -and $d -le $window) { return $true }
    }
    return $false
}

function Read-Results {
    $r = @{}
    if (Test-Path $pvars) {
        foreach ($line in Get-Content $pvars) {
            $i = $line.IndexOf("`t")
            if ($i -gt 0 -and $line.StartsWith("rt_")) { $r[$line.Substring(0, $i)] = $line.Substring($i + 1) }
        }
    }
    return $r
}

$started = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
if ($ClientPid) {
    # $gamePid, not $clientPid: PowerShell names ignore case, so that would BE the -ClientPid parameter,
    # and a launch would then read as "already in the world" and skip the wait (2026-10-22).
    $gamePid = $ClientPid
    $commandDir = "C:\Games\RynthCore\Logs\status\commands"
    New-Item -ItemType Directory -Force $commandDir | Out-Null
    # The file the status agent writes for a phone command (LocalStatusServer.cs): RynthRemote in that
    # client takes RynthCore.<its pid>.*.cmd.json in name order and hands "sendchat" to RynthAi.
    function Send([string]$command) {
        $name = "RynthCore.$gamePid.$('{0:D19}' -f [DateTime]::UtcNow.Ticks)-$([Guid]::NewGuid().ToString('N')).cmd.json"
        $json = @{ pid = $gamePid; action = "sendchat"; value = $command; ts = [DateTimeOffset]::UtcNow.ToString("o") } | ConvertTo-Json -Compress
        Set-Content "$commandDir\$name.tmp" $json -NoNewline
        Move-Item "$commandDir\$name.tmp" "$commandDir\$name" -Force
        Start-Sleep 2
    }
    Write-Host "Testing in client $gamePid ($Character), log $log"
} else {
    # The same isolation as Test-ClrWorld.ps1: test engine settings, its own panel layout, its own dispatch file.
    $env:RYNTHCORE_ENGINE_SETTINGS = $Settings
    $env:RYNTHCORE_TEST_CLIENT = "Run-GameTest"
    $env:LOCALAPPDATA = "C:\Games\RynthCore\Test\localappdata"
    New-Item -ItemType Directory -Force $env:LOCALAPPDATA | Out-Null
    $dispatch = "C:\Games\RynthCore\Test\dispatch-gametest.txt"
    $env:RYNTHCORE_DISPATCH_FILE = $dispatch
    Set-Content $dispatch "# Run-GameTest dispatch file"
    function Send([string]$command) { Set-Content $dispatch "force:$command"; Start-Sleep 2 }

    Write-Host "Launching $Character ($Account on $Server)..."
    $launchedAt = Get-Date
    $out = & $injector --launch --account $Account --server $Server --engine "$Runtime\RynthCore.Loader.dll" --character $Character 2>&1
    $m = $out | Select-String "LAUNCHED_PID=(\d+)"
    if (-not $m) { $out | ForEach-Object { Write-Host "  $_" }; throw "the injector didn't launch a client" }
    $gamePid = [int]$m.Matches[0].Groups[1].Value
    $log = "C:\Games\RynthCore\Logs\RynthCore.$gamePid.log"
    Write-Host "client $gamePid, log $log"
}

$exit = 1
try {
    if (-not $ClientPid) {
        # In the world with plugins running: a heartbeat written since the launch says login=1 and
        # plug=N/s with N > 0. Only lines since the launch count: pids are reused and logs kept, so
        # RynthCore.<pid>.log can hold an old client's "login=1" (2026-10-22: the commands went out
        # before RynthAi had loaded, and the run never started).
        $deadline = (Get-Date).AddSeconds($LoginTimeoutSeconds)
        while (-not (Test-ReadySince $log $launchedAt)) {
            if (-not (Get-Process -Id $gamePid -ErrorAction SilentlyContinue)) { throw "the client exited before logging in" }
            if ((Get-Date) -gt $deadline) { throw "not in the world with plugins running after $LoginTimeoutSeconds s" }
            Start-Sleep 3
        }
        Start-Sleep 10   # plugins settle (RynthAi loads its profile, the character's own meta may start)
    }

    # A meta's rules run only with the macro on AND RynthAi's Meta switch on (Freya's was off on the
    # first run, 2026-10-22: the meta loaded and nothing happened). Put the switch back afterwards.
    $statusFile = "C:\Games\RynthCore\Logs\status\RynthCore.$gamePid.status.json"
    $metaWasOn = $true
    try { $metaWasOn = [bool](Get-Content $statusFile -Raw | ConvertFrom-Json).bot.metaEnabled } catch { }
    Send "/ra stop"
    Send "/ra meta load RynthSelfTest"
    Send "/vt opt set EnableMeta true"
    Send "/ra start"

    # Started: the meta's Default state writes rt_status RUNNING and rt_started (unix seconds).
    $deadline = (Get-Date).AddSeconds(45)
    do {
        Start-Sleep 3
        $r = Read-Results
        $fresh = $r.ContainsKey("rt_started") -and [long]$r["rt_started"] -ge $started
        if ((Get-Date) -gt $deadline) { throw "the self-test meta didn't start within 45 s (no fresh rt_started in $pvars)" }
    } until ($fresh)
    Write-Host "the self-test meta is running"

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $shown = @{}
    do {
        Start-Sleep 5
        if (-not (Get-Process -Id $gamePid -ErrorAction SilentlyContinue)) { throw "the client exited during the test" }
        $r = Read-Results
        foreach ($k in "rt_buff", "rt_fight", "rt_loot", "rt_craft") {
            if ($r[$k] -and $r[$k] -ne "PENDING" -and -not $shown[$k]) {
                $shown[$k] = $true
                Write-Host ("  {0,-6} {1}" -f $r[$k], $k.Substring(3))
            }
        }
        if ((Get-Date) -gt $deadline) { throw "the self-test meta didn't finish within $TimeoutMinutes minutes" }
    } until ($r["rt_status"] -eq "DONE" -and $r.ContainsKey("rt_finished") -and [long]$r["rt_finished"] -ge $started)

    Write-Host ""
    $names = [ordered]@{ meta = "self-test meta"; buff = "buffing"; fight = "fight (a kill)"; loot = "looting"; craft = "missile crafting" }
    $fails = 0
    foreach ($k in $names.Keys) {
        $result = $r["rt_$k"]; $why = $r["rt_${k}_why"]
        if ($result -eq "FAIL" -or -not $result) { $fails++ }
        $color = switch ($result) { "PASS" { "Green" } "SKIP" { "Yellow" } default { "Red" } }
        Write-Host ("{0,-5} {1,-17} {2}" -f $result, $names[$k], $why) -ForegroundColor $color
    }
    Write-Host ""
    Write-Host $(if ($fails -eq 0) { "No failures." } else { "$fails check(s) failed." })
    $exit = [math]::Min($fails, 1)
}
finally {
    $p = Get-Process -Id $gamePid -ErrorAction SilentlyContinue
    if ($p -and (Get-Variable metaWasOn -ErrorAction SilentlyContinue) -and -not $metaWasOn) {
        Send "/ra stop"   # the meta stops the macro when it finishes; this covers a run that didn't
        Send "/vt opt set EnableMeta false"
    }
    if ($p -and -not $Keep -and -not $ClientPid) {
        # The normal close path (plugins stopped, engine shut down, logged off), not a kill.
        [void]$p.CloseMainWindow()
        if (-not $p.WaitForExit(30000)) { Write-Warning "client $gamePid didn't close within 30 s; left running" }
        else { Write-Host "client closed" }
    }
}
exit $exit
