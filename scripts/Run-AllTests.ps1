#Requires -Version 7
<#
.SYNOPSIS
    Builds and runs every offline test program in RynthCore, RynthSuite and RynthServers. Exit 0 only
    when every one passes.

.DESCRIPTION
    The test programs are console apps: RynthCore tools\*Tests, RynthSuite and RynthServers Tools\*Tests,
    each run as `dotnet run -c Release` from its own folder (exit 0 = pass, anything else = fail), and the
    loader's C tests (a Run-Tests.ps1 beside test.c, built with MSVC). None needs a game client, a server
    or the network, and none writes to %APPDATA% or C:\Games (each project's header says what it reads).

    Publish-Update.ps1 runs this before it builds a release and refuses to publish when a test fails
    (2026-10-22: tests only ran when someone remembered to; a release went out with Missile Crafting
    broken while the tests that existed passed).

    Every folder whose name contains "Test" is run unless $Skip below names it with a reason; skipped ones
    are listed in the summary, never dropped silently. Each program's output goes to
    <LogDir>\<repo>.<name>.log, and the summary (also <LogDir>\summary.txt) shows each failure's last lines.

.EXAMPLE
    .\scripts\Run-AllTests.ps1 -RynthSuiteRoot C:\Projects\rel1002\RynthSuite
.EXAMPLE
    .\scripts\Run-AllTests.ps1 -Only RynthCore.RynthAiHostTests, ChatFilterTests
#>
param(
    [string]$RynthCoreRoot = "",      # defaults to this checkout
    [string]$RynthSuiteRoot = "",     # defaults to ..\RynthSuite beside RynthCore
    [string]$RynthServersRoot = "",   # defaults to ..\RynthServers beside RynthSuite
    [string[]]$Only = @(),            # folder names (or repo.name) to run; empty = all
    [string]$LogDir = "",             # defaults to %TEMP%\rynth-tests\<time>
    [int]$TimeoutMinutes = 10         # per program, build and run each
)

$ErrorActionPreference = "Stop"
if (-not $RynthCoreRoot) { $RynthCoreRoot = Split-Path $PSScriptRoot -Parent }
if (-not $RynthSuiteRoot) { $RynthSuiteRoot = Join-Path (Split-Path $RynthCoreRoot -Parent) "RynthSuite" }
if (-not $RynthServersRoot) { $RynthServersRoot = Join-Path (Split-Path $RynthSuiteRoot -Parent) "RynthServers" }
if (-not $LogDir) { $LogDir = Join-Path $env:TEMP ("rynth-tests\" + (Get-Date -Format "yyyyMMdd-HHmmss")) }
New-Item -ItemType Directory -Force $LogDir | Out-Null
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

# Folders with "Test" in the name that are not tests, and tests that can't run here. Each needs a reason.
$Skip = @{
    "HookResolver.SmokeTest" = "prints the hooks it resolves in acclient.exe and checks nothing (always exits 0)"
    "TestClient"             = "builds a stand-in acclient.exe for the reload scripts; not a test"
}

$repos = @(
    @{ Name = "RynthCore";    Root = $RynthCoreRoot;    Tools = "tools" },
    @{ Name = "RynthSuite";   Root = $RynthSuiteRoot;   Tools = "Tools" },
    @{ Name = "RynthServers"; Root = $RynthServersRoot; Tools = "Tools" }
)

$tests = [System.Collections.Generic.List[object]]::new()
$skipped = [System.Collections.Generic.List[object]]::new()
foreach ($r in $repos) {
    $dir = Join-Path $r.Root $r.Tools
    if (-not (Test-Path $dir)) { throw "No $($r.Tools) folder in $($r.Name) at $($r.Root)" }
    foreach ($d in Get-ChildItem $dir -Directory | Where-Object Name -like "*Test*" | Sort-Object Name) {
        $id = "$($r.Name).$($d.Name)"
        if ($Only.Count -gt 0 -and -not ($Only -contains $d.Name -or $Only -contains $id)) { continue }
        if ($Skip.ContainsKey($d.Name)) { $skipped.Add([pscustomobject]@{ Id = $id; Why = $Skip[$d.Name] }); continue }
        $proj = Get-ChildItem $d.FullName -Filter *.csproj | Select-Object -First 1
        $ps = Join-Path $d.FullName "Run-Tests.ps1"
        if ($proj) { $tests.Add([pscustomobject]@{ Id = $id; Dir = $d.FullName; Kind = "dotnet"; Path = $proj.FullName }) }
        elseif (Test-Path $ps) { $tests.Add([pscustomobject]@{ Id = $id; Dir = $d.FullName; Kind = "script"; Path = $ps }) }
        elseif (-not (Get-ChildItem $d.FullName -Force | Where-Object Name -notin "bin", "obj")) {
            # Git doesn't remove ignored build output, so a moved or deleted project leaves its bin\obj behind.
            $skipped.Add([pscustomobject]@{ Id = $id; Why = "only build output left (bin, obj): the project moved or was deleted" })
        }
        else { $tests.Add([pscustomobject]@{ Id = $id; Dir = $d.FullName; Kind = "none"; Path = "" }) }
    }
}
if ($Only.Count -gt 0 -and $tests.Count -eq 0 -and $skipped.Count -eq 0) { throw "-Only matched no test folder: $($Only -join ', ')" }

# Runs one program with its output captured; returns @{ Code; TimedOut; Output }.
function Invoke-Captured([string]$file, [string[]]$argList, [string]$cwd) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($file)
    foreach ($a in $argList) { $psi.ArgumentList.Add($a) }
    $psi.WorkingDirectory = $cwd
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = $true   # a program that waits for a key gets end-of-input, not a hang
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Close()
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    $timedOut = -not $p.WaitForExit($TimeoutMinutes * 60 * 1000)
    if ($timedOut) { try { $p.Kill($true) } catch { } ; $p.WaitForExit() }
    $p.WaitForExit()   # drains the readers
    $text = $out.Result
    if ($err.Result) { $text += "`n--- stderr ---`n" + $err.Result }
    return @{ Code = $p.ExitCode; TimedOut = $timedOut; Output = $text }
}

$results = [System.Collections.Generic.List[object]]::new()
$i = 0
foreach ($t in $tests) {
    $i++
    $log = Join-Path $LogDir "$($t.Id).log"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $status = "PASS"; $text = ""
    Write-Host ("[{0,2}/{1}] {2} ... " -f $i, $tests.Count, $t.Id) -NoNewline
    switch ($t.Kind) {
        "none" { $status = "FAIL"; $text = "no .csproj and no Run-Tests.ps1 in $($t.Dir) (add one, or list the folder in `$Skip with a reason)" }
        "script" {
            $r = Invoke-Captured "pwsh" @("-NoProfile", "-NonInteractive", "-File", $t.Path) $t.Dir
            $text = $r.Output
            if ($r.TimedOut) { $status = "TIMEOUT" } elseif ($r.Code -ne 0) { $status = "FAIL" }
        }
        "dotnet" {
            # The same as `dotnet run -c Release` from the project's folder, without dotnet run's
            # ~10 s of project evaluation per program: build, then start what was built. -t:Build is
            # needed: -getProperty alone only evaluates the project and builds nothing (an old build
            # would then be tested).
            $b = Invoke-Captured "dotnet" @("build", $t.Path, "-c", "Release", "-nologo", "-t:Build", "-getProperty:TargetPath") $t.Dir
            $text = "=== build ===`n" + $b.Output
            $dll = ($b.Output -split "`r?`n" | Where-Object { $_ -match '\.dll\s*$' } | Select-Object -Last 1)
            if ($b.TimedOut) { $status = "TIMEOUT (build)" }
            elseif ($b.Code -ne 0) { $status = "BUILD FAILED" }
            elseif (-not $dll -or -not (Test-Path $dll.Trim())) { $status = "BUILD FAILED"; $text += "`n(no built program found)" }
            else {
                $dll = $dll.Trim()
                $exe = [IO.Path]::ChangeExtension($dll, ".exe")
                $r = if (Test-Path $exe) { Invoke-Captured $exe @() $t.Dir } else { Invoke-Captured "dotnet" @($dll) $t.Dir }
                $text += "`n=== run ===`n" + $r.Output
                if ($r.TimedOut) { $status = "TIMEOUT" } elseif ($r.Code -ne 0) { $status = "FAIL (exit $($r.Code))" }
            }
        }
    }
    $sw.Stop()
    Set-Content $log $text
    $color = if ($status -eq "PASS") { "Green" } else { "Red" }
    Write-Host ("{0} ({1:0}s)" -f $status, $sw.Elapsed.TotalSeconds) -ForegroundColor $color
    $results.Add([pscustomobject]@{ Id = $t.Id; Status = $status; Seconds = [math]::Round($sw.Elapsed.TotalSeconds); Log = $log })
}

$failed = @($results | Where-Object Status -ne "PASS")
$summary = [System.Collections.Generic.List[string]]::new()
$summary.Add("RynthCore $(git -C $RynthCoreRoot rev-parse --short HEAD) ($RynthCoreRoot), RynthSuite $(git -C $RynthSuiteRoot rev-parse --short HEAD) ($RynthSuiteRoot), RynthServers $(git -C $RynthServersRoot rev-parse --short HEAD) ($RynthServersRoot)")
$summary.Add("$($results.Count - $failed.Count) of $($results.Count) test programs passed, $($failed.Count) failed, $($skipped.Count) skipped. Logs: $LogDir")
foreach ($s in $skipped) { $summary.Add("  skipped $($s.Id): $($s.Why)") }
foreach ($f in $failed) {
    $summary.Add("")
    $summary.Add("$($f.Status): $($f.Id)  ($($f.Log))")
    $tail = @(Get-Content $f.Log -Tail 15)
    foreach ($line in $tail) { $summary.Add("    $line") }
}
Set-Content (Join-Path $LogDir "summary.txt") $summary
Write-Host ""
$summary | ForEach-Object { Write-Host $_ }
exit [math]::Min($failed.Count, 1)
