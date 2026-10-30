# Builds and runs the offline checks for the loader's reload cap and CoreCLR crash-dump setup
# against the real native/Loader/Loader.c (x86, MSVC): a client that has hot reloaded MaxHotReloads
# times refuses the next reload without shutting down, unloading or staging anything, and tells
# the engine; the dump environment (DOTNET_DbgEnableMiniDump/Type/Name) is set unless already set;
# only the newest 3 clr_*.dmp are kept. Builds and runs in %TEMP%\LoaderReloadCapTests; touches
# nothing under C:\Games.
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
$out = Join-Path $env:TEMP "LoaderReloadCapTests"
New-Item -ItemType Directory -Force $out | Out-Null
Push-Location $out
try {
    foreach ($d in "work", "logs") { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
    # The exe plays the engine: its two fake exports, undecorated, as the engine's are.
    & $cl /nologo /O2 /MT /W4 /wd4100 /I $L (Join-Path $PSScriptRoot "test.c") "$L\Hooks.c" "$L\Clr.c" "$L\Services.c" user32.lib /Fe:test.exe `
        /link "/EXPORT:RynthCoreMaxHotReloads=_FakeMaxHotReloads@0" "/EXPORT:RynthCoreReloadCapped=_FakeReloadCapped@8"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & .\test.exe @args
    exit $LASTEXITCODE
} finally { Pop-Location }
