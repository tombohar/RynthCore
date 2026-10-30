#Requires -Version 7
<#
.SYNOPSIS
    Checks installer\PluginManifest.ps1 (what Publish-Update.ps1 uses to put minEngineApi in the feed)
    against plugin DLLs: it must read the same manifest the engine and launcher read.

.EXAMPLE
    .\Test-PluginManifestPs.ps1 -Dll C:\Projects\manifest\_pub\RynthChat\RynthCore.Plugin.RynthChat.dll
    Exit 0 = pass. Read-only: maps the DLLs as data files, runs nothing from them.
#>
param([string[]]$Dll = @(), [string[]]$NoManifestDll = @())

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
. (Join-Path $repo "installer\PluginManifest.ps1")
$fails = 0
function Check($cond, $msg) { if ($cond) { Write-Host "ok   $msg" } else { Write-Host "FAIL $msg"; $script:fails++ } }

$api = Get-RynthEngineApi $repo
$src = Get-Content (Join-Path $repo "src\RynthCore.Engine\Plugins\PluginContractVersion.cs") -Raw
Check ($api -gt 0 -and $src -match "Current = $api;") "Get-RynthEngineApi reads PluginContractVersion.Current ($api)"

foreach ($d in $Dll) {
    $m = Get-RynthPluginManifest $d
    Check ($null -ne $m) "$(Split-Path $d -Leaf): manifest found"
    if (-not $m) { continue }
    $expected = [IO.Path]::GetFileNameWithoutExtension($d) -replace '^RynthCore\.Plugin\.', ''
    Check ($m.name -eq $expected) "$(Split-Path $d -Leaf): name '$($m.name)'"
    Check ([int]$m.minEngineApi -ge 66 -and [int]$m.minEngineApi -le $api) "$(Split-Path $d -Leaf): minEngineApi $($m.minEngineApi) within 66..$api"
}
foreach ($d in $NoManifestDll) {
    Check ($null -eq (Get-RynthPluginManifest $d)) "$(Split-Path $d -Leaf): no manifest -> `$null"
}

if ($fails -gt 0) { Write-Host "FAIL ($fails)"; exit 1 }
Write-Host "PASS"
exit 0
