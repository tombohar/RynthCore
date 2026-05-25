# ============================================================================
#  Show-AcMemoryTrend.ps1
#
#  Live console-based memory monitor for acclient.exe. Complements the
#  persistent CSV logger (Watch-AcMemory.ps1) by giving an in-the-moment
#  trend view: current WorkingSet/Private/Virtual + handle/thread counts,
#  deltas vs. session start, and computed growth rates (MB/min, MB/hour)
#  with stable/growing/shrinking indicators.
#
#  Two parallel rate windows are shown:
#    * all-time  — from the first sample we took to now (good for long
#                  averaged leak rates)
#    * recent    — last ~15 samples (~75s at 5s tick), so a process that
#                  leaked at startup then stabilised reads STABLE in this
#                  column while the all-time column still shows growth
#
#  Zero impact on the running engine — pure OS query, no injection.
#
#  This is a live VIEWER only — no file output. Persistence is owned by
#  Watch-AcMemory.ps1 (5-min CSV samples to C:\Games\RynthCore\Logs\).
#  Both can run in parallel against the same PID.
#
#  Usage:
#    .\Show-AcMemoryTrend.ps1                      # auto-pick first acclient.exe, 5s refresh
#    .\Show-AcMemoryTrend.ps1 -TargetPid 35456     # explicit PID
#    .\Show-AcMemoryTrend.ps1 -IntervalSeconds 2   # tighter cadence
#    .\Show-AcMemoryTrend.ps1 -HistorySize 120     # deeper recent-trend window
#
#  acclient.exe is x86 — 2 GB user address space ceiling. The viewer
#  surfaces a WARNING line at >1800 MB Virtual and a CRITICAL line at
#  >1900 MB Virtual.
#
#  Exit: Ctrl-C (clean), or auto-exits with a final summary when the
#  watched process terminates.
#
#  Accessibility: status indicators use text labels (STABLE / GROWING /
#  SHRINKING) and ASCII arrows (->, ^, v). Colour is used as a
#  supplement only and is never the sole signal.
# ============================================================================

[CmdletBinding()]
param(
    [Alias('Pid')]
    [int]$TargetPid = 0,

    [int]$IntervalSeconds = 5,

    # How many samples to keep in the rolling buffer. 60 @ 5s = 5 min.
    [int]$HistorySize = 60,

    # How many of the most-recent samples define the "recent trend" slope.
    [int]$RecentSampleCount = 15,

    # MB/min threshold below which a memory metric reads STABLE.
    [double]$StableThresholdMbPerMin = 0.5,

    # Handles/hour threshold below which the handle metric reads STABLE.
    [double]$StableThresholdHandlesPerHour = 50.0
)

# ---------------------------------------------------------------------------
# Resolve target PID — explicit param wins, else auto-pick the running client.
# ---------------------------------------------------------------------------
if ($TargetPid -eq 0) {
    $candidates = @(Get-Process -Name acclient -ErrorAction SilentlyContinue)
    if ($candidates.Count -eq 0) {
        Write-Error 'No acclient.exe process found. Pass -TargetPid <id> or launch AC first.'
        exit 1
    }
    if ($candidates.Count -gt 1) {
        Write-Warning ("Multiple acclient.exe processes running ({0}). Picking PID {1}; pass -TargetPid to override." -f $candidates.Count, $candidates[0].Id)
        Start-Sleep -Seconds 2
    }
    $TargetPid = $candidates[0].Id
}

$proc = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
if (-not $proc) {
    Write-Error "No process found with PID $TargetPid."
    exit 1
}

# StartTime can throw on protected processes; tolerate it.
$startTime = $null
try { $startTime = $proc.StartTime } catch { $startTime = (Get-Date) }

# ---------------------------------------------------------------------------
# Sample state. Each sample is a PSCustomObject; we keep at most $HistorySize.
# ---------------------------------------------------------------------------
$history    = New-Object System.Collections.Generic.List[object]
$firstSample = $null
$sessionStartWall = Get-Date

function New-Sample {
    param([System.Diagnostics.Process]$p)

    [pscustomobject]@{
        Time      = Get-Date
        WsMB      = [double]($p.WorkingSet64       / 1MB)
        PrivMB    = [double]($p.PrivateMemorySize64 / 1MB)
        VmMB      = [double]($p.VirtualMemorySize64 / 1MB)
        Handles   = [int]$p.HandleCount
        Threads   = [int]$p.Threads.Count
    }
}

# Compute MB-per-minute slope between two samples. Returns 0 if window is
# too short to be meaningful.
function Get-RatePerMinute {
    param($older, $newer, [string]$field)

    if (-not $older -or -not $newer) { return 0.0 }
    $deltaMinutes = ($newer.Time - $older.Time).TotalMinutes
    if ($deltaMinutes -le 0.01) { return 0.0 }
    return ($newer.$field - $older.$field) / $deltaMinutes
}

# Pick a text status label + an ASCII arrow given a slope and a stability
# threshold. The arrow + label are the load-bearing signals (the user is
# colorblind — colour, where used, is a supplement only).
function Get-Status {
    param([double]$ratePerMinute, [double]$threshold)

    $abs = [math]::Abs($ratePerMinute)
    if ($abs -lt $threshold) {
        return [pscustomobject]@{ Label = 'STABLE   '; Arrow = '->'; Color = 'Gray' }
    }
    if ($ratePerMinute -gt 0) {
        return [pscustomobject]@{ Label = 'GROWING  '; Arrow = '^^'; Color = 'Yellow' }
    }
    return [pscustomobject]@{ Label = 'SHRINKING'; Arrow = 'vv'; Color = 'Cyan' }
}

# Format a delta with explicit sign so STABLE vs GROWING is visually obvious
# even without colour. "+12.3" / "-4.5" / "  0.0".
function Format-Delta {
    param([double]$v, [int]$width = 7, [int]$decimals = 1)

    # Build the .NET composite-format string explicitly to dodge PS's
    # "variable:scope" lookahead after a colon. e.g. width=7, decimals=1 ->
    # "{0,7:+0.0;-0.0; 0.0}".
    $dec  = ('0' * $decimals)
    $pos  = if ($decimals -gt 0) { "+0.$dec" } else { '+0' }
    $neg  = if ($decimals -gt 0) { "-0.$dec" } else { '-0' }
    $zero = if ($decimals -gt 0) { " 0.$dec" } else { ' 0' }
    $fmt  = '{0,' + $width + ':' + $pos + ';' + $neg + ';' + $zero + '}'
    return ($fmt -f $v)
}

# ---------------------------------------------------------------------------
# Console plumbing. We try in-place redraw via SetCursorPosition; if the host
# doesn't support it (ISE, some remote shells) we fall back to Clear-Host.
# Either way, redraws must tolerate a resized terminal — no fixed widths
# beyond the host's BufferWidth.
# ---------------------------------------------------------------------------
$supportsCursor = $true
try {
    [void][Console]::CursorTop
    [void][Console]::CursorLeft
} catch {
    $supportsCursor = $false
}

function Clear-Frame {
    if ($supportsCursor) {
        try {
            [Console]::SetCursorPosition(0, 0)
            # Overwrite the previously-drawn region with blanks. We pad to the
            # current BufferWidth so the new frame doesn't leave stale chars
            # at the right edge after a resize.
            $w = [Console]::BufferWidth
            $h = [Math]::Min(40, [Console]::BufferHeight - 1)
            $blank = ' ' * ($w - 1)
            for ($i = 0; $i -lt $h; $i++) {
                [Console]::SetCursorPosition(0, $i)
                [Console]::Write($blank)
            }
            [Console]::SetCursorPosition(0, 0)
        } catch {
            # Resize or other transient failure — fall back to Clear-Host.
            Clear-Host
        }
    } else {
        Clear-Host
    }
}

function Write-Line {
    param([string]$text, [ConsoleColor]$Color = [ConsoleColor]::White)

    # Truncate to BufferWidth-1 so we never wrap mid-frame and corrupt
    # subsequent SetCursorPosition writes.
    try {
        $maxLen = [Console]::BufferWidth - 1
        if ($maxLen -gt 0 -and $text.Length -gt $maxLen) {
            $text = $text.Substring(0, $maxLen)
        }
    } catch { }

    $prevFg = $null
    try { $prevFg = [Console]::ForegroundColor } catch { }
    try { [Console]::ForegroundColor = $Color } catch { }
    [Console]::WriteLine($text)
    if ($null -ne $prevFg) {
        try { [Console]::ForegroundColor = $prevFg } catch { }
    }
}

function Format-Uptime {
    param([TimeSpan]$ts)
    return ("{0:00}:{1:00}:{2:00}" -f [int]$ts.TotalHours, $ts.Minutes, $ts.Seconds)
}

# ---------------------------------------------------------------------------
# Main loop. Sample, append to history, render frame, sleep.
# Ctrl-C handling: PowerShell raises a PipelineStoppedException out of
# Start-Sleep; the finally block prints a goodbye and restores cursor.
# ---------------------------------------------------------------------------
try {
    Clear-Frame
    Write-Line ("Starting Show-AcMemoryTrend for PID {0} (interval {1}s, history {2})..." -f $TargetPid, $IntervalSeconds, $HistorySize)
    Start-Sleep -Milliseconds 250

    while ($true) {
        $p = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
        if (-not $p) {
            Clear-Frame
            Write-Line ("PID {0} (acclient.exe) exited at {1}." -f $TargetPid, (Get-Date).ToString('HH:mm:ss')) Red
            if ($firstSample -and $history.Count -gt 0) {
                $last = $history[$history.Count - 1]
                $sessionMin = ($last.Time - $firstSample.Time).TotalMinutes
                if ($sessionMin -gt 0) {
                    $wsRate   = ($last.WsMB   - $firstSample.WsMB)   / $sessionMin
                    $privRate = ($last.PrivMB - $firstSample.PrivMB) / $sessionMin
                    $vmRate   = ($last.VmMB   - $firstSample.VmMB)   / $sessionMin
                    Write-Line ("Final summary  : {0} samples over {1:0.0} min" -f $history.Count, $sessionMin)
                    Write-Line ("  WS  : last={0,5:0.0} MB  delta={1}  rate={2,7:+0.00;-0.00; 0.00} MB/min" -f $last.WsMB,   (Format-Delta ($last.WsMB   - $firstSample.WsMB)),   $wsRate)
                    Write-Line ("  Priv: last={0,5:0.0} MB  delta={1}  rate={2,7:+0.00;-0.00; 0.00} MB/min" -f $last.PrivMB, (Format-Delta ($last.PrivMB - $firstSample.PrivMB)), $privRate)
                    Write-Line ("  VM  : last={0,5:0.0} MB  delta={1}  rate={2,7:+0.00;-0.00; 0.00} MB/min" -f $last.VmMB,   (Format-Delta ($last.VmMB   - $firstSample.VmMB)),   $vmRate)
                }
            }
            exit 0
        }

        $sample = New-Sample -p $p
        if (-not $firstSample) { $firstSample = $sample }
        $history.Add($sample)
        if ($history.Count -gt $HistorySize) {
            $history.RemoveAt(0)
        }

        # ---- Compute deltas + rates ----
        $deltaWs   = $sample.WsMB    - $firstSample.WsMB
        $deltaPriv = $sample.PrivMB  - $firstSample.PrivMB
        $deltaVm   = $sample.VmMB    - $firstSample.VmMB
        $deltaHnd  = $sample.Handles - $firstSample.Handles
        $deltaThr  = $sample.Threads - $firstSample.Threads

        $allTimeWs   = Get-RatePerMinute $firstSample $sample 'WsMB'
        $allTimePriv = Get-RatePerMinute $firstSample $sample 'PrivMB'
        $allTimeVm   = Get-RatePerMinute $firstSample $sample 'VmMB'
        $allTimeHnd  = Get-RatePerMinute $firstSample $sample 'Handles'

        # Recent window: last N samples, oldest of those vs newest.
        $recentOlder = $null
        if ($history.Count -ge 2) {
            $idx = [Math]::Max(0, $history.Count - $RecentSampleCount)
            $recentOlder = $history[$idx]
        }
        $recentWs   = Get-RatePerMinute $recentOlder $sample 'WsMB'
        $recentPriv = Get-RatePerMinute $recentOlder $sample 'PrivMB'
        $recentVm   = Get-RatePerMinute $recentOlder $sample 'VmMB'
        $recentHnd  = Get-RatePerMinute $recentOlder $sample 'Handles'

        $statWs   = Get-Status -ratePerMinute $recentWs   -threshold $StableThresholdMbPerMin
        $statPriv = Get-Status -ratePerMinute $recentPriv -threshold $StableThresholdMbPerMin
        $statVm   = Get-Status -ratePerMinute $recentVm   -threshold $StableThresholdMbPerMin
        # Handle threshold is given per-hour; convert to per-minute for the
        # comparator so we share the same Get-Status helper.
        $statHnd  = Get-Status -ratePerMinute $recentHnd  -threshold ($StableThresholdHandlesPerHour / 60.0)

        # ---- Render frame ----
        Clear-Frame

        $procUptime = if ($startTime) { (Get-Date) - $startTime } else { [TimeSpan]::Zero }
        $sessionUptime = (Get-Date) - $sessionStartWall

        Write-Line "============================================================================"
        Write-Line ("  Show-AcMemoryTrend   PID {0,-6}   now {1}" -f $TargetPid, (Get-Date).ToString('HH:mm:ss'))
        Write-Line ("  proc started {0}   proc uptime {1}   viewer uptime {2}" -f `
            ($(if ($startTime) { $startTime.ToString('yyyy-MM-dd HH:mm:ss') } else { '?' })), `
            (Format-Uptime $procUptime), (Format-Uptime $sessionUptime))
        Write-Line ("  samples {0}/{1}   interval {2}s   recent window = last {3} samples" -f `
            $history.Count, $HistorySize, $IntervalSeconds, [Math]::Min($RecentSampleCount, $history.Count))
        Write-Line "============================================================================"
        Write-Line ""
        Write-Line "  METRIC      CURRENT       DELTA         ALL-TIME         RECENT (~75s)"
        Write-Line "  ---------   -----------   -----------   --------------   ------------------------"

        # WorkingSet
        Write-Line (("  {0,-9}   {1,7:0.0} MB   {2} MB    {3,7:+0.00;-0.00; 0.00} MB/min   {4,7:+0.00;-0.00; 0.00} MB/min  {5} {6}" -f `
            'WS',   $sample.WsMB,   (Format-Delta $deltaWs),   $allTimeWs,   $recentWs,   $statWs.Arrow, $statWs.Label)) $statWs.Color
        # Private
        Write-Line (("  {0,-9}   {1,7:0.0} MB   {2} MB    {3,7:+0.00;-0.00; 0.00} MB/min   {4,7:+0.00;-0.00; 0.00} MB/min  {5} {6}" -f `
            'Private', $sample.PrivMB, (Format-Delta $deltaPriv), $allTimePriv, $recentPriv, $statPriv.Arrow, $statPriv.Label)) $statPriv.Color
        # Virtual
        Write-Line (("  {0,-9}   {1,7:0.0} MB   {2} MB    {3,7:+0.00;-0.00; 0.00} MB/min   {4,7:+0.00;-0.00; 0.00} MB/min  {5} {6}" -f `
            'Virtual', $sample.VmMB,   (Format-Delta $deltaVm),   $allTimeVm,   $recentVm,   $statVm.Arrow, $statVm.Label)) $statVm.Color
        # Handles (count, not MB; rate column header still reads correctly)
        Write-Line (("  {0,-9}   {1,7}      {2}        {3,7:+0.00;-0.00; 0.00} /min      {4,7:+0.00;-0.00; 0.00} /min     {5} {6}" -f `
            'Handles', $sample.Handles, (Format-Delta $deltaHnd 7 0), $allTimeHnd, $recentHnd, $statHnd.Arrow, $statHnd.Label)) $statHnd.Color
        # Threads (informational, no slope label — usually noisy by design)
        Write-Line ("  {0,-9}   {1,7}      {2}" -f `
            'Threads', $sample.Threads, (Format-Delta $deltaThr 7 0))

        Write-Line ""
        Write-Line ("  Per-hour extrapolation (all-time):  WS {0,7:+0.0;-0.0; 0.0} MB/hr   Priv {1,7:+0.0;-0.0; 0.0} MB/hr   VM {2,7:+0.0;-0.0; 0.0} MB/hr   Handles {3,7:+0;-0; 0} /hr" -f `
            ($allTimeWs * 60), ($allTimePriv * 60), ($allTimeVm * 60), ($allTimeHnd * 60))
        Write-Line ("  Per-hour extrapolation (recent)  :  WS {0,7:+0.0;-0.0; 0.0} MB/hr   Priv {1,7:+0.0;-0.0; 0.0} MB/hr   VM {2,7:+0.0;-0.0; 0.0} MB/hr   Handles {3,7:+0;-0; 0} /hr" -f `
            ($recentWs * 60), ($recentPriv * 60), ($recentVm * 60), ($recentHnd * 60))

        # ---- x86 2 GB ceiling warnings ----
        Write-Line ""
        if ($sample.VmMB -ge 1900) {
            Write-Line "  !! CRITICAL: Virtual memory >= 1900 MB. acclient.exe x86 ceiling is ~2 GB. Crash imminent. !!" Red
            Write-Line "  !! CRITICAL: Save state / disconnect now before the address space is exhausted.            !!" Red
        } elseif ($sample.VmMB -ge 1800) {
            Write-Line "  ** WARNING: Virtual memory >= 1800 MB. Approaching x86 ~2 GB address space ceiling. **" Yellow
        }

        Write-Line ""
        Write-Line "  Status legend: STABLE -> (within threshold)   GROWING ^^ (rising)   SHRINKING vv (falling)"
        Write-Line ("  Thresholds   : memory < {0} MB/min = STABLE; handles < {1} /hr = STABLE" -f $StableThresholdMbPerMin, $StableThresholdHandlesPerHour)
        Write-Line "  Press Ctrl-C to exit. CSV persistence is handled separately by Watch-AcMemory.ps1."

        Start-Sleep -Seconds $IntervalSeconds
    }
} finally {
    # Make sure the cursor is left on a fresh line so the prompt isn't
    # overwritten on whatever row the last frame happened to end on.
    try { [Console]::ResetColor() } catch { }
    Write-Host ""
    Write-Host "Show-AcMemoryTrend exited."
}
