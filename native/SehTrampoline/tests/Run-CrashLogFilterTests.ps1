# Run-CrashLogFilterTests.ps1
# Builds and runs CrashLogFilterTests.c (x86 MSVC, the same toolchain as the DLL).
# -OutDir: where the test exe goes (default: native\SehTrampoline\tests\bin). Exit 0 = pass.

param([string]$OutDir = (Join-Path $PSScriptRoot "bin"))

$ErrorActionPreference = "Stop"

$vsPath = & "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath 2>$null
if (-not $vsPath) { Write-Error "Visual Studio with C++ tools not found."; exit 1 }

$vcVer  = (Get-ChildItem "$vsPath\VC\Tools\MSVC" | Sort-Object Name -Descending | Select-Object -First 1).Name
$vcBase = "$vsPath\VC\Tools\MSVC\$vcVer"
$cl     = "$vcBase\bin\Hostx86\x86\cl.exe"
$sdkVer = (Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\Include" | Sort-Object Name -Descending | Select-Object -First 1).Name
$sdkInc = "C:\Program Files (x86)\Windows Kits\10\Include\$sdkVer"
$sdkLib = "C:\Program Files (x86)\Windows Kits\10\Lib\$sdkVer"
$env:INCLUDE = "$vcBase\include;$sdkInc\ucrt;$sdkInc\um;$sdkInc\shared"
$env:LIB     = "$vcBase\lib\x86;$sdkLib\ucrt\x86;$sdkLib\um\x86"

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$exe = Join-Path $OutDir "CrashLogFilterTests.exe"
& $cl /nologo /W4 /WX "$PSScriptRoot\CrashLogFilterTests.c" /Fo"$OutDir\\" /Fe"$exe" /link /MACHINE:X86
if ($LASTEXITCODE -ne 0) { Write-Error "Compilation failed."; exit $LASTEXITCODE }
& $exe
exit $LASTEXITCODE
