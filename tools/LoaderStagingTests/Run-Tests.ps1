# Builds and runs the offline staging checks against the real native/Loader/Loader.c (x86, MSVC):
# CleanStaleStagingFiles only deletes this client's older generations and closed clients' files,
# and CopyGuarded keeps the staged engine undeletable through the load.
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

$L   = (Resolve-Path (Join-Path $PSScriptRoot "..\..\native\Loader")).Path
$out = Join-Path $env:TEMP "LoaderStagingTests"
New-Item -ItemType Directory -Force $out | Out-Null
Push-Location $out
try {
    & $cl /nologo /O2 /MT /W4 /wd4100 /I $L (Join-Path $PSScriptRoot "test.c") "$L\Hooks.c" "$L\Clr.c" "$L\Services.c" user32.lib /Fe:test.exe
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & .\test.exe @args
    exit $LASTEXITCODE
} finally { Pop-Location }
