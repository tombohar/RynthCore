<#
.SYNOPSIS
    Rebuilds townnet.json: the Town Network's portals (world database) and the walks inside (dats).

.DESCRIPTION
    1. gen_townnet.py reads the portals from the world database (SELECT only, over ssh; or from a
       dump with -FromDump) and writes townnet.json.
    2. RynthSuite's Tools\RynthNav.TownNet adds a checked walk from every arrival point to every
       exit portal, from the retail dats (read only), then checks the file again.

    The live NavData folder is refused unless -ForceLive: copy the result there when it ships.

.EXAMPLE
    tools\navdata\Build-TownNet.ps1
    tools\navdata\Build-TownNet.ps1 -FromDump C:\Temp\tndump -Map C:\Temp\townnet.svg
#>
param(
    [string]$Out = (Join-Path $PSScriptRoot "NavData"),
    [string]$RynthSuite = (Join-Path $PSScriptRoot "..\..\..\RynthSuite"),
    [string]$Ac = "C:\Games\ACE\Dats",
    [string]$FromDump,
    [string]$Dump,
    [string]$Map,                 # also draw the floor plan with the walks (SVG)
    [switch]$ForceLive
)
$ErrorActionPreference = "Stop"

$outFull = [IO.Path]::GetFullPath($Out).TrimEnd('\')
if (-not $ForceLive -and $outFull -ieq "C:\Games\RynthCore\NavData") {
    throw "$outFull is the live NavData folder; pass -ForceLive to write there."
}
$tool = Join-Path $RynthSuite "Tools\RynthNav.TownNet\RynthNav.TownNet.csproj"
if (-not (Test-Path $tool)) { throw "RynthNav.TownNet not found at $tool (pass -RynthSuite <checkout>)" }

$py = @("$PSScriptRoot\gen_townnet.py", "--out", $outFull)
if ($FromDump) { $py += @("--from-dump", $FromDump) }
if ($Dump) { $py += @("--dump", $Dump) }
python @py
if ($LASTEXITCODE -ne 0) { throw "gen_townnet.py failed" }

$json = Join-Path $outFull "townnet.json"
dotnet run -c Release --project $tool -- build --in $json --ac $Ac
if ($LASTEXITCODE -ne 0) { throw "RynthNav.TownNet build: some walks failed (see above)" }
dotnet run -c Release --no-build --project $tool -- check --in $json --ac $Ac
if ($LASTEXITCODE -ne 0) { throw "RynthNav.TownNet check failed" }
if ($Map) { dotnet run -c Release --no-build --project $tool -- map --in $json --ac $Ac --out $Map }
Write-Host "townnet.json ready: $json"
