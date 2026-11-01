# Builds and runs bench.c (x86, MSVC): one MinHook queued apply timed with and without the
# HIGH priority class the loader uses around each freeze, on a quiet machine and with every CPU
# busy above this process's LOWEST threads. Loads the machine for ~30 s at below-normal priority.
# Not part of Run-AllTests (no "Test" in the folder name): it measures, it doesn't pass or fail.
param([string]$MinHook = (Join-Path $PSScriptRoot "..\..\src\RynthCore.Engine\Native\minhook.x86.dll"))
$ErrorActionPreference = "Stop"
$vsPath = & "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcVer  = (Get-ChildItem "$vsPath\VC\Tools\MSVC" | Sort-Object Name -Descending | Select-Object -First 1).Name
$vcBase = "$vsPath\VC\Tools\MSVC\$vcVer"
$cl     = "$vcBase\bin\Hostx86\x86\cl.exe"
$sdkVer = (Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\Include" | Sort-Object Name -Descending | Select-Object -First 1).Name
$sdkInc = "C:\Program Files (x86)\Windows Kits\10\Include\$sdkVer"
$sdkLib = "C:\Program Files (x86)\Windows Kits\10\Lib\$sdkVer"
$env:INCLUDE = "$vcBase\include;$sdkInc\ucrt;$sdkInc\um;$sdkInc\shared"
$env:LIB     = "$vcBase\lib\x86;$sdkLib\ucrt\x86;$sdkLib\um\x86"

$out = Join-Path $env:TEMP "LoaderFreezeBench"
New-Item -ItemType Directory -Force $out | Out-Null
Push-Location $out
try {
    & $cl /nologo /O2 /MT /W4 /wd4100 (Join-Path $PSScriptRoot "bench.c") /Fe:bench.exe
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & .\bench.exe (Resolve-Path $MinHook).Path
    exit $LASTEXITCODE
} finally { Pop-Location }
