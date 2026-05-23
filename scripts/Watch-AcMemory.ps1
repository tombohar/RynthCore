# ============================================================================
#  Watch-AcMemory.ps1
#
#  Polls acclient.exe's memory + handle metrics on an interval and appends
#  them to a CSV under C:\Games\RynthCore\Logs\. Designed for long-running
#  24/7 bot sessions where we want to know:
#
#    * is RynthCore (or AC itself) leaking?
#    * if yes, how fast (MB/hour)?
#    * does growth correlate with subsystem activity (loot on/off, mage cast
#      cycles, travel into new cells)?
#
#  Zero impact on the running engine — runs in a separate PowerShell host,
#  pure OS query, no injection.
#
#  Usage:
#    .\Watch-AcMemory.ps1                       # auto-pick first acclient.exe
#    .\Watch-AcMemory.ps1 -Pid 35456            # explicit PID
#    .\Watch-AcMemory.ps1 -IntervalSeconds 60   # tighter cadence
#    .\Watch-AcMemory.ps1 -OutFile foo.csv      # custom output path
#
#  Exits when the watched process exits (clean or crash) so it's safe to
#  start-and-forget; the CSV stays on disk for post-session analysis.
# ============================================================================

[CmdletBinding()]
param(
    [Alias('Pid')]
    [int]$TargetPid = 0,

    [int]$IntervalSeconds = 300,

    [string]$OutFile = $null
)

# Resolve target PID — explicit param wins, else auto-pick the running acclient.
if ($TargetPid -eq 0) {
    $candidates = @(Get-Process -Name acclient -ErrorAction SilentlyContinue)
    if ($candidates.Count -eq 0) {
        Write-Error 'No acclient.exe process found. Pass -TargetPid <id> or launch AC first.'
        exit 1
    }
    if ($candidates.Count -gt 1) {
        Write-Warning ("Multiple acclient.exe processes running ({0}). Picking PID {1}; pass -TargetPid to override." -f $candidates.Count, $candidates[0].Id)
    }
    $TargetPid = $candidates[0].Id
}

# Verify the PID is alive before we commit to a CSV path.
$proc = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
if (-not $proc) {
    Write-Error "No process found with PID $TargetPid."
    exit 1
}

# Default output: timestamped per session so successive runs never overwrite.
if (-not $OutFile) {
    $logsDir = 'C:\Games\RynthCore\Logs'
    if (-not (Test-Path $logsDir)) { New-Item -ItemType Directory -Path $logsDir | Out-Null }
    $stamp = (Get-Date -Format 'yyyyMMdd-HHmmss')
    $OutFile = Join-Path $logsDir ("acclient-memory-pid{0}-{1}.csv" -f $TargetPid, $stamp)
}

Write-Host "Watching acclient.exe PID $TargetPid"
Write-Host "Interval     : $IntervalSeconds seconds"
Write-Host "Output       : $OutFile"
Write-Host "Process start: $($proc.StartTime)"
Write-Host ''

# Header. Append-only after this so a Ctrl-C resume preserves prior samples.
'Timestamp,PID,UptimeMinutes,WorkingSetMB,PrivateMB,VirtualMB,HandleCount,ThreadCount' |
    Out-File -FilePath $OutFile -Encoding UTF8

$startTime = $proc.StartTime

while ($true) {
    $p = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
    if (-not $p) {
        Write-Host "PID $TargetPid exited. Final sample written. CSV: $OutFile"
        break
    }

    $now = Get-Date
    $uptimeMin = [math]::Round(($now - $startTime).TotalMinutes, 2)
    $wsMB      = [int]($p.WorkingSet64       / 1MB)
    $privMB    = [int]($p.PrivateMemorySize64 / 1MB)
    $vmMB      = [int]($p.VirtualMemorySize64 / 1MB)
    $handles   = $p.HandleCount
    $threads   = $p.Threads.Count

    $row = "{0},{1},{2},{3},{4},{5},{6},{7}" -f `
        $now.ToString('s'), $TargetPid, $uptimeMin, $wsMB, $privMB, $vmMB, $handles, $threads
    $row | Out-File -FilePath $OutFile -Append -Encoding UTF8

    # Echo to console too so a watching terminal shows live trend.
    Write-Host ("[{0}] up={1,7:0.00}min  WS={2,5}MB  Priv={3,5}MB  Handles={4,5}  Threads={5,3}" -f `
        $now.ToString('HH:mm:ss'), $uptimeMin, $wsMB, $privMB, $handles, $threads)

    Start-Sleep -Seconds $IntervalSeconds
}
