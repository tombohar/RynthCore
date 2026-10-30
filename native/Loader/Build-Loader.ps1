# Build-Loader.ps1
# Compiles the native RynthCore.Loader.dll (x86 MSVC) into native\Loader\bin.
# Does not deploy: copy it to Runtime\ yourself (a running client keeps the old loader
# until it restarts; the loader is injected once per process).

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

$src = $PSScriptRoot
$out = Join-Path $src "bin"
$obj = Join-Path $out "obj"
New-Item -ItemType Directory -Path $out, $obj -Force | Out-Null

Write-Host "Compiling RynthCore.Loader.dll (MSVC $vcVer, x86)..."
Push-Location $obj
try {
    & $cl /nologo /O2 /MT /GS /W4 /wd4100 /LD /Zi "$src\Loader.c" "$src\Hooks.c" "$src\Clr.c" "$src\Services.c" user32.lib `
        /link /MACHINE:X86 /DEF:"$src\Loader.def" /DEBUG /OPT:REF /OUT:"$out\RynthCore.Loader.dll" /PDB:"$out\RynthCore.Loader.pdb"
    if ($LASTEXITCODE -ne 0) { Write-Error "Compilation failed."; exit $LASTEXITCODE }
} finally { Pop-Location }

$size = (Get-Item "$out\RynthCore.Loader.dll").Length
Write-Host "Built: $out\RynthCore.Loader.dll ($size bytes)"
