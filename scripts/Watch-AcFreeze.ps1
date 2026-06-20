# ============================================================================
#  Watch-AcFreeze.ps1
#
#  Out-of-process freeze/wedge watchdog for RynthCore-injected acclient.exe.
#  Tails the per-PID heartbeat log (C:\Games\RynthCore\Logs\RynthCore.<pid>.log)
#  and applies the SAME wedge heuristics the Avalonia launcher uses
#  (MaybeRestartWedgedClients), so it can run with the launcher closed:
#
#    * fps=0 with login=1 sustained >= WedgeHoldSeconds   (render dead, alive)
#    * main-thread queue qd > QueueWedgeDepth with login=1 (queue not draining)
#    * combat-mode "stuck Ns" >= StanceStuckSeconds        (item-action hard-lock)
#    * heartbeat file silent > HbStaleSeconds while alive  (engine died in-proc)
#
#  On a CONFIRMED wedge it captures a diagnostic bundle, then (by default) kills
#  and relaunches the same account so an unattended soak keeps running:
#
#    1. procdump -ma full dump            -> Logs\freezes\freeze_<pid>_<stamp>.dmp
#    2. cdb "~*k" all-thread stacks       -> ..._cdb.txt
#    3. symbolicate.py on the per-PID log -> ..._symbolicated.txt
#
#  Zero impact on the running engine - pure OS query + log tail, no injection.
#
#  Usage:
#    .\Watch-AcFreeze.ps1                          # auto-pick the RynthCore client
#    .\Watch-AcFreeze.ps1 -TargetPid 12345
#    .\Watch-AcFreeze.ps1 -Account Buffi           # account to use on relaunch
#    .\Watch-AcFreeze.ps1 -Relaunch:$false         # capture only, leave frozen
#    .\Watch-AcFreeze.ps1 -Once                    # single evaluation pass (test)
# ============================================================================

[CmdletBinding()]
param(
    [Alias('Pid')]
    [int]$TargetPid = 0,

    # Account name/id passed to the injector on relaunch. If empty, read from
    # the watched PID's launch_context_<pid>.json.
    [string]$Account = '',

    [int]$IntervalSeconds = 5,

    # Sustained dwell before an fps=0 / queue wedge is acted on. Matches the
    # launcher's WedgeRestartSeconds default.
    [int]$WedgeHoldSeconds = 90,

    # Heartbeat file untouched this long while the process lives = engine dead.
    [int]$HbStaleSeconds = 120,

    # Combat-mode "stuck Ns" threshold for the fps>0 item-action hard-lock.
    [int]$StanceStuckSeconds = 300,

    # Main-thread queue depth that means AC stopped draining.
    [int]$QueueWedgeDepth = 2000,

    [bool]$Relaunch = $true,

    # Stop relaunching after this many wedges (kill/relaunch loop breaker).
    [int]$MaxRelaunch = 5,

    [string]$InjectorPath = 'C:\Projects\RynthCore\src\RynthCore.Injector\bin\Release\net10.0-windows\RynthCore.Injector.exe',

    [string]$LogDir = 'C:\Games\RynthCore\Logs',

    [string]$ToolsDir = 'C:\Games\RynthCore\tools',

    [string]$SymbolicatePs1 = 'C:\Projects\RynthCore\tools\symbolicate.ps1',

    # Do one evaluation pass and exit (for testing the detection logic).
    [switch]$Once
)

$ErrorActionPreference = 'Stop'
$DumpDir   = Join-Path $LogDir 'freezes'
$WatchLog  = Join-Path $LogDir 'freeze-watch.log'
$ProcDump  = Join-Path $ToolsDir 'procdump.exe'
$Cdb       = Join-Path $ToolsDir 'dbg_x86\cdb.exe'
New-Item -ItemType Directory -Path $DumpDir -Force | Out-Null

# Heartbeat parse. Groups: 1=fps, 2=plug pump rate, 3=login(in-world), 4=qd.
# NOTE: fps is captured but NOT used as a wedge signal - see Test-Wedge.
$HbRegex      = [regex]'hb #\d+ .*?fps=(\d+) .*?plug=(\d+)/s .*?login=(\d)(?:.*?qd=(\d+))?'
$StanceRegex  = [regex]'ChangeCombatMode\([^)]*\) retry #\d+ \(stuck (\d+)s'
# The engine's own MainThreadHangWatchdog confirms a permanent main-thread wedge.
# It beats off the UseTime game tick (live even when EndScene/fps is not), so it
# works in every overlay config - the strongest cross-config signal.
$HangPermRegex = [regex]'still hung after'
$RecoverRegex  = [regex]'MAIN THREAD RECOVERED'

Add-Type -Namespace Native -Name Win -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool IsIconic(System.IntPtr hWnd);
'@

function Write-WatchLog {
    param([string]$Message, [string]$Level = 'INF')
    $line = "[{0}] [{1}] {2}" -f (Get-Date -Format 'HH:mm:ss'), $Level, $Message
    Write-Host $line
    try { Add-Content -Path $WatchLog -Value $line -Encoding UTF8 } catch { }
}

function Get-RynthCorePids {
    # A RynthCore-injected acclient is the one with a recently-written
    # per-PID log; Decal/Thwargle clients have no RynthCore.<pid>.log.
    $live = @{}
    foreach ($p in @(Get-Process -Name acclient -ErrorAction SilentlyContinue)) { $live[$p.Id] = $true }
    $result = @()
    foreach ($f in Get-ChildItem (Join-Path $LogDir 'RynthCore.*.log') -ErrorAction SilentlyContinue) {
        if ($f.Name -match '^RynthCore\.(\d+)\.log$') {
            $candidatePid = [int]$Matches[1]
            if ($live.ContainsKey($candidatePid) -and ((Get-Date) - $f.LastWriteTime).TotalSeconds -lt 30) {
                $result += $candidatePid
            }
        }
    }
    return $result
}

function Resolve-TargetPid {
    if ($TargetPid -ne 0) { return $TargetPid }
    $found = @(Get-RynthCorePids)
    if ($found.Count -eq 0) { return 0 }
    if ($found.Count -gt 1) {
        Write-WatchLog ("Multiple RynthCore clients ({0}); picking {1}. Pass -TargetPid to override." -f ($found -join ', '), $found[0]) 'WRN'
    }
    return $found[0]
}

function Get-AccountForPid {
    param([int]$ProcId)
    if ($Account) { return $Account }
    $ctx = Join-Path $env:APPDATA "RynthCore\launch_contexts\launch_context_$ProcId.json"
    if (Test-Path $ctx) {
        try { return (Get-Content $ctx -Raw | ConvertFrom-Json).AccountName } catch { }
    }
    return ''
}

# Returns $null if healthy, else a reason string. $state is a hashtable used to
# track sustained-dwell start times across calls (per the launcher's logic).
function Test-Wedge {
    param([int]$ProcId, [hashtable]$State)

    $proc = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
    if (-not $proc) { return $null }   # gone, not wedged

    # Minimized windows can read fps=0 on healthy clients - exempt them.
    try {
        $h = $proc.MainWindowHandle
        if ($h -ne [IntPtr]::Zero -and [Native.Win]::IsIconic($h)) {
            $State.Remove('zeroSince'); return $null
        }
    } catch { }

    $logPath = Join-Path $LogDir "RynthCore.$ProcId.log"
    $fi = Get-Item $logPath -ErrorAction SilentlyContinue
    if (-not $fi) { $State.Remove('zeroSince'); return $null }

    if (((Get-Date) - $fi.LastWriteTime).TotalSeconds -gt $HbStaleSeconds) {
        return ("engine heartbeat silent for >{0}s while process alive" -f $HbStaleSeconds)
    }

    # Tail ~16KB (enough for the every-30s combat-stuck line).
    $tail = ''
    try {
        $fs = [System.IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite, Delete')
        try {
            $want = [Math]::Min(16384, $fs.Length)
            [void]$fs.Seek(-$want, 'End')
            $buf = New-Object byte[] $want
            $read = $fs.Read($buf, 0, $want)
            $tail = [System.Text.Encoding]::UTF8.GetString($buf, 0, $read)
        } finally { $fs.Close() }
    } catch { return $null }

    # STRONGEST signal: the engine's own watchdog confirmed a permanent
    # main-thread wedge. Act only on the "still hung" confirmation, and only if
    # no later RECOVERED line (transient stalls self-recover).
    $hangIdx = $tail.LastIndexOf('MAIN THREAD HANG DETECTED')
    if ($hangIdx -ge 0) {
        $after = $tail.Substring($hangIdx)
        if ($HangPermRegex.IsMatch($after) -and -not $RecoverRegex.IsMatch($after)) {
            return "in-process MainThreadHangWatchdog confirmed a permanent main-thread wedge"
        }
    }

    # Item-action hard-lock while the engine threads keep ticking.
    $stanceStuck = 0
    foreach ($m in $StanceRegex.Matches($tail)) {
        $s = [int]$m.Groups[1].Value
        if ($s -gt $stanceStuck) { $stanceStuck = $s }
    }
    if ($stanceStuck -ge $StanceStuckSeconds) {
        return ("combat-mode stuck {0}s with login=1 (item-action hard-lock, heartbeat alive)" -f $stanceStuck)
    }

    $hb = $HbRegex.Matches($tail)
    if ($hb.Count -eq 0) { $State.Remove('zeroSince'); return $null }
    $last = $hb[$hb.Count - 1]
    $fps     = [int]$last.Groups[1].Value
    $plug    = [int]$last.Groups[2].Value
    $inWorld = $last.Groups[3].Value -eq '1'
    $qd      = if ($last.Groups[4].Success) { [int]$last.Groups[4].Value } else { 0 }

    # Minimized clients were already exempted above (they throttle fps on their
    # own), so fps=0 here = a VISIBLE in-world client that stopped rendering
    # (a healthy visible client renders tens of fps). plug=0 = plugin pump dead.
    if (-not $inWorld) { $State.Remove('zeroSince'); return $null }

    $reason = $null
    if ($fps -eq 0)                   { $reason = "fps=0 with login=1 (render dead)" }
    elseif ($plug -eq 0)              { $reason = "plugin pump dead (plug=0/s) with login=1" }
    elseif ($qd -gt $QueueWedgeDepth) { $reason = ("main-thread queue backing up (qd={0}, login=1)" -f $qd) }

    if (-not $reason) { $State.Remove('zeroSince'); return $null }

    if (-not $State.ContainsKey('zeroSince')) {
        $State['zeroSince'] = Get-Date
        Write-WatchLog ("Wedge candidate on PID {0} ({1}) - holding {2}s to confirm." -f $ProcId, $reason, $WedgeHoldSeconds) 'WRN'
        return $null
    }
    $held = ((Get-Date) - $State['zeroSince']).TotalSeconds
    if ($held -lt $WedgeHoldSeconds) { return $null }

    return ("{0} for >{1}s (heartbeat alive - AC main thread/pump wedged)" -f $reason, $WedgeHoldSeconds)
}

function Invoke-FreezeCapture {
    param([int]$ProcId, [string]$Reason)
    $stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
    $base    = Join-Path $DumpDir ("freeze_{0}_{1}" -f $ProcId, $stamp)
    $dmp     = "$base.dmp"
    $cdbOut  = "${base}_cdb.txt"
    $symOut  = "${base}_symbolicated.txt"

    Write-WatchLog ("FREEZE CONFIRMED pid={0} - {1}" -f $ProcId, $Reason) 'ERR'
    Write-WatchLog ("Capturing bundle -> {0}.*" -f $base)

    # 1. Full dump.
    if (Test-Path $ProcDump) {
        try {
            & $ProcDump -accepteula -ma $ProcId $dmp 2>&1 | Out-Null
            if (Test-Path $dmp) { Write-WatchLog ("  dump:        {0} ({1} MB)" -f $dmp, [int]((Get-Item $dmp).Length/1MB)) }
            else { Write-WatchLog "  dump:        procdump produced no file" 'WRN' }
        } catch { Write-WatchLog ("  dump FAILED: {0}" -f $_.Exception.Message) 'WRN' }
    } else { Write-WatchLog ("  procdump not found at {0}" -f $ProcDump) 'WRN' }

    # 2. All-thread native stacks from the dump (catches fps>0 locks the
    #    in-process watchdog never logged a stack for).
    if ((Test-Path $Cdb) -and (Test-Path $dmp)) {
        try {
            & $Cdb -z $dmp -c "~*k; q" 2>&1 | Out-File -FilePath $cdbOut -Encoding UTF8
            Write-WatchLog ("  cdb stacks:  {0}" -f $cdbOut)
        } catch { Write-WatchLog ("  cdb FAILED:  {0}" -f $_.Exception.Message) 'WRN' }
    }

    # 3. Symbolicate the per-PID log (contains the in-process hang stack when
    #    the EndScene/UseTime watchdog fired).
    $logPath = Join-Path $LogDir "RynthCore.$ProcId.log"
    if ((Test-Path $SymbolicatePs1) -and (Test-Path $logPath)) {
        try {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $SymbolicatePs1 $logPath -o $symOut --only-crash 2>&1 | Out-Null
            if (Test-Path $symOut) { Write-WatchLog ("  symbolicated: {0}" -f $symOut) }
        } catch { Write-WatchLog ("  symbolicate FAILED: {0}" -f $_.Exception.Message) 'WRN' }
    }

    return $base
}

function Invoke-Relaunch {
    param([int]$DeadPid, [string]$Acct)
    if (-not (Test-Path $InjectorPath)) {
        Write-WatchLog ("Cannot relaunch - injector not found at {0}" -f $InjectorPath) 'ERR'
        return 0
    }
    if (-not $Acct) {
        Write-WatchLog "Cannot relaunch - no account resolved (pass -Account)." 'ERR'
        return 0
    }
    try { Stop-Process -Id $DeadPid -Force -ErrorAction SilentlyContinue } catch { }
    Write-WatchLog ("Relaunching account '{0}' via injector..." -f $Acct)
    $out = & $InjectorPath --launch --account $Acct 2>&1
    $out | ForEach-Object { Write-WatchLog ("  injector> {0}" -f $_) }
    $m = ($out | Select-String -Pattern 'LAUNCHED_PID=(\d+)' | Select-Object -Last 1)
    if ($m) {
        $newPid = [int]$m.Matches[0].Groups[1].Value
        Write-WatchLog ("Relaunched as PID {0}." -f $newPid) 'INF'
        return $newPid
    }
    Write-WatchLog "Relaunch did not report a PID." 'WRN'
    return 0
}

# -- Main ---------------------------------------------------------------------
$activePid = Resolve-TargetPid
if ($activePid -eq 0) {
    Write-WatchLog "No RynthCore-injected acclient found. Launch one first (RynthCore.Injector.exe --launch)." 'ERR'
    exit 1
}
$acct = Get-AccountForPid -ProcId $activePid
Write-WatchLog ("Watching PID {0} (account='{1}', relaunch={2}, hold={3}s, interval={4}s)." -f $activePid, $acct, $Relaunch, $WedgeHoldSeconds, $IntervalSeconds)

$state = @{}
$relaunchCount = 0

while ($true) {
    $proc = Get-Process -Id $activePid -ErrorAction SilentlyContinue
    if (-not $proc) {
        Write-WatchLog ("PID {0} is gone." -f $activePid) 'WRN'
        if ($Relaunch -and -not $Once -and $acct -and $relaunchCount -lt $MaxRelaunch) {
            $relaunchCount++
            $newPid = Invoke-Relaunch -DeadPid $activePid -Acct $acct
            if ($newPid -ne 0) { $activePid = $newPid; $state = @{}; Start-Sleep -Seconds 10; continue }
        }
        break
    }

    $reason = Test-Wedge -ProcId $activePid -State $state
    if ($reason) {
        [void](Invoke-FreezeCapture -ProcId $activePid -Reason $reason)
        $state = @{}
        if ($Relaunch -and -not $Once) {
            if ($relaunchCount -ge $MaxRelaunch) {
                Write-WatchLog ("Hit MaxRelaunch ({0}) - leaving client and stopping." -f $MaxRelaunch) 'ERR'
                break
            }
            $relaunchCount++
            $newPid = Invoke-Relaunch -DeadPid $activePid -Acct $acct
            if ($newPid -ne 0) { $activePid = $newPid; Start-Sleep -Seconds 10; continue }
            break
        } else {
            Write-WatchLog ("Relaunch disabled - leaving PID {0} frozen for inspection. Stopping." -f $activePid) 'INF'
            break
        }
    }

    if ($Once) { Write-WatchLog ("Single pass complete - PID {0} healthy." -f $activePid); break }
    Start-Sleep -Seconds $IntervalSeconds
}
