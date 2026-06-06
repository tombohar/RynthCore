<#
.SYNOPSIS
  Pre-deploy / CI gate: verify every acclient.exe address pattern embedded in the engine
  source still resolves uniquely and correctly against the target client binary.

  Exits 0 if all patterns verify, non-zero if any drifted (binary changed) or is wrong
  (transcription typo). Wire this into the deploy script or CI BEFORE publishing the engine,
  so a future AC patch / ACE rebuild can't silently ship a hook that falls back to a stale VA.

.PARAMETER AcClient
  Path to the acclient.exe to verify against. Defaults to the fingerprinted reference client.
  Point it at whatever binary the engine will actually be injected into.

.EXAMPLE
  pwsh tools\check-patterns.ps1
  pwsh tools\check-patterns.ps1 -AcClient "C:\Games\RynthCore\AcClient\acclient.exe"
#>
param(
    [string]$AcClient = "C:\Turbine\Asheron's Call\acclient.exe"
)

if (-not (Test-Path -LiteralPath $AcClient)) {
    Write-Error "acclient.exe not found at: $AcClient"
    exit 2
}

python "$PSScriptRoot\pe_pattern.py" $AcClient CHECK
exit $LASTEXITCODE
